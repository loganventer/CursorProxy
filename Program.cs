using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

// ===== JSON OPTIONS =====
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// ===== CONFIG =====
var ollamaBase = Environment.GetEnvironmentVariable("OLLAMA_BASE") ?? "http://192.168.0.100:11434";
var listenAddr = Environment.GetEnvironmentVariable("PROXY_LISTEN") ?? "http://0.0.0.0:8080";
var debug = (Environment.GetEnvironmentVariable("PROXY_DEBUG") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);

// ===== UTILS =====
string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
void Log(string msg)
{
    if (!debug) return;
    Console.WriteLine($"[{Now()}] {msg}");
}
string SafeBodyPreview(string? s, int max = 500)
{
    if (string.IsNullOrEmpty(s)) return "";
    s = s.Replace("\n", " ").Replace("\r", " ");
    return s.Length <= max ? s : s.Substring(0, max) + "...";
}

// ===== CORS (simple for dev) =====
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
    ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
    if (ctx.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = 204;
        return;
    }
    await next();
});

// ===== HEALTH =====
app.MapGet("/healthz", () => Results.Ok(new { ok = true, upstream = ollamaBase }));

// ===== /v1/models  ->  /api/tags =====
app.MapGet("/v1/models", async () =>
{
    var sw = Stopwatch.StartNew();
    Log("IN  /v1/models");
    using var http = new HttpClient();
    var url = $"{ollamaBase}/api/tags";
    try
    {
        var res = await http.GetAsync(url);
        var txt = await res.Content.ReadAsStringAsync();
        Log($"OUT GET {url}  status={(int)res.StatusCode}  {sw.ElapsedMilliseconds}ms  body={txt.Length}B");

        if (!res.IsSuccessStatusCode)
            return Results.Problem($"Ollama /api/tags error: {(int)res.StatusCode} {SafeBodyPreview(txt)}", statusCode: 502);

        var parsed = JsonNode.Parse(txt)!.AsObject();
        var arr = parsed["models"]!.AsArray();

        var data = new JsonArray();
        foreach (var m in arr)
        {
            var name = m!["name"]!.GetValue<string>(); // e.g. "mistral:7b"
            data.Add(new JsonObject
            {
                ["id"] = name,
                ["object"] = "model",
                ["owned_by"] = "ollama"
            });
        }
        var payload = new { @object = "list", data };
        return Results.Json(payload);
    }
    catch (Exception ex)
    {
        Log($"ERR /v1/models -> {url}  ex={ex.Message}");
        return Results.Problem($"Proxy error: {ex.Message}", statusCode: 502);
    }
});

// ===== /v1/chat/completions  ->  /api/chat  (non-stream) =====
app.MapPost("/v1/chat/completions", async (HttpRequest req) =>
{
    var sw = Stopwatch.StartNew();
    string bodyText;
    using (var reader = new StreamReader(req.Body, Encoding.UTF8))
        bodyText = await reader.ReadToEndAsync();

    Log($"IN  /v1/chat/completions  bytes={bodyText.Length}  preview={SafeBodyPreview(bodyText)}");

    JsonNode? body;
    try { body = JsonNode.Parse(bodyText); }
    catch (Exception ex)
    {
        Log($"ERR bad JSON /v1/chat/completions: {ex.Message}");
        return Results.BadRequest("Invalid JSON");
    }

    var model = body?["model"]?.GetValue<string>() ?? "mistral:7b";
    var messages = body?["messages"] ?? new JsonArray();
    var temperature = body?["temperature"]?.GetValue<double?>() ?? 0.2;
    var topP = body?["top_p"]?.GetValue<double?>() ?? 0.9;
    var maxTokens = body?["max_tokens"]?.GetValue<int?>() ?? 512;
    var stream = body?["stream"]?.GetValue<bool?>() ?? false; // we ignore streaming below

    var ollamaPayload = new
    {
        model,
        messages,
        stream = false, // keep simple; add streaming later if needed
        options = new
        {
            temperature = temperature,
            top_p = topP,
            num_ctx = 16384,     // tune per your machine/model if you want
            num_predict = maxTokens
        }
    };

    var url = $"{ollamaBase}/api/chat";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var res = await http.PostAsJsonAsync(url, ollamaPayload);
        var txt = await res.Content.ReadAsStringAsync();
        Log($"OUT POST {url}  model={model}  status={(int)res.StatusCode}  {sw.ElapsedMilliseconds}ms  body={txt.Length}B");

        if (!res.IsSuccessStatusCode)
            return Results.Problem($"Ollama /api/chat error: {(int)res.StatusCode} {SafeBodyPreview(txt)}", statusCode: 502);

        var data = JsonNode.Parse(txt)!;
        var content = data?["message"]?["content"]?.GetValue<string>() ?? "";

        var openAiLike = new
        {
            id = $"chatcmpl-ollama-proxy-{Guid.NewGuid()}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new {
                    index = 0,
                    message = new { role = "assistant", content },
                    finish_reason = "stop"
                }
            }
        };
        return Results.Json(openAiLike);
    }
    catch (Exception ex)
    {
        Log($"ERR /v1/chat/completions -> {url}  ex={ex.Message}");
        return Results.Problem($"Proxy error: {ex.Message}", statusCode: 502);
    }
});

// ===== /v1/embeddings  ->  /api/embeddings =====
// OpenAI-style request: { "model":"...", "input": "text" | ["t1","t2"] }
// Ollama expects: { "model":"...", "input":"text" } and returns: { "embedding":[...] }
app.MapPost("/v1/embeddings", async (HttpRequest req) =>
{
    var sw = Stopwatch.StartNew();
    string bodyText;
    using (var reader = new StreamReader(req.Body, Encoding.UTF8))
        bodyText = await reader.ReadToEndAsync();

    Log($"IN  /v1/embeddings  bytes={bodyText.Length}  preview={SafeBodyPreview(bodyText)}");

    JsonNode? body;
    try { body = JsonNode.Parse(bodyText); }
    catch (Exception ex)
    {
        Log($"ERR bad JSON /v1/embeddings: {ex.Message}");
        return Results.BadRequest("Invalid JSON");
    }

    var model = body?["model"]?.GetValue<string>() ?? "mistral:7b";
    var inputNode = body?["input"];
    if (inputNode is null)
        return Results.BadRequest("Missing 'input'");

    // Normalize to array of strings to mimic OpenAI behavior
    List<string> inputs = new();
    if (inputNode is JsonArray jarr)
    {
        foreach (var it in jarr)
            if (it is not null) inputs.Add(it.ToString());
    }
    else
    {
        inputs.Add(inputNode.ToString());
    }

    var url = $"{ollamaBase}/api/embeddings";
    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

    try
    {
        var dataList = new List<object>();
        int index = 0;

        foreach (var text in inputs)
        {
            var payload = new { model, input = text };
            var res = await http.PostAsJsonAsync(url, payload);
            var txt = await res.Content.ReadAsStringAsync();
            Log($"OUT POST {url}  model={model}  idx={index}  status={(int)res.StatusCode}  {sw.ElapsedMilliseconds}ms  body={txt.Length}B");

            if (!res.IsSuccessStatusCode)
                return Results.Problem($"Ollama /api/embeddings error: {(int)res.StatusCode} {SafeBodyPreview(txt)}", statusCode: 502);

            var parsed = JsonNode.Parse(txt)!;
            var emb = parsed["embedding"]?.AsArray();
            if (emb is null)
                return Results.Problem("Invalid response from Ollama: missing 'embedding'", statusCode: 502);

            dataList.Add(new
            {
                @object = "embedding",
                embedding = emb,
                index = index++
            });
        }

        var openAiEmb = new
        {
            @object = "list",
            data = dataList,
            model,
            usage = new { prompt_tokens = 0, total_tokens = 0 } // Ollama does not return tokens here; placeholders
        };
        return Results.Json(openAiEmb);
    }
    catch (Exception ex)
    {
        Log($"ERR /v1/embeddings -> {url}  ex={ex.Message}");
        return Results.Problem($"Proxy error: {ex.Message}", statusCode: 502);
    }
});

// ===== START =====
Log($"Starting Ollama OpenAI proxy on {listenAddr}  →  upstream={ollamaBase}");
app.Run(listenAddr);
