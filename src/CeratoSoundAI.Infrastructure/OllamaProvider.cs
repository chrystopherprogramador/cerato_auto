using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CeratoSoundAI.Domain;

namespace CeratoSoundAI.Infrastructure.Llm;

public sealed class OllamaOptions
{
    /// <summary>https://ollama.com (direct, user token) or a Samos backend URL that holds the credential.</summary>
    public string BaseUrl { get; set; } = "https://ollama.com";
    /// <summary>Provided at runtime by the user (secure storage). Never compiled into the APK.</summary>
    public string? ApiKey { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed class OllamaProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly Func<OllamaOptions> _options;

    public OllamaProvider(HttpClient http, Func<OllamaOptions> options) { _http = http; _options = options; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options().BaseUrl) &&
        (!string.IsNullOrWhiteSpace(_options().ApiKey) || !_options().BaseUrl.Contains("ollama.com", StringComparison.OrdinalIgnoreCase));

    private HttpRequestMessage Req(HttpMethod m, string path, string? body = null)
    {
        var o = _options();
        var r = new HttpRequestMessage(m, o.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(o.ApiKey)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        if (body != null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }

    public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options().Timeout);
        using var resp = await _http.SendAsync(Req(HttpMethod.Get, "/api/tags"), cts.Token);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        return ParseModels(doc.RootElement);
    }

    public static IReadOnlyList<LlmModel> ParseModels(JsonElement root)
    {
        var list = new List<LlmModel>();
        if (root.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray())
            {
                var n = m.TryGetProperty("name", out var x) ? x.GetString() : m.TryGetProperty("model", out var y) ? y.GetString() : null;
                if (!string.IsNullOrWhiteSpace(n)) list.Add(new LlmModel(n!));
            }
        return list;
    }

    public async Task<string> ChatAsync(string model, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model, stream = false,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            options = new { temperature = 0.2 }
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options().Timeout);
        using var resp = await _http.SendAsync(Req(HttpMethod.Post, "/api/chat", body), cts.Token);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
