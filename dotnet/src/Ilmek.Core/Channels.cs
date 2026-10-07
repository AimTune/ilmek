using System.Collections;

namespace Ilmek;

/// <summary>
/// One named slot of state plus the reducer that folds updates into it
/// (MODEL.md §2).
///
/// A node never writes a channel directly — it returns a partial update and the
/// engine folds each key through its channel's reducer.
/// </summary>
public sealed class Channel
{
    /// <summary>The sentinel meaning "this channel has never been written".</summary>
    public static readonly object Unset = new UnsetMarker();

    private sealed class UnsetMarker
    {
        public override string ToString() => "(unset)";
    }

    public string Kind { get; }

    /// <summary>
    /// The value an unwritten channel reads as. For the built-in collection
    /// reducers (append, merge) every read is a fresh empty collection: one shared
    /// instance would let a node that mutates <c>state["log"]</c> in place, instead
    /// of returning an update, corrupt the default for every later run in the
    /// process (the TypeScript reference freezes its defaults for the same reason).
    /// </summary>
    public object? Default => _defaultFactory is null ? _default : _defaultFactory();

    private readonly object? _default;
    private readonly Func<object?>? _defaultFactory;
    private readonly Func<object?, object?, object?> _reduce;

    public Channel(string kind, object? @default, Func<object?, object?, object?> reduce)
    {
        Kind = kind;
        _default = @default;
        _reduce = reduce;
    }

    internal Channel(string kind, Func<object?> defaultFactory, Func<object?, object?, object?> reduce)
    {
        Kind = kind;
        _defaultFactory = defaultFactory;
        _reduce = reduce;
    }

    /// <summary>Fold <paramref name="incoming"/> into <paramref name="current"/>, which may be <see cref="Unset"/>.</summary>
    public object? Reduce(string name, object? current, object? incoming)
    {
        try
        {
            return _reduce(current, incoming);
        }
        catch (ReducerException ex)
        {
            // A built-in reducer's own refusal: attach the channel name so the
            // error says which write failed.
            throw new ReducerException($"channel \"{name}\": {ex.Message}", ex.InnerException);
        }
        catch (Exception ex) when (ex is not ReducerException)
        {
            throw new ReducerException(
                $"channel \"{name}\": reducer {Kind} cannot fold {Describe(incoming)} into {Describe(current)}", ex);
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        UnsetMarker => "(unset)",
        string s => $"\"{s}\"",
        _ => value.ToString() ?? value.GetType().Name,
    };
}

/// <summary>The built-in reducers (MODEL.md §2).</summary>
public static class Channels
{
    /// <summary><c>incoming</c> wins. The default reducer.</summary>
    public static Channel LastWrite(object? @default = null) =>
        new("last_write", @default, (_, incoming) => incoming);

    /// <summary>List concat. Holds a list; accepts one item or many.</summary>
    public static Channel Append() =>
        new("append", () => new List<object?>(), (current, incoming) =>
        {
            var list = current switch
            {
                null => new List<object?>(),
                _ when ReferenceEquals(current, Channel.Unset) => new List<object?>(),
                IEnumerable items when current is not string && !IsMap(current) => items.Cast<object?>().ToList(),
                // The TS reference's concat throws on a non-list current; quietly
                // starting a fresh list here would drop the channel's history.
                _ => throw new ReducerException(
                    $"append holds a list, but the channel's current value is {current.GetType().Name}"),
            };
            // Mirror the TS rule: an enumerable splats, anything else appends as
            // one item. Strings are enumerable in .NET and must NOT splat into
            // characters — that is the whole reason for the explicit exclusion.
            // A dictionary is one value too (MODEL.md §2: `List.wrap(map)` is
            // `[map]`); splatting it would append its KeyValuePairs instead.
            if (incoming is IEnumerable seq && incoming is not string && !IsMap(incoming))
            {
                foreach (var item in seq) list.Add(item);
            }
            else
            {
                list.Add(incoming);
            }
            return list;
        });

    private static bool IsMap(object value) =>
        value is IDictionary
        || value.GetType().GetInterfaces().Any(i => i.IsGenericType
            && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    /// <summary>Shallow dictionary merge, <c>incoming</c> wins per key.</summary>
    public static Channel Merge() =>
        new("merge", () => new Dictionary<string, object?>(),(current, incoming) =>
        {
            var merged = current is IReadOnlyDictionary<string, object?> existing
                ? new Dictionary<string, object?>(existing.ToDictionary(kv => kv.Key, kv => kv.Value))
                : new Dictionary<string, object?>();
            if (incoming is IReadOnlyDictionary<string, object?> add)
            {
                foreach (var (k, v) in add) merged[k] = v;
            }
            else
            {
                throw new ReducerException($"merge expects a dictionary update, got {incoming?.GetType().Name ?? "null"}");
            }
            return merged;
        });

    /// <summary>Any <c>(current, incoming) -&gt; next</c>. <c>current</c> is null on the first write.</summary>
    public static Channel Reduce(Func<object?, object?, object?> fn, object? @default = null) =>
        new("custom", @default, (current, incoming) =>
            fn(ReferenceEquals(current, Channel.Unset) ? null : current, incoming));
}

/// <summary>
/// A read-only view of the channel values a node sees (MODEL.md §4).
///
/// Deliberately the checkpoint view, not a live one: a task never observes a
/// sibling's update within its superstep.
/// </summary>
public sealed class State
{
    private readonly IReadOnlyDictionary<string, object?> _values;

    public State(IReadOnlyDictionary<string, object?> values) => _values = values;

    public object? this[string key] => _values.TryGetValue(key, out var v) ? v : null;

    public bool Has(string key) => _values.ContainsKey(key);

    public IEnumerable<string> Keys => _values.Keys;

    /// <summary>Read a channel, cast to <typeparamref name="T"/>.</summary>
    public T Get<T>(string key)
    {
        var value = this[key];
        if (value is T typed) return typed;
        if (value is null) return default!;
        throw new GraphException(
            $"channel \"{key}\" holds {value.GetType().Name}, not {typeof(T).Name}");
    }

    /// <summary>Read a channel, or <paramref name="fallback"/> when unset.</summary>
    public T GetOr<T>(string key, T fallback) => this[key] is T typed ? typed : fallback;

    /// <summary>A list channel as <c>List&lt;T&gt;</c> — the common shape for `Append` channels.</summary>
    public List<T> GetList<T>(string key) =>
        this[key] is IEnumerable<object?> seq ? seq.OfType<T>().ToList() : new List<T>();

    public IReadOnlyDictionary<string, object?> AsDictionary() => _values;

    public override string ToString() =>
        "{" + string.Join(", ", _values.Select(kv => $"{kv.Key}: {kv.Value}")) + "}";
}

/// <summary>Small helpers for building the update dictionary a node returns.</summary>
public static partial class Update
{
    public static Dictionary<string, object?> Of(string key, object? value) => new() { [key] = value };

    public static Dictionary<string, object?> Of(
        string key1, object? value1, string key2, object? value2) =>
        new() { [key1] = value1, [key2] = value2 };

    public static Dictionary<string, object?> Of(
        string key1, object? value1, string key2, object? value2, string key3, object? value3) =>
        new() { [key1] = value1, [key2] = value2, [key3] = value3 };
}
