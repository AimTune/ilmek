using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ilmek;

/// <summary>
/// A declarative predicate — the only kind of condition a <b>stored</b> graph may
/// carry (MODEL.md §9). There is no eval path: the engine interprets these, it
/// never executes text from the document.
///
/// <para><see cref="Eq"/> and <see cref="Neq"/> remember whether they were set, so
/// <c>Eq = null</c> is a predicate ("the channel holds null"), exactly like
/// <c>{ channel, eq: null }</c> in TypeScript — not "no operator". The JSON form
/// writes only the operators that were set and reads <c>channel</c>/<c>Channel</c>
/// keys alike, so a document round-trips between the two languages.</para>
/// </summary>
[JsonConverter(typeof(SpecPredicateJsonConverter))]
public sealed record SpecPredicate
{
    private readonly object? _eq;
    private readonly object? _neq;

    public required string Channel { get; init; }

    public object? Eq { get => _eq; init { _eq = value; HasEq = true; } }

    public object? Neq { get => _neq; init { _neq = value; HasNeq = true; } }

    public IReadOnlyList<object?>? In { get; init; }
    public double? Gt { get; init; }
    public double? Lt { get; init; }
    public bool? Truthy { get; init; }

    /// <summary><see cref="Eq"/> was set — possibly to <c>null</c>.</summary>
    [JsonIgnore] public bool HasEq { get; private init; }

    /// <summary><see cref="Neq"/> was set — possibly to <c>null</c>.</summary>
    [JsonIgnore] public bool HasNeq { get; private init; }
}

/// <summary>
/// JSON for <see cref="SpecPredicate"/>: only the operators that were set are
/// written (so <c>Eq = null</c> survives and an unset operator stays unset); keys
/// are matched case-insensitively, so a TypeScript document (<c>"eq"</c>) reads
/// too; operand values come back as plain CLR data (long, double, string, …),
/// never <see cref="JsonElement"/>, so they compare like any channel value.
/// </summary>
internal sealed class SpecPredicateJsonConverter : JsonConverter<SpecPredicate>
{
    public override SpecPredicate? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"a spec predicate must be a JSON object, got {reader.TokenType}");

        string? channel = null;
        object? eq = null, neq = null;
        bool hasEq = false, hasNeq = false;
        List<object?>? @in = null;
        double? gt = null, lt = null;
        bool? truthy = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString() ?? "";
            reader.Read();
            using var doc = JsonDocument.ParseValue(ref reader);
            var value = doc.RootElement;
            var isNull = value.ValueKind == JsonValueKind.Null;

            switch (name.ToLowerInvariant())
            {
                case "channel": channel = value.ValueKind == JsonValueKind.String ? value.GetString() : null; break;
                case "eq": hasEq = true; eq = JournalJson.ToPlain(value); break;
                case "neq": hasNeq = true; neq = JournalJson.ToPlain(value); break;
                case "in": @in = value.ValueKind == JsonValueKind.Array ? (List<object?>)JournalJson.ToPlain(value)! : null; break;
                case "gt": gt = isNull ? null : Number(value, "gt"); break;
                case "lt": lt = isNull ? null : Number(value, "lt"); break;
                case "truthy": truthy = isNull ? null : value.ValueKind == JsonValueKind.True ? true
                    : value.ValueKind == JsonValueKind.False ? false
                    : throw new JsonException($"a spec predicate's \"truthy\" must be a boolean, got {value.ValueKind}"); break;
                default: break; // an unknown key is not an operator; FromSpec says so if nothing else is
            }
        }

        var pred = new SpecPredicate { Channel = channel!, In = @in, Gt = gt, Lt = lt, Truthy = truthy };
        if (hasEq) pred = pred with { Eq = eq };
        if (hasNeq) pred = pred with { Neq = neq };
        return pred;
    }

    private static double Number(JsonElement value, string op) =>
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : throw new JsonException($"a spec predicate's \"{op}\" must be a number, got {value.ValueKind}");

    public override void Write(Utf8JsonWriter writer, SpecPredicate value, JsonSerializerOptions options)
    {
        string Name(string n) => options.PropertyNamingPolicy?.ConvertName(n) ?? n;

        writer.WriteStartObject();
        writer.WritePropertyName(Name("Channel"));
        writer.WriteStringValue(value.Channel);
        if (value.HasEq) { writer.WritePropertyName(Name("Eq")); JsonSerializer.Serialize(writer, value.Eq, options); }
        if (value.HasNeq) { writer.WritePropertyName(Name("Neq")); JsonSerializer.Serialize(writer, value.Neq, options); }
        if (value.In is not null) { writer.WritePropertyName(Name("In")); JsonSerializer.Serialize(writer, value.In, options); }
        if (value.Gt is { } gt) { writer.WritePropertyName(Name("Gt")); writer.WriteNumberValue(gt); }
        if (value.Lt is { } lt) { writer.WritePropertyName(Name("Lt")); writer.WriteNumberValue(lt); }
        if (value.Truthy is { } t) { writer.WritePropertyName(Name("Truthy")); writer.WriteBooleanValue(t); }
        writer.WriteEndObject();
    }
}

public sealed record SpecChannel(string Reducer = "last_write");

public sealed record SpecNode(string Id, string Type, IReadOnlyDictionary<string, object?>? Config = null);

public sealed record SpecEdge(string From, string To, SpecPredicate? When = null);

/// <summary>The serializable form of a graph (MODEL.md §9).</summary>
public sealed record GraphSpec
{
    public string? Name { get; init; }
    public IReadOnlyDictionary<string, SpecChannel> Channels { get; init; } = new Dictionary<string, SpecChannel>();
    public IReadOnlyList<SpecNode> Nodes { get; init; } = Array.Empty<SpecNode>();
    public IReadOnlyList<SpecEdge> Edges { get; init; } = Array.Empty<SpecEdge>();
}

/// <summary>Maps a node <c>type</c> to a builder. Code-defined graphs register anonymous types.</summary>
public delegate NodeFn NodeBuilder(IReadOnlyDictionary<string, object?> config);

/// <summary>
/// Graphs as data (MODEL.md §9) — the serializable form and its round-trip.
///
/// This is why a drag-and-drop builder is a CRUD app over a document plus a
/// registry browser, and why nothing in the engine knows the builder exists.
///
/// <para>Two rules keep stored graphs safe: a stored spec never carries
/// executable text, and <see cref="ToSpec"/> refuses to serialize what a document
/// cannot honestly hold — a code router, an anonymous node type, a hand-written
/// guard.</para>
/// </summary>
public static class Spec
{
    /// <summary>
    /// Build a graph from its spec.
    ///
    /// <para>A spec is a stored document — usually deserialized JSON — so its shape
    /// is checked at run time rather than trusted from the static types: a null
    /// spec, channel config, node, edge or name fails as a <see cref="GraphException"/>
    /// naming the bad part (the same cases and messages as the TypeScript
    /// <c>fromSpec</c>), never as a NullReferenceException from inside the builder.
    /// Missing <c>Channels</c>, <c>Nodes</c> or <c>Edges</c> read as empty.</para>
    /// </summary>
    public static Graph FromSpec(GraphSpec spec, IReadOnlyDictionary<string, NodeBuilder>? registry = null)
    {
        if (spec is null) throw new GraphException("a graph spec must be an object, got null");

        registry ??= new Dictionary<string, NodeBuilder>();
        var graph = Graph.Create(spec.Name);
        var channels = spec.Channels ?? new Dictionary<string, SpecChannel>();
        var declared = new HashSet<string>(channels.Keys);

        foreach (var (name, cfg) in channels)
        {
            if (cfg is null) throw new GraphException($"channel \"{name}\": config must be an object, got null");
            graph.Channel(name, ChannelFromSpec(cfg.Reducer, name));
        }

        foreach (var node in spec.Nodes ?? [])
        {
            if (node is null) throw new GraphException("a spec node must be an object, got null");
            graph.Node(node.Id, BuildNode(node, registry), type: node.Type,
                config: node.Config ?? new Dictionary<string, object?>());
        }

        foreach (var edge in spec.Edges ?? [])
        {
            if (edge is null) throw new GraphException("a spec edge must be an object, got null");
            graph.Edge(edge.From, edge.To,
                when: edge.When is null ? null : PredicateFromSpec(edge.When, declared),
                specWhen: edge.When);
        }

        return graph;
    }

    /// <summary>
    /// Serialize a graph back to its spec. Throws when the graph holds anything a
    /// document cannot honestly represent — refusing beats inventing a spec that
    /// would not rebuild the same graph.
    /// </summary>
    public static GraphSpec ToSpec(CompiledGraph g)
    {
        var channels = new Dictionary<string, SpecChannel>();
        foreach (var (name, ch) in g.Channels)
        {
            if (ch.Kind == "custom")
                throw new GraphException(
                    $"channel \"{name}\" uses a custom reducer function, which cannot be serialized. " +
                    "Stored graphs are limited to the built-in reducers.");
            channels[name] = new SpecChannel(ch.Kind);
        }

        var nodes = new List<SpecNode>();
        foreach (var id in g.NodeOrder)
        {
            var node = g.Nodes[id];
            if (node.Type is null)
                throw new GraphException(
                    $"node \"{id}\" has no type, so it cannot be serialized — its behaviour is an " +
                    "anonymous function that no registry could resolve back. Give it a type (and a " +
                    "matching registry entry) to make it storable.");
            nodes.Add(new SpecNode(id, node.Type, node.Config));
        }

        var edges = new List<SpecEdge>();
        foreach (var edge in g.Edges)
        {
            if (edge.Router is not null)
                throw new GraphException(
                    $"the router on \"{edge.From}\" cannot be serialized — it is code, and a stored graph " +
                    "must not carry executable text (MODEL.md §9). Express the branch as guarded edges " +
                    "with declarative predicates instead.");
            if (edge.When is not null && edge.SpecWhen is null)
                throw new GraphException(
                    $"the guard on the edge from \"{edge.From}\" is a hand-written function with no " +
                    "declarative equivalent, so it cannot be serialized. Build the edge from a spec, or " +
                    "pass specWhen alongside it.");
            edges.Add(new SpecEdge(edge.From, edge.To!, edge.SpecWhen));
        }

        return new GraphSpec { Name = g.Name, Channels = channels, Nodes = nodes, Edges = edges };
    }

    private static NodeFn BuildNode(SpecNode node, IReadOnlyDictionary<string, NodeBuilder> registry)
    {
        // A document deserialized from JSON can carry a null type despite the
        // record's annotation; say so instead of a raw ArgumentNullException.
        if (node.Type is null)
        {
            throw new GraphException(
                $"node {Graph.Quote(node.Id)} has no \"type\" — a stored graph resolves behaviour through the " +
                "registry, so every node needs one.");
        }

        if (!registry.TryGetValue(node.Type, out var build) || build is null)
        {
            throw new GraphException(
                $"node {Graph.Quote(node.Id)} has type \"{node.Type}\", which is not in the registry. " +
                $"Known types: [{string.Join(", ", registry.Keys)}]");
        }
        return build(node.Config ?? new Dictionary<string, object?>())
            ?? throw new GraphException(
                $"registry entry \"{node.Type}\" returned null; expected a node function (state, ctx).");
    }

    // No reducer named means last_write, as in TS (`reducer ?? "last_write"`).
    private static Channel ChannelFromSpec(string? reducer, string name) => (reducer ?? "last_write") switch
    {
        "last_write" => Channels.LastWrite(),
        "append" => Channels.Append(),
        "merge" => Channels.Merge(),
        _ => throw new GraphException(
            $"channel \"{name}\": unknown reducer \"{reducer}\". A stored graph may only name a " +
            "built-in reducer: \"last_write\", \"append\", \"merge\"."),
    };

    private static GuardFn PredicateFromSpec(SpecPredicate pred, HashSet<string> declared)
    {
        if (pred.Channel is null)
        {
            throw new GraphException(
                $"predicate {JsonSerializer.Serialize(pred)} is malformed — expected an object with a \"channel\" " +
                "key, e.g. { channel: \"intent\", eq: \"buy\" }.");
        }

        if (!declared.Contains(pred.Channel))
        {
            throw new GraphException(
                $"predicate references channel \"{pred.Channel}\", which this spec does not declare. " +
                $"Declared: [{string.Join(", ", declared)}]");
        }

        var op = CompileOp(pred);
        return (state, _) => op(state[pred.Channel]);
    }

    private static Func<object?, bool> CompileOp(SpecPredicate p)
    {
        // Operators in the TS order (eq, neq, in, gt, lt, truthy). Eq/Neq count when
        // they were set, even to null — `"eq" in pred` in TS. One exception keeps
        // documents written by the default serializer before SpecPredicate had its
        // own converter routing as before: there every operator is present and the
        // unused ones are null, so a null Eq/Neq yields when another operator has a
        // value.
        var otherGiven = p.In is not null || p.Gt is not null || p.Lt is not null || p.Truthy is not null;
        var eqGiven = p.HasEq && (p.Eq is not null || (!otherGiven && !(p.HasNeq && p.Neq is not null)));
        var neqGiven = p.HasNeq && (p.Neq is not null || (!otherGiven && !(p.HasEq && p.Eq is not null)));

        if (eqGiven) return a => SpecEquals(a, p.Eq);
        if (neqGiven) return a => !SpecEquals(a, p.Neq);
        if (p.In is not null) return a => p.In.Any(v => SpecEquals(a, v));
        // Like the TS reference (`typeof a === "number"`): only a number compares.
        // A string or bool channel is simply not greater — never parsed, never a
        // FormatException escaping a guard and killing the run.
        if (p.Gt is not null) return a => AsNumber(a) is { } n && n > p.Gt.Value;
        if (p.Lt is not null) return a => AsNumber(a) is { } n && n < p.Lt.Value;
        if (p.Truthy is not null) return a => IsTruthy(a) == p.Truthy.Value;

        throw new GraphException(
            $"predicate on channel \"{p.Channel}\" has no known operator. Supported: Eq, Neq, In, Gt, Lt, Truthy.");
    }

    /// <summary>
    /// A JSON document has one number type, so <c>3</c>, <c>3L</c> and <c>3.0</c>
    /// are the same value to a stored predicate — exactly as in the TS reference,
    /// where <c>3 === 3.0</c>. Plain <c>Equals(3L, 3)</c> is false, which made a
    /// predicate written with an int never match a channel holding a long (the
    /// shape every value has after a SQLite round-trip).
    /// </summary>
    private static bool SpecEquals(object? actual, object? expected) =>
        AsNumber(actual) is { } a && AsNumber(expected) is { } e ? a == e : Equals(actual, expected);

    private static double? AsNumber(object? value) => value switch
    {
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    // Mirrors the TypeScript reference's notion of emptiness so the same document
    // routes the same way in both languages: null, false, "", 0, NaN, an empty
    // list and an empty map are falsy.
    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ when AsNumber(value) is { } n => n != 0 && !double.IsNaN(n),
        _ => true,
    };
}
