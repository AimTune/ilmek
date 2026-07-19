namespace Ilmek;

/// <summary>
/// A declarative predicate — the only kind of condition a <b>stored</b> graph may
/// carry (MODEL.md §9). There is no eval path: the engine interprets these, it
/// never executes text from the document.
/// </summary>
public sealed record SpecPredicate
{
    public required string Channel { get; init; }
    public object? Eq { get; init; }
    public object? Neq { get; init; }
    public IReadOnlyList<object?>? In { get; init; }
    public double? Gt { get; init; }
    public double? Lt { get; init; }
    public bool? Truthy { get; init; }
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
    /// <summary>Build a graph from its spec.</summary>
    public static Graph FromSpec(GraphSpec spec, IReadOnlyDictionary<string, NodeBuilder>? registry = null)
    {
        registry ??= new Dictionary<string, NodeBuilder>();
        var graph = Graph.Create(spec.Name);
        var declared = new HashSet<string>(spec.Channels.Keys);

        foreach (var (name, cfg) in spec.Channels)
            graph.Channel(name, ChannelFromSpec(cfg.Reducer, name));

        foreach (var node in spec.Nodes)
            graph.Node(node.Id, BuildNode(node, registry), type: node.Type,
                config: node.Config ?? new Dictionary<string, object?>());

        foreach (var edge in spec.Edges)
            graph.Edge(edge.From, edge.To,
                when: edge.When is null ? null : PredicateFromSpec(edge.When, declared),
                specWhen: edge.When);

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
        if (!registry.TryGetValue(node.Type, out var build))
        {
            throw new GraphException(
                $"node \"{node.Id}\" has type \"{node.Type}\", which is not in the registry. " +
                $"Known types: [{string.Join(", ", registry.Keys)}]");
        }
        return build(node.Config ?? new Dictionary<string, object?>());
    }

    private static Channel ChannelFromSpec(string reducer, string name) => reducer switch
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
        if (p.Eq is not null) return a => Equals(a, p.Eq);
        if (p.Neq is not null) return a => !Equals(a, p.Neq);
        if (p.In is not null) return a => p.In.Any(v => Equals(a, v));
        if (p.Gt is not null) return a => a is IConvertible && Convert.ToDouble(a) > p.Gt.Value;
        if (p.Lt is not null) return a => a is IConvertible && Convert.ToDouble(a) < p.Lt.Value;
        if (p.Truthy is not null) return a => IsTruthy(a) == p.Truthy.Value;

        throw new GraphException(
            $"predicate on channel \"{p.Channel}\" has no known operator. Supported: Eq, Neq, In, Gt, Lt, Truthy.");
    }

    // Mirrors the TypeScript reference's notion of emptiness so the same document
    // routes the same way in both languages.
    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };
}
