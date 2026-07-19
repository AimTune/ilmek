namespace Ilmek;

/// <summary>
/// One named unit of work (MODEL.md §3).
///
/// Returns a channel update (<c>IReadOnlyDictionary&lt;string, object?&gt;</c>),
/// null (effects only), or a <see cref="Command"/> carrying an update alongside a
/// routing decision (MODEL.md §15).
/// </summary>
public delegate ValueTask<object?> NodeFn(State state, IContext ctx);

/// <summary>
/// A guard or router runs at plan time, outside any task — it has no journal and
/// MUST NOT perform side effects (MODEL.md §3). Route on state; compute in nodes.
/// </summary>
public delegate bool GuardFn(State state, IContext ctx);

/// <summary>
/// Returns the next target(s): node names, <see cref="Graph.End"/>, and/or
/// <see cref="Send"/> values for dynamic fan-out (MODEL.md §14).
/// </summary>
public delegate IEnumerable<object> RouterFn(State state, IContext ctx);

public sealed record GraphNode(string Id, NodeFn Fn, RetryPolicy? Retry, string? Type, IReadOnlyDictionary<string, object?> Config);

/// <summary>Exactly one of <see cref="To"/> (static, optionally guarded) or <see cref="Router"/> is set.</summary>
public sealed record GraphEdge(string From, string? To, GuardFn? When, RouterFn? Router, SpecPredicate? SpecWhen);

/// <summary>A validated, frozen graph (MODEL.md §3).</summary>
public sealed class CompiledGraph
{
    public string? Name { get; }
    public IReadOnlyDictionary<string, Channel> Channels { get; }
    public IReadOnlyDictionary<string, GraphNode> Nodes { get; }
    public IReadOnlyList<string> NodeOrder { get; }
    public IReadOnlyList<GraphEdge> Edges { get; }

    internal CompiledGraph(string? name, IReadOnlyDictionary<string, Channel> channels,
        IReadOnlyDictionary<string, GraphNode> nodes, IReadOnlyList<string> nodeOrder,
        IReadOnlyList<GraphEdge> edges)
    {
        Name = name;
        Channels = channels;
        Nodes = nodes;
        NodeOrder = nodeOrder;
        Edges = edges;
    }

    /// <summary>Materialize raw channel values, substituting defaults for unwritten channels.</summary>
    public State Materialize(IReadOnlyDictionary<string, object?> values)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (name, channel) in Channels)
        {
            var has = values.TryGetValue(name, out var raw);
            result[name] = !has || raw is null || ReferenceEquals(raw, Channel.Unset) ? channel.Default : raw;
        }
        return new State(result);
    }
}

/// <summary>
/// The static, serializable description of a workflow (MODEL.md §3).
///
/// <code>
/// var g = Graph.Create("support")
///     .Channel("messages", Channels.Append())
///     .Node("agent", (state, ctx) => Update.Of("messages", "hi"))
///     .Edge(Graph.Start, "agent")
///     .Edge("agent", Graph.End)
///     .Compile();
/// </code>
/// </summary>
public sealed class Graph
{
    /// <summary>The virtual entry node. Implicit; has no body.</summary>
    public const string Start = "__start__";

    /// <summary>The virtual exit node. Implicit; has no body.</summary>
    public const string End = "__end__";

    private static readonly HashSet<string> Reserved = new() { Start, End };

    private readonly string? _name;
    private readonly Dictionary<string, Channel> _channels = new();
    private readonly Dictionary<string, GraphNode> _nodes = new();
    private readonly List<string> _nodeOrder = new();
    private readonly List<GraphEdge> _edges = new();

    private Graph(string? name) => _name = name;

    /// <summary>Start an untyped graph — state is a string-keyed channel map (MODEL.md §2).</summary>
    public static Graph Create(string? name = null) => new(name);

    /// <summary>
    /// Start a graph whose state is <typeparamref name="TState"/> — channels are
    /// properties, named by selector, so `state.Cart` is typed and a typo is a
    /// compile error. Compiles to the same channel map as <see cref="Create"/>.
    /// </summary>
    public static Graph<TState> Create<TState>(string? name = null) where TState : class, new() => new(name);

    /// <summary>Declare a channel and its reducer (MODEL.md §2). Undeclared channels are a runtime error.</summary>
    public Graph Channel(string name, Channel channel)
    {
        if (_channels.ContainsKey(name)) throw new GraphException($"duplicate channel \"{name}\"");
        _channels[name] = channel;
        return this;
    }

    public Graph Node(string id, NodeFn fn, RetryPolicy? retry = null, string? type = null,
        IReadOnlyDictionary<string, object?>? config = null)
    {
        if (Reserved.Contains(id)) throw new GraphException($"\"{id}\" is reserved and implicit");
        if (_nodes.ContainsKey(id)) throw new GraphException($"duplicate node \"{id}\"");
        if (retry is not null && retry.MaxAttempts < 1)
            throw new GraphException($"node \"{id}\": RetryPolicy.MaxAttempts must be ≥ 1");

        _nodes[id] = new GraphNode(id, fn, retry, type, config ?? new Dictionary<string, object?>());
        _nodeOrder.Add(id);
        return this;
    }

    /// <summary>Synchronous node overload — the common case.</summary>
    public Graph Node(string id, Func<State, IContext, object?> fn, RetryPolicy? retry = null,
        string? type = null, IReadOnlyDictionary<string, object?>? config = null) =>
        Node(id, (state, ctx) => new ValueTask<object?>(fn(state, ctx)), retry, type, config);

    /// <summary>A static edge, optionally guarded.</summary>
    public Graph Edge(string from, string to, GuardFn? when = null, SpecPredicate? specWhen = null)
    {
        _edges.Add(new GraphEdge(from, to, when, null, specWhen));
        return this;
    }

    /// <summary>A conditional edge returning the target name(s) — and/or sends — at plan time.</summary>
    public Graph Router(string from, RouterFn fn)
    {
        _edges.Add(new GraphEdge(from, null, null, fn, null));
        return this;
    }

    /// <summary>Validate the graph and freeze it. Throws <see cref="GraphException"/>.</summary>
    public CompiledGraph Compile()
    {
        foreach (var edge in _edges)
        {
            if (edge.From != Start && !_nodes.ContainsKey(edge.From))
                throw new GraphException($"edge from unknown node \"{edge.From}\"");
            if (edge.To is not null && edge.To != End && !_nodes.ContainsKey(edge.To))
                throw new GraphException($"edge \"{edge.From}\" -> unknown node \"{edge.To}\"");
        }

        if (!_edges.Any(e => e.From == Start))
            throw new GraphException("graph has no entry edge — add .Edge(Graph.Start, \"someNode\")");

        return new CompiledGraph(_name, _channels, _nodes, _nodeOrder, _edges);
    }
}

internal static class Routing
{
    /// <summary>Plan the targets leaving <paramref name="from"/> (MODEL.md §4 step 1).</summary>
    public static List<object> Targets(CompiledGraph g, string from, State state, IContext ctx)
    {
        var raw = new List<object>();
        foreach (var edge in g.Edges)
        {
            if (edge.From != from) continue;
            if (edge.Router is not null) raw.AddRange(edge.Router(state, ctx));
            else if (edge.To is not null && (edge.When is null || edge.When(state, ctx))) raw.Add(edge.To);
        }
        return Resolve(g, from, raw);
    }

    /// <summary>
    /// Validate raw targets and dedup the plain names (MODEL.md §14). <c>End</c>
    /// drops out; sends are kept as-is — two sends with identical payloads are two
    /// real tasks, so they must never be collapsed the way duplicate names are.
    /// </summary>
    public static List<object> Resolve(CompiledGraph g, string from, IEnumerable<object> raw)
    {
        var seen = new HashSet<string>();
        var result = new List<object>();

        foreach (var target in raw)
        {
            if (target is Send send)
            {
                ValidateNode(g, from, send.Node);
                result.Add(send);
                continue;
            }

            var name = target as string
                ?? throw new GraphException(
                    $"routing from \"{from}\" produced {target?.GetType().Name ?? "null"}; " +
                    "expected a node name string or a Send.");

            if (name == Graph.End || !seen.Add(name)) continue;
            ValidateNode(g, from, name);
            result.Add(name);
        }

        return result;
    }

    private static void ValidateNode(CompiledGraph g, string from, string node)
    {
        if (!g.Nodes.ContainsKey(node))
            throw new GraphException(
                $"routing from \"{from}\" produced \"{node}\", which is not a node in this graph. " +
                $"Known nodes: [{string.Join(", ", g.Nodes.Keys)}]");
    }

    /// <summary>
    /// Turn raw targets into scheduled tasks (MODEL.md §4, §14). Plain names dedup
    /// — a node runs once per superstep however many predecessors point at it —
    /// while every send becomes its own task, keyed <c>node#n</c>.
    /// </summary>
    public static List<ScheduledTask> Schedule(IEnumerable<object> raw)
    {
        var seen = new HashSet<string>();
        var sendCounts = new Dictionary<string, int>();
        var result = new List<ScheduledTask>();

        foreach (var target in raw)
        {
            if (target is Send send)
            {
                var n = sendCounts.TryGetValue(send.Node, out var c) ? c : 0;
                sendCounts[send.Node] = n + 1;
                result.Add(new ScheduledTask(send.Node, $"{send.Node}#{n}", true, send.Input));
            }
            else if (target is string name && seen.Add(name))
            {
                result.Add(new ScheduledTask(name, name, false));
            }
        }

        return result;
    }
}
