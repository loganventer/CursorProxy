using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var http = new HttpClient();

app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    // Parse JSON
    var json = JsonNode.Parse(body)!.AsObject();

    var messages = json["messages"]!.AsArray();

    // Convert OpenAI-style messages into Ollama format
    var ollamaMessages = new JsonArray();
    foreach (var msg in messages)
    {
        var role = msg!["role"]!.ToString();
        var contentNode = msg!["content"];

        string content;
        if (contentNode is JsonArray arr)
        {
            // Flatten array into a single string
            content = string.Join("\n", arr.Select(a => a!["text"]!.ToString()));
        }
        else
        {
            content = contentNode!.ToString();
        }

        ollamaMessages.Add(new JsonObject
        {
            ["role"] = role,
            ["content"] = content
        });
    }

    var ollamaRequest = new JsonObject
    {
        ["model"] = json["model"]!.ToString(),
        ["messages"] = ollamaMessages
    };

    var ollamaJson = ollamaRequest.ToJsonString();

    var res = await http.PostAsync("http://192.168.0.100:11434/api/chat", new StringContent(ollamaJson, System.Text.Encoding.UTF8, "application/json"));
    var result = await res.Content.ReadAsStringAsync();

    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(result);
});

app.Run("http://0.0.0.0:8080");
