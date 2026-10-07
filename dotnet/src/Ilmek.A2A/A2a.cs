using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Ilmek.A2A;

/// <summary>
/// The A2A transport port: fetch the Agent Card, post one JSON-RPC request. Two
/// calls, so a real HTTP client (<see cref="HttpA2aTransport"/>) and a scripted
/// fake look the same. Messages are plain dictionaries — the shape
/// <see cref="PlainJson"/> reads and writes.
/// </summary>
public interface IA2aTransport
{
    Task<IReadOnlyDictionary<string, object?>> GetAgentCardAsync(CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>> PostAsync(IReadOnlyDictionary<string, object?> request, CancellationToken ct = default);
}

/// <summary>The remote agent answered with a JSON-RPC error.</summary>
public sealed class A2aException(int code, string message, object? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public object? Data2 { get; } = data;
}

/// <summary>
/// The HTTP transport: the card from <c>&lt;origin&gt;/.well-known/agent-card.json</c>
/// (or the given card URL), JSON-RPC posted to the card's <c>url</c> (or the given
/// endpoint). Mirror of TypeScript's <c>HttpA2aTransport</c>.
/// </summary>
public sealed class HttpA2aTransport : IA2aTransport
{
    /// <summary>The default Agent Card location, relative to an agent's origin.</summary>
    public const string AgentCardPath = "/.well-known/agent-card.json";

    private readonly HttpClient _http;
    private readonly Uri _cardUrl;
    private Uri? _endpoint;

    /// <param name="http">The client to use (bring your own auth headers).</param>
    /// <param name="agentUrl">The agent's origin, its card URL (ending in <c>.json</c>), or its JSON-RPC endpoint.</param>
    public HttpA2aTransport(HttpClient http, string agentUrl)
    {
        _http = http;
        var url = new Uri(agentUrl);
        if (url.AbsolutePath.EndsWith(".json", StringComparison.Ordinal))
        {
            _cardUrl = url;
        }
        else
        {
            _cardUrl = new Uri(new Uri(url.GetLeftPart(UriPartial.Authority)), AgentCardPath);
            if (url.AbsolutePath is not ("/" or "")) _endpoint = url;
        }
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetAgentCardAsync(CancellationToken ct = default)
    {
        using var res = await _http.GetAsync(_cardUrl, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"agent card {_cardUrl}: HTTP {(int)res.StatusCode}");
        var card = PlainJson.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as IReadOnlyDictionary<string, object?>
            ?? throw new InvalidOperationException("agent card is not a JSON object");
        if (_endpoint is null && card.GetValueOrDefault("url") is string u) _endpoint = new Uri(u);
        return card;
    }

    public async Task<IReadOnlyDictionary<string, object?>> PostAsync(IReadOnlyDictionary<string, object?> request, CancellationToken ct = default)
    {
        if (_endpoint is null) await GetAgentCardAsync(ct).ConfigureAwait(false);
        if (_endpoint is null)
            throw new InvalidOperationException(
                $"A2A {request.GetValueOrDefault("method")}: the agent card at {_cardUrl} has no url and no endpoint was given");
        using var content = new StringContent(PlainJson.Serialize(request), Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"A2A {request.GetValueOrDefault("method")}: HTTP {(int)res.StatusCode}");
        return PlainJson.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as IReadOnlyDictionary<string, object?>
            ?? throw new InvalidOperationException("JSON-RPC response is not a JSON object");
    }
}

/// <summary>JSON ↔ the plain Dictionary/List/string/long/double/bool shape this package works on.</summary>
public static class PlainJson
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static object? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement);
    }

    public static object? FromElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => FromElement(p.Value)) as Dictionary<string, object?>,
        JsonValueKind.Array => el.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.String => el.GetString(),
        // The (object) cast is load-bearing: without it the conditional unifies
        // to double and EVERY integer comes back as 3.0 instead of 3L.
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>Pure reductions over A2A messages and tasks — mirror of TypeScript's <c>textOf</c> / <c>dataOf</c>.</summary>
public static class A2aParts
{
    /// <summary>The text parts, joined by newlines.</summary>
    public static string TextOf(object? parts) =>
        string.Join("\n", (parts as IEnumerable<object?> ?? []).OfType<IReadOnlyDictionary<string, object?>>()
            .Where(p => p.GetValueOrDefault("kind") as string == "text" && p.GetValueOrDefault("text") is string)
            .Select(p => (string)p["text"]!));

    /// <summary>The first data part, if any.</summary>
    public static IReadOnlyDictionary<string, object?>? DataOf(object? parts) =>
        (parts as IEnumerable<object?> ?? []).OfType<IReadOnlyDictionary<string, object?>>()
            .FirstOrDefault(p => p.GetValueOrDefault("kind") as string == "data")?.GetValueOrDefault("data") as IReadOnlyDictionary<string, object?>;
}
