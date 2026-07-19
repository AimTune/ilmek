using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace Ilmek;

/// <summary>
/// A typed update: the partial write a node returns (MODEL.md §2).
///
/// <para>It <b>is</b> an <c>IReadOnlyDictionary&lt;string, object?&gt;</c>, which is
/// why the typed layer needs no engine changes: a node can return it directly, put
/// it in a <see cref="Command"/>, or pass it as run input, and the core folds it
/// through the same reducers as any other update.</para>
///
/// <code>
/// return Update.For&lt;CheckoutState&gt;()
///     .Set(s => s.Intent, "buy")     // last-write channel: the value itself
///     .Append(s => s.Log, "charged") // append channel: one item, element-typed
///     ;
/// </code>
/// </summary>
public sealed class StateUpdate<TState> : IReadOnlyDictionary<string, object?>
    where TState : class, new()
{
    private readonly Dictionary<string, object?> _values = new();

    /// <summary>Write a channel with a value of exactly its own type.</summary>
    public StateUpdate<TState> Set<TProp>(Expression<Func<TState, TProp>> selector, TProp value)
    {
        _values[TypedSchema.NameOf(selector)] = value;
        return this;
    }

    /// <summary>
    /// Append to a list channel. The item type comes from the property, so
    /// `Append(s => s.Log, 42)` on a `List&lt;string&gt;` is a compile error.
    /// </summary>
    public StateUpdate<TState> Append<TItem>(
        Expression<Func<TState, List<TItem>>> selector, params TItem[] items)
    {
        _values[TypedSchema.NameOf(selector)] = items.Cast<object?>().ToList();
        return this;
    }

    // ── IReadOnlyDictionary ─────────────────────────────────────────────────
    public object? this[string key] => _values[key];
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<object?> Values => _values.Values;
    public int Count => _values.Count;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();
}

/// <summary>Entry point for typed updates: <c>Update.For&lt;TState&gt;().Set(…)</c>.</summary>
public static partial class Update
{
    public static StateUpdate<TState> For<TState>() where TState : class, new() => new();
}

/// <summary>
/// Declares a property's reducer on the state class itself (MODEL.md §2), so the
/// graph derives every channel from the class — no per-channel restatement.
/// A property with no attribute is a <c>last_write</c> channel, the default.
///
/// <code>
/// public sealed class CheckoutState
/// {
///     public List&lt;string&gt; Cart { get; set; } = [];   // last_write (default)
///     [Append] public List&lt;string&gt; Log { get; set; } = [];
///     public string Intent { get; set; } = "";
/// }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public abstract class ChannelAttribute : Attribute
{
    internal abstract Channel Build();
}

/// <summary>The default reducer: <c>incoming</c> wins (MODEL.md §2). Rarely needed — it is the default.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LastWriteAttribute : ChannelAttribute
{
    internal override Channel Build() => Channels.LastWrite();
}

/// <summary>List concat: the property accumulates across supersteps (MODEL.md §2).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AppendAttribute : ChannelAttribute
{
    internal override Channel Build() => Channels.Append();
}

/// <summary>Shallow dictionary merge, <c>incoming</c> wins per key (MODEL.md §2).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MergeAttribute : ChannelAttribute
{
    internal override Channel Build() => Channels.Merge();
}

/// <summary>Maps a state class's properties to channel names, and back again.</summary>
internal static class TypedSchema
{
    /// <summary>Every public writable instance property is a channel (MODEL.md §2).</summary>
    public static List<PropertyInfo> ChannelProps<TState>() =>
        typeof(TState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)
            .ToList();

    /// <summary>The channel a property declares via its attribute, or last_write by default.</summary>
    public static Channel ChannelFor(PropertyInfo prop) =>
        prop.GetCustomAttribute<ChannelAttribute>()?.Build() ?? Channels.LastWrite();

    /// <summary>A channel's name is its property's name — one obvious mapping, no attributes.</summary>
    public static string NameOf<TState, TProp>(Expression<Func<TState, TProp>> selector) =>
        selector.Body switch
        {
            MemberExpression m => m.Member.Name,
            // A value-typed property under a Func<,object> selector arrives boxed.
            UnaryExpression { Operand: MemberExpression m } => m.Member.Name,
            _ => throw new GraphException(
                $"expected a property selector like s => s.Log, got {selector.Body}"),
        };

    /// <summary>
    /// A fan-out task's state is its send payload (MODEL.md §14). It reaches here
    /// as either the object passed to <c>send(...)</c> (in-memory) or a dictionary
    /// keyed by property names (after a durable round-trip) — handle both.
    /// </summary>
    public static TIn SendPayload<TIn>(State state, IReadOnlyList<PropertyInfo> props)
        where TIn : class, new() =>
        state["input"] is TIn direct ? direct : Materialize<TIn>(state, props);

    /// <summary>Build a <typeparamref name="TState"/> from the raw channel values.</summary>
    public static TState Materialize<TState>(State state, IReadOnlyList<PropertyInfo> props)
        where TState : class, new()
    {
        var typed = new TState();
        foreach (var prop in props)
        {
            var raw = state[prop.Name];
            if (raw is null) continue;
            prop.SetValue(typed, Coerce(raw, prop.PropertyType));
        }
        return typed;
    }

    /// <summary>
    /// Fit a channel value to its property's type.
    ///
    /// <para>Necessary because a durable checkpointer round-trips values through
    /// JSON (see the SQLite provider): a `List&lt;string&gt;` written before a
    /// restart comes back as `List&lt;object?&gt;`, and a resumed node would
    /// otherwise get a cast failure where the untyped API silently handed back
    /// whatever was there.</para>
    /// </summary>
    public static object? Coerce(object? value, Type target)
    {
        if (value is null) return null;
        if (target.IsInstanceOfType(value)) return value;

        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>)
            && value is IEnumerable seq and not string)
        {
            var itemType = target.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(target)!;
            foreach (var item in seq) list.Add(Coerce(item, itemType));
            return list;
        }

        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            && value is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            var valueType = target.GetGenericArguments()[1];
            var dict = (IDictionary)Activator.CreateInstance(target)!;
            foreach (var (k, v) in pairs) dict[k] = Coerce(v, valueType);
            return dict;
        }

        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(underlying))
        {
            try { return Convert.ChangeType(value, underlying); }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                // Fall through: hand the value over untouched rather than guess.
            }
        }

        return value;
    }
}

/// <summary>A compiled graph whose state is <typeparamref name="TState"/> (MODEL.md §3).</summary>
public sealed class CompiledGraph<TState> where TState : class, new()
{
    /// <summary>The untyped graph underneath. The typed layer is a facade; this is the engine's view.</summary>
    public CompiledGraph Inner { get; }

    internal IReadOnlyList<PropertyInfo> Props { get; }

    internal CompiledGraph(CompiledGraph inner, IReadOnlyList<PropertyInfo> props)
    {
        Inner = inner;
        Props = props;
    }

    internal TState Materialize(State state) => TypedSchema.Materialize<TState>(state, Props);
}

/// <summary>
/// A graph whose state is a class you declare, rather than a string-keyed map.
///
/// <para>Channels are properties, named by a selector — so a typo is a compile
/// error, `state.Cart` is a `List&lt;string&gt;`, and the state type is a thing you
/// can export and share. It compiles down to exactly the same channel map the
/// untyped <see cref="Graph"/> builds, so reducers, journals and everything in
/// MODEL.md work unchanged.</para>
///
/// <code>
/// public sealed class CheckoutState
/// {
///     public List&lt;string&gt; Cart { get; set; } = [];   // last_write (default)
///     [Append] public List&lt;string&gt; Log { get; set; } = [];
/// }
///
/// // Channels are derived from CheckoutState — no per-channel restatement.
/// var g = Graph.Create&lt;CheckoutState&gt;("checkout")
///     .Node("checkout", (state, ctx) => Update.For&lt;CheckoutState&gt;().Append(s => s.Log, "done"))
///     .Edge(Graph.Start, "checkout")
///     .Edge("checkout", Graph.End)
///     .Compile();
/// </code>
/// </summary>
public sealed class Graph<TState> where TState : class, new()
{
    private readonly Graph _inner;
    private readonly List<PropertyInfo> _props;
    private readonly Dictionary<string, Channel> _channels;

    internal Graph(string? name)
    {
        _inner = Graph.Create(name);
        // The class IS the schema: every writable property is a channel, its
        // reducer read from the property's attribute (MODEL.md §2). Registered
        // into the engine at Compile so an override below can still replace one.
        _props = TypedSchema.ChannelProps<TState>();
        _channels = _props.ToDictionary(p => p.Name, TypedSchema.ChannelFor);
    }

    /// <summary>
    /// Override a channel's reducer — the escape hatch for a custom reducer, which
    /// an attribute cannot express. The built-in reducers are declared on the
    /// state class (<see cref="AppendAttribute"/> et al.) and need no call here.
    /// </summary>
    public Graph<TState> Channel<TProp>(Expression<Func<TState, TProp>> selector, Channel channel)
    {
        var name = TypedSchema.NameOf(selector);
        if (!_channels.ContainsKey(name))
        {
            var prop = typeof(TState).GetProperty(name);
            throw new GraphException(prop is { CanWrite: false }
                ? $"{typeof(TState).Name}.{name} has no setter — a channel must be writable so the " +
                  "engine can hand each task its state."
                : $"{typeof(TState).Name} has no channel property \"{name}\".");
        }
        _channels[name] = channel;
        return this;
    }

    /// <summary>
    /// A node over the typed state. Return a <see cref="StateUpdate{TState}"/>,
    /// null, or a <see cref="Command"/>.
    /// </summary>
    public Graph<TState> Node(string id, Func<TState, IContext, object?> fn,
        RetryPolicy? retry = null, string? type = null,
        IReadOnlyDictionary<string, object?>? config = null)
        => Node(id, (state, ctx) => new ValueTask<object?>(fn(state, ctx)), retry, type, config);

    /// <summary>Async node over the typed state.</summary>
    public Graph<TState> Node(string id, Func<TState, IContext, ValueTask<object?>> fn,
        RetryPolicy? retry = null, string? type = null,
        IReadOnlyDictionary<string, object?>? config = null)
    {
        _inner.Node(id, (state, ctx) => fn(Typed(state), ctx), retry, type, config);
        return this;
    }

    /// <summary>
    /// A node whose input is a fan-out payload (MODEL.md §14), not the graph
    /// state. The <see cref="Send"/> payload is materialized to
    /// <typeparamref name="TIn"/> — so `send("worker", new Job { … })` gives the
    /// worker a typed `Job`, the same way the graph state is typed. The node still
    /// writes the graph's channels through its return.
    /// </summary>
    public Graph<TState> Node<TIn>(string id, Func<TIn, IContext, object?> fn,
        RetryPolicy? retry = null, string? type = null,
        IReadOnlyDictionary<string, object?>? config = null) where TIn : class, new()
        => Node<TIn>(id, (payload, ctx) => new ValueTask<object?>(fn(payload, ctx)), retry, type, config);

    /// <summary>Async fan-out node — see the synchronous overload.</summary>
    public Graph<TState> Node<TIn>(string id, Func<TIn, IContext, ValueTask<object?>> fn,
        RetryPolicy? retry = null, string? type = null,
        IReadOnlyDictionary<string, object?>? config = null) where TIn : class, new()
    {
        var props = TypedSchema.ChannelProps<TIn>();
        _inner.Node(id, (state, ctx) => fn(TypedSchema.SendPayload<TIn>(state, props), ctx), retry, type, config);
        return this;
    }

    /// <summary>A static edge, optionally guarded by a predicate over the typed state.</summary>
    public Graph<TState> Edge(string from, string to, Func<TState, IContext, bool>? when = null)
    {
        _inner.Edge(from, to, when is null ? null : (state, ctx) => when(Typed(state), ctx));
        return this;
    }

    /// <summary>A conditional edge returning node names and/or sends (MODEL.md §14).</summary>
    public Graph<TState> Router(string from, Func<TState, IContext, IEnumerable<object>> fn)
    {
        _inner.Router(from, (state, ctx) => fn(Typed(state), ctx));
        return this;
    }

    /// <summary>Validate and freeze. Throws <see cref="GraphException"/>.</summary>
    public CompiledGraph<TState> Compile()
    {
        foreach (var (name, channel) in _channels) _inner.Channel(name, channel);
        return new(_inner.Compile(), _props);
    }

    private TState Typed(State state) => TypedSchema.Materialize<TState>(state, _props);
}

/// <summary>The settled outcome of a typed run.</summary>
public sealed record Result<TState> where TState : class, new()
{
    public required RunStatus Status { get; init; }

    /// <summary>Final state. Set when <see cref="Status"/> is <see cref="RunStatus.Done"/>.</summary>
    public TState? State { get; init; }

    public required string ThreadId { get; init; }
    public required string RunId { get; init; }
    public IReadOnlyList<Pending> Pending { get; init; } = Array.Empty<Pending>();
    public IReadOnlyList<(string Node, Exception Error)> Errors { get; init; } = Array.Empty<(string, Exception)>();
    public string? AbortReason { get; init; }
    public IReadOnlyList<IlmekEvent> Events { get; init; } = Array.Empty<IlmekEvent>();

    internal static Result<TState> From(Result r, CompiledGraph<TState> graph) => new()
    {
        Status = r.Status,
        State = r.State is null ? null : graph.Materialize(r.State),
        ThreadId = r.ThreadId,
        RunId = r.RunId,
        Pending = r.Pending,
        Errors = r.Errors,
        AbortReason = r.AbortReason,
        Events = r.Events,
    };
}

/// <summary>Run entry points for a typed graph — the same engine, typed at the edges.</summary>
public static class TypedGraphExtensions
{
    public static async Task<Result<TState>> RunAsync<TState>(this CompiledGraph<TState> graph,
        IReadOnlyDictionary<string, object?>? input = null, RunOptions? options = null,
        CancellationToken ct = default) where TState : class, new() =>
        Result<TState>.From(await graph.Inner.RunAsync(input, options, ct).ConfigureAwait(false), graph);

    public static IAsyncEnumerable<IlmekEvent> StreamEvents<TState>(this CompiledGraph<TState> graph,
        IReadOnlyDictionary<string, object?>? input = null, RunOptions? options = null,
        CancellationToken ct = default) where TState : class, new() =>
        graph.Inner.StreamEvents(input, options, ct);

    public static async Task<Result<TState>> ResumeAsync<TState>(this CompiledGraph<TState> graph,
        object? answer, RunOptions? options = null, CancellationToken ct = default)
        where TState : class, new() =>
        Result<TState>.From(await graph.Inner.ResumeAsync(answer, options, ct).ConfigureAwait(false), graph);

    public static async Task<Result<TState>> ResumeKeyedAsync<TState>(this CompiledGraph<TState> graph,
        IReadOnlyDictionary<string, object?> answers, RunOptions? options = null,
        CancellationToken ct = default) where TState : class, new() =>
        Result<TState>.From(
            await graph.Inner.ResumeKeyedAsync(answers, options, ct).ConfigureAwait(false), graph);
}
