using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);

// Optional: add CORS so jy kan ook uit browser of tools call
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();
app.UseRouting();

var http = new HttpClient();

// ---------- CHAT COMPLETIONS ----------
app.MapPost("/v1/chat/completions", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    var root = JsonNode.Parse(body)!.AsObject();

    // Convert array content → string (Ollama expects plain text)
    foreach (var msg in root["messages"]!.AsArray())
    {
        if (msg?["content"] is JsonArray arr)
        {
            var joined = string.Join("\n", arr
                .Where(e => e?["type"]?.ToString() == "text")
                .Select(e => e?["text"]?.ToString() ?? "")
            );
            msg["content"] = joined;
        }
    }

    var ollamaReq = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json");
    var resp = await http.PostAsync("http://192.168.0.100:11434/api/chat", ollamaReq);

    context.Response.StatusCode = (int)resp.StatusCode;
    await resp.Content.CopyToAsync(context.Response.Body);
});

// ---------- EMBEDDINGS ----------
app.MapPost("/v1/embeddings", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    var root = JsonNode.Parse(body)!.AsObject();
    var ollamaReq = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json");
    var resp = await http.PostAsync("http://192.168.0.100:11434/api/embeddings", ollamaReq);

    context.Response.StatusCode = (int)resp.StatusCode;
    await resp.Content.CopyToAsync(context.Response.Body);
});

// ---------- MODELS ----------
app.MapGet("/v1/models", async (HttpContext context) =>
{
    var resp = await http.GetAsync("http://192.168.0.100:11434/api/tags");
    context.Response.StatusCode = (int)resp.StatusCode;
    await resp.Content.CopyToAsync(context.Response.Body);
});

app.Run("http://0.0.0.0:8080");
