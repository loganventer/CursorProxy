// Program.cs — OpenAI-compatible proxy → Ollama
// dotnet new web -n OllamaProxy && replace Program.cs with this file
// RUN:
//   Windows PS:
//     $env:OLLAMA_BASE="http://192.168.0.100:11434"
//     $env:PROXY_LISTEN="http://0.0.0.0:8080"
//     dotnet run
// TEST:
//   curl http://localhost:8080/v1/models
//   curl http://localhost:8080/v1/chat/completions -H "Content-Type: application/json" -d "{ \"model\":\"mistral:7b\", \"messages\":[{\"role\":\"user\",\"content\":\"ping via proxy\"}] }"

using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

// JSON opts
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// ---------- Config ----------
var ollamaBase = Environment.GetEnvironmentVariable("OLLAMA_BASE") ?? "http://192.168.100:11434"; // bv. http://192.168.0.100:11434
var listenAddr = Environment.GetEnvironmentVariable("PROXY_LISTEN") ?? "http://0.0.0.0:8080";

// Basic CORS (local dev)
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
    ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
    ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
    if (ctx.Request.Method == "OPTIONS") { ctx.Response.StatusCode = 204; return; }
    await next();
});

// Simple health
app.MapGet("/healthz", () => Results.Ok(new { ok = true, upstream = ollamaBase }));

// ---------- Helpers ----------
static (double temp, double topP, int maxTok, bool stream) ReadGenParams(JsonNode? body)
{
    double temperature = body?["temperature"]?.GetValue<double?>() ?? 0.2;
    double topP       = body?["top_p"]?.GetValue<double?>()       ?? 0.9;
    int maxTokens     = body?["max_tokens"]?.GetValue<int?>()     ?? 512;
    bool stream       = body?["stream"]?.GetValue<bool?>()        ?? false;
    return (temperature, topP, maxTokens, stream);
}

static int PickNumCtx(string model)
{
    // Reasonable defaults vir 16GB Mac; pas soos jy wil
    if (model.StartsWith("mistral", StringComparison.OrdinalIgnoreCase)) return 16384;
    if (model.StartsWith("llama3",  StringComparison.OrdinalIgnoreCase)) return 16384;
    if (model.StartsWith("gemma",   StringComparison.OrdinalIgnoreCase)) return 16384;
    if (model.StartsWith("phi3",    StringComparison.OrdinalIgnoreCase)) return 8192;
    if (model.StartsWith("qwen2",   StringComparison.OrdinalIgnoreCase)) return 8192;
    return 8192;
}

static string NormalizeModel(string model)
{
    if (string.IsNullOrWhiteSpace(model)) return "mistral:7b";
    model = model.Trim();

    // Shortcuts
    if (model.Equals("mistral", StringComparison.OrdinalIgnoreCase)) return "mistral:7b";
    if (model.Equals("llama3",  StringComparison.OrdinalIgnoreCase)) return "llama3:8b";
    if (model.Equals("gemma",   StringComparison.OrdinalIgnoreCase)) return "gemma:7b";
    if (model.Equals("phi3",    StringComparison.OrdinalIgnoreCase)) return "phi3:mini";

    // :latest → kies ’n sinvolle default tag
    if (model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
    {
        var baseName = model[..model.IndexOf(':')];
        return baseName.ToLower() switch
        {
            "mistral" => "mistral:7b",
            "llama3"  => "llama3:8b",
            "gemma"   => "gemma:7b",
            "phi3"    => "phi3:mini",
            _ => model
        };
    }
    return model;
}

static void LogIn(string route, string payloadPreview, string model)
{
    Console.WriteLine($"[IN ] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {route} model={model} preview={payloadPreview}");
}

static void LogOut(string method, string url, string model, int status, long ms, string bodyPreview)
{
    Console.WriteLine($"[OUT] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {method} {url} model={model} status={status} {ms}ms body={bodyPreview}");
}

static string Preview(string s, int max = 160)
{
    s ??= "";
    s = s.Replace("\n", "\\n");
    return s.Length <= max ? s : s[..max] + "...";
}

// ---------- /v1/models ----------
app.MapGet("/v1/models", async () =>
{
    using var http = new HttpClient();
    var url = $"{ollamaBase}/api/tags";
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var res = await http.GetAsync(url);
    var body = await res.Content.ReadAsStringAsync();
    sw.Stop();
    LogOut("GET", url, "-", (int)res.StatusCode, sw.ElapsedMilliseconds, Preview(body));

    if (!res.IsSuccessStatusCode)
        return Results.Problem($"Ollama /api/tags error: {(int)res.StatusCode} {body}", statusCode: 502);

    var parsed = JsonNode.Parse(body)!.AsObject();
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

    return Results.Json(new { @object = "list", data });
});

// ---------- /v1/chat/completions ----------
app.MapPost("/v1/chat/completions", async (HttpRequest req, HttpResponse resp) =>
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var bodyNode = await JsonNode.ParseAsync(req.Body);
    if (bodyNode is null) { resp.StatusCode = 400; await resp.WriteAsync("Invalid JSON"); return; }

    string model = NormalizeModel(bodyNode["model"]?.GetValue<string>() ?? "mistral:7b");
    var messages = bodyNode["messages"] ?? new JsonArray();
    var (temperature, topP, maxTokens, stream) = ReadGenParams(bodyNode);

    var payloadObj = new
    {
        model,
        messages,
        stream,
        options = new
        {
            temperature,
            top_p = topP,
            num_ctx = PickNumCtx(model),
            num_predict = maxTokens
        }
    };

    var payload = JsonSerializer.Serialize(payloadObj);
    LogIn("/v1/chat/completions", Preview(payload, 400), model);

    var url = $"{ollamaBase}/api/chat";
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var upstream = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
    var upTxt = await upstream.Content.ReadAsStringAsync();
    sw.Stop();
    LogOut("POST", url, model, (int)upstream.StatusCode, sw.ElapsedMilliseconds, Preview(upTxt, 200));

    if (!upstream.IsSuccessStatusCode)
    {
        resp.StatusCode = 502;
        await resp.WriteAsync($"Ollama /api/chat error: {(int)upstream.StatusCode} {upTxt}");
        return;
    }

    if (!stream)
    {
        // Non-stream: lees heel JSON, map na OpenAI shape
        var data = JsonNode.Parse(upTxt)!;
        var content = data["message"]?["content"]?.GetValue<string>() ?? "";
        var openai = new
        {
            id = "chatcmpl-ollama-proxy",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } }
        };
        await resp.WriteAsJsonAsync(openai);
        return;
    }

    // Stream mode: stream Ollama se JSONL na OpenAI sse-delta's
    resp.StatusCode = 200;
    resp.Headers.ContentType = "text/event-stream";

    await using var src = await upstream.Content.ReadAsStreamAsync();
    var buffer = ArrayPool<byte>.Shared.Rent(8192);
    try
    {
        var sb = new StringBuilder();
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
            // Ollama stuur newline-delimited JSON. Parse per line:
            while (true)
            {
                var str = sb.ToString();
                var idx = str.IndexOf('\n');
                if (idx < 0) break;

                var line = str[..idx].Trim();
                sb.Remove(0, idx + 1);
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var node = JsonNode.Parse(line);
                    var deltaText = node?["message"]?["content"]?.GetValue<string>() ?? "";
                    var done      = node?["done"]?.GetValue<bool?>() ?? false;

                    if (!string.IsNullOrEmpty(deltaText))
                    {
                        var ev = new
                        {
                            id = "chatcmpl-ollama-proxy",
                            @object = "chat.completion.chunk",
                            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            model,
                            choices = new[] {
                                new {
                                    index = 0,
                                    delta = new { role = (string?)null, content = deltaText },
                                    finish_reason = (string?)null
                                }
                            }
                        };
                        await resp.WriteAsync($"data: {JsonSerializer.Serialize(ev)}\n\n");
                        await resp.Body.FlushAsync();
                    }

                    if (done)
                    {
                        await resp.WriteAsync("data: [DONE]\n\n");
                        await resp.Body.FlushAsync();
                    }
                }
                catch
                {
                    // ignore parse glitches in stream
                }
            }
        }
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
});

// ---------- /v1/completions (legacy text) ----------
app.MapPost("/v1/completions", async (HttpRequest req) =>
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var bodyNode = await JsonNode.ParseAsync(req.Body);
    if (bodyNode is null) return Results.BadRequest("Invalid JSON");

    string model = NormalizeModel(bodyNode["model"]?.GetValue<string>() ?? "mistral:7b");
    string prompt = bodyNode["prompt"]?.GetValue<string>() ?? "";
    var (temperature, topP, maxTokens, stream) = ReadGenParams(bodyNode);

    var payloadObj = new
    {
        model,
        prompt,
        stream,
        options = new
        {
            temperature,
            top_p = topP,
            num_ctx = PickNumCtx(model),
            num_predict = maxTokens
        }
    };
    var payload = JsonSerializer.Serialize(payloadObj);
    LogIn("/v1/completions", Preview(payload, 400), model);

    var url = $"{ollamaBase}/api/generate";
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var res = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
    var txt = await res.Content.ReadAsStringAsync();
    sw.Stop();
    LogOut("POST", url, model, (int)res.StatusCode, sw.ElapsedMilliseconds, Preview(txt, 200));

    if (!res.IsSuccessStatusCode)
        return Results.Problem($"Ollama /api/generate error: {(int)res.StatusCode} {txt}", statusCode: 502);

    if (!stream)
    {
        var node = JsonNode.Parse(txt)!;
        var txtOut = node?["response"]?.GetValue<string>() ?? "";
        return Results.Json(new
        {
            id = "cmpl-ollama-proxy",
            @object = "text_completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[] { new { index = 0, text = txtOut, finish_reason = "stop" } }
        });
    }

    // Optional: implement SSE streaming vir /v1/completions ook
    return Results.Problem("stream=true vir /v1/completions nie geïmplementeer nie; gebruik /v1/chat/completions vir streaming.", statusCode: 501);
});

// ---------- /v1/embeddings ----------
app.MapPost("/v1/embeddings", async (HttpRequest req) =>
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var bodyNode = await JsonNode.ParseAsync(req.Body);
    if (bodyNode is null) return Results.BadRequest("Invalid JSON");

    string model = NormalizeModel(bodyNode["model"]?.GetValue<string>() ?? "mistral:7b");

    // OpenAI: input kan string of array wees → normaliseer na array<string>
    var inputNode = bodyNode["input"];
    List<string> inputs = new();
    if (inputNode is null) inputs.Add("");
    else if (inputNode is JsonArray arr)
        inputs.AddRange(arr.Select(x => x?.GetValue<string>() ?? ""));
    else
        inputs.Add(inputNode.GetValue<string>());

    var payloadObj = new { model, input = inputs };
    var payload = JsonSerializer.Serialize(payloadObj);
    LogIn("/v1/embeddings", Preview(payload, 200), model);

    var url = $"{ollamaBase}/api/embeddings";
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var res = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
    var txt = await res.Content.ReadAsStringAsync();
    sw.Stop();
    LogOut("POST", url, model, (int)res.StatusCode, sw.ElapsedMilliseconds, Preview(txt, 200));

    if (!res.IsSuccessStatusCode)
        return Results.Problem($"Ollama /api/embeddings error: {(int)res.StatusCode} {txt}", statusCode: 502);

    // Verskillende Ollama builds gee {embedding:[...]} of {embeddings:[[...],[...]]}
    var node = JsonNode.Parse(txt)!;
    var single = node?["embedding"];
    var multi  = node?["embeddings"];

    var data = new JsonArray();
    if (single is not null)
    {
        data.Add(new JsonObject { ["index"] = 0, ["embedding"] = single });
    }
    else if (multi is JsonArray marr)
    {
        for (int i = 0; i < marr.Count; i++)
            data.Add(new JsonObject { ["index"] = i, ["embedding"] = marr[i] });
    }
    else
    {
        // Fallback—party returns het ander shape
        data.Add(new JsonObject { ["index"] = 0, ["embedding"] = node?["data"]?["embedding"] ?? new JsonArray() });
    }

    return Results.Json(new
    {
        @object = "list",
        data,
        model,
        usage = new { prompt_tokens = 0, total_tokens = 0 } // OpenAI-compatible
    });
});

app.Run(listenAddr);
