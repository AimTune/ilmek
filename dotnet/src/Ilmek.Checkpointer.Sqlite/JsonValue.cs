using System.Text.Json;

namespace Ilmek.Checkpointers.Sqlite;

/// <summary>
/// Round-trips the loosely-typed halves of a checkpoint — channel values, send
/// payloads, journaled step results — through JSON, with the core's
/// <see cref="JournalJson"/> contract.
///
/// <para>Writing is easy; reading is the part that bites. <c>System.Text.Json</c>
/// hands a <c>JsonElement</c> back for every <c>object?</c>, so a naive decode
/// would resurrect a thread whose <c>state["cart"]</c> is a JsonElement rather
/// than the list the node wrote. Every node reading state after a restart would
/// break. So the decode below lowers JsonElement into plain CLR values —
/// dictionary, list, string, long/double (decimal when a double would round),
/// bool, null — the shape MODEL.md §5.4 requires of anything journaled.</para>
///
/// <para>A value that went into a channel as a custom type comes back as that
/// JSON shape. A typed step or interrupt does not: <c>StepAsync&lt;T&gt;</c> and
/// <c>InterruptAsync&lt;T&gt;</c> convert the replayed data back to <c>T</c>
/// through <see cref="JournalJson.ConvertTo{T}"/>, with these same options.</para>
/// </summary>
internal static class JsonValue
{
    public static string Encode(object? value) => JournalJson.Serialize(value);

    /// <summary>Lower a parsed element into plain CLR values.</summary>
    public static object? Decode(JsonElement element) => JournalJson.ToPlain(element);

    public static Dictionary<string, object?> DecodeObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(p => p.Name, p => Decode(p.Value))
            : new Dictionary<string, object?>();

    public static string? StringOrNull(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
