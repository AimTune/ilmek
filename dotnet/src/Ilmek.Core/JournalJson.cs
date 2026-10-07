using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ilmek;

/// <summary>
/// The one JSON contract for values that cross a durable checkpointer: channel
/// values, send payloads, journaled step results and interrupt answers
/// (MODEL.md §5.4, §7).
///
/// <para>A durable store writes those values as JSON and hands back plain data —
/// dictionaries, lists, strings, numbers, booleans — not the CLR objects that went
/// in. <see cref="ConvertTo{T}"/> is what turns a replayed value back into the
/// <c>T</c> a node asked for (<c>StepAsync&lt;T&gt;</c>,
/// <c>InterruptAsync&lt;T&gt;</c>): a record, a tuple, an exact <c>decimal</c>, an
/// enum, a list of records. It goes through System.Text.Json with
/// <see cref="Options"/>, the same options a checkpointer journals with, so what
/// is written is exactly what is read. A value that is already a <c>T</c> — every
/// value the in-memory checkpointer keeps — is returned as is: same instance, no
/// conversion.</para>
///
/// <para>A checkpointer implementation should serialize with <see cref="Options"/>
/// and lower what it reads with <see cref="ToPlain"/>. Everything here is
/// culture-invariant.</para>
/// </summary>
public static class JournalJson
{
    /// <summary>
    /// The serializer options for journaled values. Property names are kept as
    /// declared (PascalCase); public fields are included so value tuples
    /// round-trip; an <c>object</c>-typed member reads back as plain CLR data
    /// (see <see cref="ToPlain"/>), never as a <see cref="JsonElement"/>. Read-only.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            IncludeFields = true,
        };
        options.Converters.Add(new PlainObjectConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Serialize a journaled value to JSON text.</summary>
    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Lower a parsed element into plain CLR values: <c>Dictionary&lt;string, object?&gt;</c>,
    /// <c>List&lt;object?&gt;</c>, <c>string</c>, <c>bool</c>, <c>null</c>, and numbers
    /// as <c>long</c> when integral, otherwise <c>double</c> — or <c>decimal</c> when a
    /// double cannot reproduce the number's exact text (<c>1.10</c>, or more digits
    /// than a double holds), so a decimal is never rounded on the way through.
    /// </summary>
    public static object? ToPlain(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToPlain(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToPlain).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => Number(element),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static object Number(JsonElement element)
    {
        // An integral number stays integral: a step that journaled 3 must not come
        // back as 3.0 and start failing `is long` checks on replay.
        if (element.TryGetInt64(out var l)) return l;

        // A double only when it writes back to the very same text — then any later
        // conversion (to decimal, float, …) sees exactly what was journaled.
        var raw = element.GetRawText();
        if (element.TryGetDouble(out var d) && double.IsFinite(d)
            && string.Equals(JsonSerializer.Serialize(d, Options), raw, StringComparison.Ordinal))
        {
            return d;
        }

        // Otherwise the text carries precision or scale a double would lose.
        if (element.TryGetDecimal(out var m)) return m;
        return element.GetDouble();
    }

    /// <summary>
    /// The value a replay hands back as <typeparamref name="T"/>. A value that already
    /// is a <typeparamref name="T"/> is returned unchanged; <c>null</c> is
    /// <c>default</c> for a type that admits it; anything else — the plain data or
    /// <see cref="JsonElement"/> a durable checkpointer returns — is converted through
    /// JSON with <see cref="Options"/>.
    /// </summary>
    /// <exception cref="InvalidCastException">The value cannot become a <typeparamref name="T"/>.</exception>
    public static T ConvertTo<T>(object? value)
    {
        if (value is T same) return same;

        if (value is null)
        {
            if (default(T) is null) return default!;
            throw new InvalidCastException($"null cannot become {Describe(typeof(T))}");
        }

        try
        {
            var element = value as JsonElement? ?? JsonSerializer.SerializeToElement(value, Options);
            var converted = element.Deserialize<T>(Options);
            if (converted is null && default(T) is not null)
                throw new InvalidCastException($"null cannot become {Describe(typeof(T))}");
            return converted!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidCastException(
                $"{value.GetType().Name} {Preview(value)} cannot become {Describe(typeof(T))}: {ex.Message}", ex);
        }
    }

    private static string Describe(Type type) =>
        Nullable.GetUnderlyingType(type) is { } inner ? $"{inner.Name}?" : type.Name;

    private static string Preview(object value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, Options);
            return json.Length <= 120 ? json : json[..117] + "...";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>
    /// <c>object</c>-typed members (a record's <c>IReadOnlyDictionary&lt;string, object?&gt;</c>,
    /// say) read back as plain CLR data instead of <see cref="JsonElement"/>, and write
    /// by their runtime type.
    /// </summary>
    private sealed class PlainObjectConverter : JsonConverter<object>
    {
        public override bool HandleNull => false;

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            return ToPlain(doc.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            var type = value.GetType();
            if (type == typeof(object))
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }
            JsonSerializer.Serialize(writer, value, type, options);
        }
    }
}
