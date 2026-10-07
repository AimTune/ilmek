using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ilmek;

/// <summary>Options for <see cref="CompiledGraph.ToMermaid"/> and <see cref="Spec.ToMermaid"/>.</summary>
public sealed record MermaidOptions
{
    /// <summary>Flowchart direction: <c>TD</c> (default), <c>TB</c>, <c>BT</c>, <c>LR</c> or <c>RL</c>.</summary>
    public string Direction { get; init; } = "TD";

    /// <summary>
    /// Mark a thread's position: nodes in <see cref="Checkpoint.Next"/> get the
    /// <c>next</c> class (bold), nodes in <see cref="Checkpoint.Pending"/> the
    /// <c>pending</c> class and a ⏸ in their label.
    /// </summary>
    public Checkpoint? Highlight { get; init; }

    /// <summary>Draw router edges. Default true.</summary>
    public bool IncludeRouters { get; init; } = true;
}

/// <summary>
/// Renders a graph as a Mermaid flowchart (MODEL.md §9.1).
///
/// <para>Pure and deterministic: the same graph renders byte-identically in
/// every port (<c>conformance/viz</c>). Solid edges are static or declarative;
/// dashed edges are decided by code — a hand-written guard, or a router (to each
/// of its declared targets, or to a <c>?</c> when it declared none). A node's
/// <see cref="Command"/> goto is invisible here: it is decided inside the node.</para>
/// </summary>
internal static partial class Mermaid
{
    private static readonly HashSet<string> Directions = new(StringComparer.Ordinal) { "TD", "TB", "BT", "LR", "RL" };

    // Words Mermaid's flowchart grammar claims for itself; a node id spelled like
    // one (in any case) breaks the parse, so such a node gets a generated id.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "end", "graph", "flowchart", "subgraph", "style", "class", "classdef",
        "click", "linkstyle", "direction", "default", "call", "href", "interpolate",
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SafeId();

    private enum Kind { Static, Spec, Guard, Router }

    /// <summary>One edge, reduced to what a picture can show.</summary>
    private sealed record VizEdge(Kind Kind, string From, string? To, SpecPredicate? When, IReadOnlyList<string>? Targets);

    public static string Render(CompiledGraph g, MermaidOptions? options)
    {
        var edges = g.Edges.Select(e =>
            e.Router is not null ? new VizEdge(Kind.Router, e.From, null, null, e.Targets)
            : e.SpecWhen is not null ? new VizEdge(Kind.Spec, e.From, e.To, e.SpecWhen, null)
            : e.When is not null ? new VizEdge(Kind.Guard, e.From, e.To, null, null)
            : new VizEdge(Kind.Static, e.From, e.To, null, null));
        return Render(g.NodeOrder, edges.ToList(), options);
    }

    public static string Render(GraphSpec spec, MermaidOptions? options)
    {
        var edges = spec.Edges.Select(e => e.When is not null
            ? new VizEdge(Kind.Spec, e.From, e.To, e.When, null)
            : new VizEdge(Kind.Static, e.From, e.To, null, null));
        return Render(spec.Nodes.Select(n => n.Id).ToList(), edges.ToList(), options);
    }

    private static string Render(IReadOnlyList<string> nodes, List<VizEdge> allEdges, MermaidOptions? options)
    {
        options ??= new MermaidOptions();
        if (!Directions.Contains(options.Direction))
            throw new GraphException($"unknown Mermaid direction \"{options.Direction}\"; use TD, TB, BT, LR or RL");

        var edges = options.IncludeRouters ? allEdges : allEdges.Where(e => e.Kind != Kind.Router).ToList();

        // Every node referenced by an edge is drawn, even one a spec forgot to declare.
        var order = new List<string>(nodes);
        var known = new HashSet<string>(order, StringComparer.Ordinal);
        foreach (var e in edges)
        {
            var names = e.Kind == Kind.Router ? new[] { e.From }.Concat(e.Targets ?? Array.Empty<string>()) : new[] { e.From, e.To! };
            foreach (var name in names)
                if (name != Graph.Start && name != Graph.End && known.Add(name)) order.Add(name);
        }

        var used = new HashSet<string>(StringComparer.Ordinal) { Graph.Start, Graph.End };
        foreach (var name in order) if (IsSafeId(name)) used.Add(name);
        string Fresh(string id)
        {
            while (used.Contains(id)) id += "_";
            used.Add(id);
            return id;
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal) { [Graph.Start] = Graph.Start, [Graph.End] = Graph.End };
        for (var i = 0; i < order.Count; i++)
            ids[order[i]] = IsSafeId(order[i]) ? order[i] : Fresh(Invariant($"n{i}"));

        var unknownTargets = new Dictionary<VizEdge, string>(ReferenceEqualityComparer.Instance);
        var routerCount = 0;
        foreach (var e in edges)
            if (e.Kind == Kind.Router && e.Targets is null) unknownTargets[e] = Fresh(Invariant($"r{routerCount++}"));

        var pending = new HashSet<string>(options.Highlight?.Pending.Select(p => p.Node) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var next = new HashSet<string>(
            options.Highlight?.Next.Select(t => t.Node).Where(n => !pending.Contains(n)) ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);

        var usesEnd = edges.Any(e => e.Kind == Kind.Router ? (e.Targets?.Contains(Graph.End) ?? false) : e.To == Graph.End);

        var lines = new List<string> { $"flowchart {options.Direction}", $"  {Graph.Start}([START])" };
        foreach (var name in order)
            lines.Add($"  {ids[name]}[\"{Label(pending.Contains(name) ? $"⏸ {name}" : name)}\"]");
        foreach (var id in unknownTargets.Values) lines.Add($"  {id}{{\"?\"}}");
        if (usesEnd) lines.Add($"  {Graph.End}([END])");

        foreach (var e in edges)
        {
            var from = ids[e.From];
            switch (e.Kind)
            {
                case Kind.Static:
                    lines.Add($"  {from} --> {ids[e.To!]}");
                    break;
                case Kind.Spec:
                    lines.Add($"  {from} -->|\"{Label(PredicateLabel(e.When!))}\"| {ids[e.To!]}");
                    break;
                case Kind.Guard:
                    lines.Add($"  {from} -.->|\"guard\"| {ids[e.To!]}");
                    break;
                case Kind.Router:
                    if (e.Targets is null) lines.Add($"  {from} -.-> {unknownTargets[e]}");
                    else foreach (var t in e.Targets) lines.Add($"  {from} -.-> {ids[t]}");
                    break;
            }
        }

        List<string> Marked(HashSet<string> set) => order.Where(set.Contains).Select(n => ids[n]).ToList();
        var nextIds = Marked(next);
        var pendingIds = Marked(pending);
        if (nextIds.Count > 0)
        {
            lines.Add("  classDef next stroke-width:3px");
            lines.Add($"  class {string.Join(",", nextIds)} next");
        }
        if (pendingIds.Count > 0)
        {
            lines.Add("  classDef pending stroke-width:3px,stroke-dasharray:5 3");
            lines.Add($"  class {string.Join(",", pendingIds)} pending");
        }

        return string.Join("\n", lines) + "\n";
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static bool IsSafeId(string name) =>
        SafeId().IsMatch(name) && !Keywords.Contains(name.ToLowerInvariant());

    /// <summary>
    /// A predicate as a reader would write it. The operator chosen is the one the
    /// engine evaluates — the first present of Eq, Neq, In, Gt, Lt, Truthy.
    /// </summary>
    private static string PredicateLabel(SpecPredicate p)
    {
        if (p.Eq is not null) return $"{p.Channel} == {Value(p.Eq)}";
        if (p.Neq is not null) return $"{p.Channel} != {Value(p.Neq)}";
        if (p.In is not null) return $"{p.Channel} in [{string.Join(", ", p.In.Select(Value))}]";
        if (p.Gt is not null) return $"{p.Channel} > {Value(p.Gt.Value)}";
        if (p.Lt is not null) return $"{p.Channel} < {Value(p.Lt.Value)}";
        if (p.Truthy is not null) return p.Truthy.Value ? $"truthy({p.Channel})" : $"not truthy({p.Channel})";
        return p.Channel;
    }

    /// <summary>A value as JavaScript's <c>JSON.stringify</c> writes it, so both ports print the same label.</summary>
    private static string Value(object? value)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case JsonElement el:
                WriteJson(sb, el);
                break;
            case string s:
                WriteString(sb, s);
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                WriteNumber(sb, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case IDictionary dict:
                sb.Append('{');
                var firstKey = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!firstKey) sb.Append(',');
                    firstKey = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "");
                    sb.Append(':');
                    WriteValue(sb, entry.Value);
                }
                sb.Append('}');
                break;
            case IEnumerable list:
                sb.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                break;
            default:
                WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    private static void WriteJson(StringBuilder sb, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String: WriteString(sb, el.GetString()!); break;
            case JsonValueKind.Number: WriteNumber(sb, el.GetDouble()); break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Array:
                sb.Append('[');
                var first = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteJson(sb, item);
                }
                sb.Append(']');
                break;
            case JsonValueKind.Object:
                sb.Append('{');
                var firstProp = true;
                foreach (var prop in el.EnumerateObject())
                {
                    if (!firstProp) sb.Append(',');
                    firstProp = false;
                    WriteString(sb, prop.Name);
                    sb.Append(':');
                    WriteJson(sb, prop.Value);
                }
                sb.Append('}');
                break;
            default: sb.Append("null"); break;
        }
    }

    // JavaScript prints an integral double without a fraction and switches to
    // exponent form only from 1e21; "R" agrees for the values a label holds.
    private static void WriteNumber(StringBuilder sb, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
        else if (d == Math.Floor(d) && Math.Abs(d) < 1e21) sb.Append(((decimal)d).ToString(CultureInfo.InvariantCulture));
        else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// Escape text for a quoted Mermaid label. <c>#</c> opens an entity code, so it
    /// is escaped first; quotes, angle brackets and backticks would otherwise end
    /// the string, open HTML, or turn it into a markdown string.
    /// </summary>
    private static string Label(string text) => text
        .Replace("#", "#35;", StringComparison.Ordinal)
        .Replace("\"", "#quot;", StringComparison.Ordinal)
        .Replace("<", "#lt;", StringComparison.Ordinal)
        .Replace(">", "#gt;", StringComparison.Ordinal)
        .Replace("`", "#96;", StringComparison.Ordinal)
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace('\r', ' ')
        .Replace('\n', ' ');
}
