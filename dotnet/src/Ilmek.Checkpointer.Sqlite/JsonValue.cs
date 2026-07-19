using System.Text.Json;

namespace Ilmek.Checkpointers.Sqlite;

/// <summary>
/// Round-trips the loosely-typed halves of a checkpoint — channel values, send
/// payloads, journaled step results — through JSON.
///
/// <para>Writing is easy; reading is the part that bites. <c>System.Text.Json</c>
/// hands a <c>JsonElement</c> back for every <c>object?</c>, so a naive decode
/// would resurrect a thread whose <c>state["cart"]</c> is a JsonElement rather
/// than the list the node wrote. Every node reading state after a restart would
/// break. So the decode below lowers JsonElement into plain CLR values —
/// dictionary, list, string, long/double, bool, null — which is exactly the shape
/// MODEL.md §5.4 already requires of anything journaled.</para>
///
/// <para>The consequence is worth stating plainly, because it is a real
/// constraint and not a bug: a value that goes into a channel or a step as a
/// custom type comes back as its JSON shape. Journal what serializes (ids,
/// strings, numbers, dictionaries) and re-resolve richer objects from it.</para>
/// </summary>
internal static class JsonValue
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        // PascalCase, matching the record property names the decoder reads below.
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static string Encode(object? value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Lower a parsed element into plain CLR values.</summary>
    public static object? Decode(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => Decode(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(Decode).ToList(),
        JsonValueKind.String => element.GetString(),
        // An integral number stays integral: a step that journaled 3 must not come
        // back as 3.0 and start failing `is long` checks on replay.
        //
        // The `(object)` cast is load-bearing. Without it C# unifies the two
        // conditional branches to their common numeric type — double — so the
        // long is silently widened even when TryGetInt64 succeeds, and EVERY
        // integer comes back as a double.
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static Dictionary<string, object?> DecodeObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(p => p.Name, p => Decode(p.Value))
            : new Dictionary<string, object?>();

    public static string? StringOrNull(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
