using Ilmek;

namespace Ilmek.Tests;

/// <summary>
/// Graphs as data (MODEL.md §9): refusals, malformed documents, and the
/// declarative predicates — which must route the same document the same way in
/// both languages. Mirrors ts/packages/core/test/spec.test.ts.
/// </summary>
public class SpecTests
{
    private static readonly IReadOnlyDictionary<string, NodeBuilder> Registry = new Dictionary<string, NodeBuilder>
    {
        ["set"] = cfg => (_, _) => new ValueTask<object?>(Update.Of((string)cfg["channel"]!, cfg["value"])),
        ["say"] = cfg => (_, _) => new ValueTask<object?>(Update.Of("messages", cfg["text"])),
    };

    /// <summary>A document whose single guarded edge decides between "hit" and nothing.</summary>
    private static GraphSpec Guarded(object? value, SpecPredicate when) => new()
    {
        Name = "guarded",
        Channels = new Dictionary<string, SpecChannel>
        {
            ["messages"] = new("append"),
            ["v"] = new("last_write"),
        },
        Nodes = new List<SpecNode>
        {
            new("write", "set", new Dictionary<string, object?> { ["channel"] = "v", ["value"] = value }),
            new("hit", "say", new Dictionary<string, object?> { ["text"] = "hit" }),
        },
        Edges = new List<SpecEdge>
        {
            new(Graph.Start, "write"),
            new("write", "hit", when),
            new("hit", Graph.End),
        },
    };

    private static async Task<bool> Routes(object? value, SpecPredicate when)
    {
        var result = await Spec.FromSpec(Guarded(value, when), Registry).Compile().RunAsync();
        Assert.Equal(RunStatus.Done, result.Status);
        return result.State!.GetList<string>("messages").Contains("hit");
    }

    // ── predicates ──────────────────────────────────────────────────────────

    [Fact(DisplayName = "eq on a number matches across int, long and double — a JSON document has one number type")]
    public async Task EqIsNumericAcrossClrTypes()
    {
        // A channel that came back from SQLite holds a long; a spec written in C#
        // naturally says Eq = 3 (an int). TS routes this (3 === 3).
        Assert.True(await Routes(3L, new SpecPredicate { Channel = "v", Eq = 3 }));
        Assert.True(await Routes(3.0, new SpecPredicate { Channel = "v", Eq = 3L }));
        Assert.False(await Routes(4L, new SpecPredicate { Channel = "v", Eq = 3 }));
        Assert.False(await Routes("3", new SpecPredicate { Channel = "v", Eq = 3 })); // no string coercion
    }

    [Fact(DisplayName = "neq and in use the same numeric equality")]
    public async Task NeqAndInAreNumeric()
    {
        Assert.False(await Routes(3L, new SpecPredicate { Channel = "v", Neq = 3 }));
        Assert.True(await Routes(2L, new SpecPredicate { Channel = "v", Neq = 3 }));
        Assert.True(await Routes(2L, new SpecPredicate { Channel = "v", In = new object?[] { 1, 2, 3 } }));
        Assert.True(await Routes("b", new SpecPredicate { Channel = "v", In = new object?[] { "a", "b" } }));
        Assert.False(await Routes("z", new SpecPredicate { Channel = "v", In = new object?[] { "a", "b" } }));
    }

    [Fact(DisplayName = "gt and lt compare numbers only; a string or bool channel simply does not match")]
    public async Task GtLtNumbersOnly()
    {
        Assert.True(await Routes(5L, new SpecPredicate { Channel = "v", Gt = 3 }));
        Assert.False(await Routes(3L, new SpecPredicate { Channel = "v", Gt = 3 }));
        Assert.True(await Routes(2.5, new SpecPredicate { Channel = "v", Lt = 3 }));

        // TS: typeof a === "number" — "5" and true are not numbers. .NET used to
        // Convert.ToDouble them: "5" matched, and "abc" threw a FormatException
        // out of the guard, killing the run.
        Assert.False(await Routes("5", new SpecPredicate { Channel = "v", Gt = 3 }));
        Assert.False(await Routes("abc", new SpecPredicate { Channel = "v", Gt = 3 }));
        Assert.False(await Routes(true, new SpecPredicate { Channel = "v", Gt = 0 }));
        Assert.False(await Routes(null, new SpecPredicate { Channel = "v", Lt = 3 }));
    }

    [Fact(DisplayName = "truthy follows the reference's emptiness: null, false, \"\", 0, empty list and map are falsy")]
    public async Task TruthyMatchesReference()
    {
        var truthy = new SpecPredicate { Channel = "v", Truthy = true };
        Assert.False(await Routes(null, truthy));
        Assert.False(await Routes(false, truthy));
        Assert.False(await Routes("", truthy));
        Assert.False(await Routes(0L, truthy));   // TS Boolean(0) is false; .NET used to say true
        Assert.False(await Routes(0.0, truthy));
        Assert.False(await Routes(double.NaN, truthy));
        Assert.False(await Routes(new List<object?>(), truthy));
        Assert.False(await Routes(new Dictionary<string, object?>(), truthy));

        Assert.True(await Routes("x", truthy));
        Assert.True(await Routes(-1L, truthy));
        Assert.True(await Routes(new List<object?> { 1L }, truthy));

        Assert.True(await Routes(0L, new SpecPredicate { Channel = "v", Truthy = false }));
    }

    [Fact(DisplayName = "a predicate with no operator is refused at build time")]
    public void PredicateWithoutOperator()
    {
        var ex = Assert.Throws<GraphException>(() =>
            Spec.FromSpec(Guarded(1L, new SpecPredicate { Channel = "v" }), Registry));
        Assert.Contains("no known operator", ex.Message);
    }

    [Fact(DisplayName = "a predicate may only reference a declared channel")]
    public void PredicateOnUndeclaredChannel()
    {
        var ex = Assert.Throws<GraphException>(() =>
            Spec.FromSpec(Guarded(1L, new SpecPredicate { Channel = "ghost", Eq = 1 }), Registry));
        Assert.Contains("\"ghost\", which this spec does not declare", ex.Message);
    }

    [Fact(DisplayName = "a predicate with a null channel (malformed JSON) is refused, not a crash")]
    public void PredicateWithNullChannel()
    {
        Assert.Throws<GraphException>(() =>
            Spec.FromSpec(Guarded(1L, new SpecPredicate { Channel = null!, Eq = 1 }), Registry));
    }

    // ── malformed documents ─────────────────────────────────────────────────

    [Fact(DisplayName = "an unknown reducer name is refused and lists the built-ins")]
    public void UnknownReducer()
    {
        var spec = Guarded(1L, new SpecPredicate { Channel = "v", Eq = 1 }) with
        {
            Channels = new Dictionary<string, SpecChannel> { ["messages"] = new("eval_this") },
        };
        var ex = Assert.Throws<GraphException>(() => Spec.FromSpec(spec, Registry));
        Assert.Contains("unknown reducer \"eval_this\"", ex.Message);
        Assert.Contains("\"append\"", ex.Message);
    }

    [Fact(DisplayName = "an unknown node type names what the registry does know")]
    public void UnknownNodeType()
    {
        var spec = new GraphSpec { Nodes = new List<SpecNode> { new("x", "nope") } };
        var ex = Assert.Throws<GraphException>(() => Spec.FromSpec(spec, Registry));
        Assert.Contains("not in the registry", ex.Message);
        Assert.Contains("set", ex.Message);
    }

    [Fact(DisplayName = "a node with a null type (malformed JSON) is refused with a GraphException")]
    public void NullNodeType()
    {
        var spec = new GraphSpec { Nodes = new List<SpecNode> { new("x", null!) } };
        var ex = Assert.Throws<GraphException>(() => Spec.FromSpec(spec, Registry));
        Assert.Contains("has no \"type\"", ex.Message);
    }

    [Fact(DisplayName = "a registry entry that builds null is refused at build time, not at run time")]
    public void RegistryReturningNull()
    {
        var spec = new GraphSpec { Nodes = new List<SpecNode> { new("x", "broken") } };
        var registry = new Dictionary<string, NodeBuilder> { ["broken"] = _ => null! };
        var ex = Assert.Throws<GraphException>(() => Spec.FromSpec(spec, registry));
        Assert.Contains("\"broken\" returned null", ex.Message);
    }

    [Fact(DisplayName = "FromSpec without a registry refuses any node")]
    public void NoRegistry()
    {
        var spec = new GraphSpec { Nodes = new List<SpecNode> { new("x", "say") } };
        Assert.Throws<GraphException>(() => Spec.FromSpec(spec));
    }

    [Fact(DisplayName = "a spec edge to an unknown node is refused at compile")]
    public void EdgeToUnknownNode()
    {
        var spec = Guarded(1L, new SpecPredicate { Channel = "v", Eq = 1 }) with
        {
            Edges = new List<SpecEdge> { new(Graph.Start, "write"), new("write", "nowhere") },
        };
        var ex = Assert.Throws<GraphException>(() => Spec.FromSpec(spec, Registry).Compile());
        Assert.Contains("unknown node \"nowhere\"", ex.Message);
    }

    [Fact(DisplayName = "a spec with a duplicate node id or a reserved id is refused")]
    public void DuplicateAndReservedSpecNodes()
    {
        var dup = new GraphSpec { Nodes = new List<SpecNode> { new("x", "say"), new("x", "say") } };
        Assert.Contains("duplicate node", Assert.Throws<GraphException>(() => Spec.FromSpec(dup, Registry)).Message);

        var reserved = new GraphSpec { Nodes = new List<SpecNode> { new(Graph.End, "say") } };
        Assert.Contains("reserved", Assert.Throws<GraphException>(() => Spec.FromSpec(reserved, Registry)).Message);
    }

    // ── malformed documents: the TS case table (boundaries.test.ts) ───────────
    //
    // A spec is a stored document, so a JSON deserializer can hand FromSpec nulls
    // the record annotations rule out. Each must be a GraphException naming the
    // bad part, with the TS message — never a NullReferenceException or
    // ArgumentNullException from inside the builder.

    private static GraphSpec Base() => new()
    {
        Name = "doc",
        Channels = new Dictionary<string, SpecChannel> { ["intent"] = new("last_write") },
        Nodes = new List<SpecNode> { new("a", "say") },
        Edges = new List<SpecEdge> { new(Graph.Start, "a") },
    };

    private static string Refused(Func<object> build) => Assert.Throws<GraphException>(() => build()).Message;

    [Fact(DisplayName = "a null spec is a GraphException")]
    public void NullSpec() =>
        Assert.Equal("a graph spec must be an object, got null", Refused(() => Spec.FromSpec(null!, Registry)));

    [Fact(DisplayName = "a channel config that is null is a GraphException naming the channel")]
    public void NullChannelConfig() =>
        Assert.Contains("channel \"intent\": config must be an object, got null",
            Refused(() => Spec.FromSpec(Base() with { Channels = new Dictionary<string, SpecChannel> { ["intent"] = null! } }, Registry)));

    [Fact(DisplayName = "a channel with no reducer named defaults to last_write")]
    public void NullReducerDefaultsToLastWrite()
    {
        var g = Spec.FromSpec(Base() with { Channels = new Dictionary<string, SpecChannel> { ["intent"] = new(null!) } }, Registry).Compile();
        Assert.Equal("last_write", Spec.ToSpec(g).Channels["intent"].Reducer);
    }

    [Fact(DisplayName = "a node that is null is a GraphException")]
    public void NullNode() =>
        Assert.Contains("spec node must be an object", Refused(() => Spec.FromSpec(Base() with { Nodes = new List<SpecNode> { null! } }, Registry)));

    [Theory(DisplayName = "a node with no id is a GraphException")]
    [InlineData(null, "node name must be a non-empty string, got null")]
    [InlineData("", "node name must be a non-empty string, got \"\"")]
    public void NodeWithoutId(string? id, string message) =>
        Assert.Contains(message, Refused(() => Spec.FromSpec(Base() with { Nodes = new List<SpecNode> { new(id!, "say") } }, Registry)));

    [Fact(DisplayName = "a channel with an empty name is a GraphException")]
    public void ChannelWithEmptyName() =>
        Assert.Contains("channel name must be a non-empty string, got \"\"",
            Refused(() => Spec.FromSpec(Base() with { Channels = new Dictionary<string, SpecChannel> { [""] = new("append") } }, Registry)));

    [Fact(DisplayName = "an edge that is null is a GraphException")]
    public void NullEdge() =>
        Assert.Contains("spec edge must be an object", Refused(() => Spec.FromSpec(Base() with { Edges = new List<SpecEdge> { null! } }, Registry)));

    [Fact(DisplayName = "an edge with a null end fails at compile, naming it")]
    public void EdgeWithNullEnds()
    {
        Assert.Contains("edge from unknown node null",
            Refused(() => Spec.FromSpec(Base() with { Edges = new List<SpecEdge> { new(Graph.Start, "a"), new(null!, "a") } }, Registry).Compile()));
        Assert.Contains("edge \"a\" -> unknown node null",
            Refused(() => Spec.FromSpec(Base() with { Edges = new List<SpecEdge> { new(Graph.Start, "a"), new("a", null!) } }, Registry).Compile()));
    }

    [Fact(DisplayName = "a null registry entry is \"not in the registry\", not a NullReferenceException")]
    public void NullRegistryEntry() =>
        Assert.Contains("not in the registry",
            Refused(() => Spec.FromSpec(Base(), new Dictionary<string, NodeBuilder> { ["say"] = null! })));

    [Fact(DisplayName = "a predicate with no channel is malformed")]
    public void PredicateWithoutChannelIsMalformed() =>
        Assert.Contains("is malformed",
            Refused(() => Spec.FromSpec(Base() with { Edges = new List<SpecEdge> { new(Graph.Start, "a", new SpecPredicate { Channel = null!, Eq = 1 }) } }, Registry)));

    [Fact(DisplayName = "a document missing channels, nodes and edges builds an empty builder that will not compile")]
    public void MissingCollections() =>
        Assert.Contains("no entry edge",
            Refused(() => Spec.FromSpec(new GraphSpec { Name = null, Channels = null!, Nodes = null!, Edges = null! }, Registry).Compile()));

    // ── eq / neq with null — a valid predicate, as in TS (`"eq" in pred`) ─────

    [Fact(DisplayName = "Eq = null is a predicate: it matches a null channel and nothing else")]
    public async Task EqNullIsAPredicate()
    {
        var eqNull = new SpecPredicate { Channel = "v", Eq = null };
        Assert.True(await Routes(null, eqNull));
        Assert.False(await Routes("x", eqNull));
        Assert.False(await Routes(0L, eqNull));
        Assert.False(await Routes(false, eqNull));
    }

    [Fact(DisplayName = "Neq = null is a predicate: it matches any non-null channel")]
    public async Task NeqNullIsAPredicate()
    {
        var neqNull = new SpecPredicate { Channel = "v", Neq = null };
        Assert.False(await Routes(null, neqNull));
        Assert.True(await Routes("x", neqNull));
        Assert.True(await Routes(0L, neqNull));
    }

    [Fact(DisplayName = "In may hold null")]
    public async Task InMayHoldNull()
    {
        Assert.True(await Routes(null, new SpecPredicate { Channel = "v", In = new object?[] { "a", null } }));
        Assert.False(await Routes(null, new SpecPredicate { Channel = "v", In = new object?[] { "a" } }));
    }

    [Fact(DisplayName = "a predicate's null operator survives JSON: Eq = null and Neq = null round-trip, unset operators stay unset")]
    public async Task NullOperatorsRoundTripThroughJson()
    {
        foreach (var (pred, json) in new[]
        {
            (new SpecPredicate { Channel = "v", Eq = null }, """{"Channel":"v","Eq":null}"""),
            (new SpecPredicate { Channel = "v", Neq = null }, """{"Channel":"v","Neq":null}"""),
            (new SpecPredicate { Channel = "v", Gt = 3 }, """{"Channel":"v","Gt":3}"""),
            (new SpecPredicate { Channel = "v", In = new object?[] { 1L, "a", null } }, """{"Channel":"v","In":[1,"a",null]}"""),
        })
        {
            Assert.Equal(json, System.Text.Json.JsonSerializer.Serialize(pred));
            var back = System.Text.Json.JsonSerializer.Deserialize<SpecPredicate>(json)!;
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(pred), System.Text.Json.JsonSerializer.Serialize(back));
        }

        // Read from a TS-style document (camelCase keys), values come back as plain data.
        var ts = System.Text.Json.JsonSerializer.Deserialize<SpecPredicate>("""{"channel":"v","eq":3}""")!;
        Assert.True(await Routes(3L, ts));
        var tsNull = System.Text.Json.JsonSerializer.Deserialize<SpecPredicate>("""{"channel":"v","neq":null}""")!;
        Assert.True(await Routes("x", tsNull));
        Assert.False(await Routes(null, tsNull));
    }

    [Fact(DisplayName = "a document written by the old serializer — every operator present, unused ones null — still routes on the real one")]
    public async Task LegacyAllNullOperatorsYieldToTheGivenOne()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<SpecPredicate>(
            """{"Channel":"v","Eq":null,"Neq":null,"In":null,"Gt":3,"Lt":null,"Truthy":null}""")!;
        Assert.True(await Routes(5L, legacy));
        Assert.False(await Routes(null, legacy));
    }

    // ── round-trip and refusals ─────────────────────────────────────────────

    [Fact(DisplayName = "ToSpec keeps every predicate, config and the edge order")]
    public void RoundTripKeepsPredicates()
    {
        var spec = Guarded(1L, new SpecPredicate { Channel = "v", Gt = 0.5 });
        var round = Spec.ToSpec(Spec.FromSpec(spec, Registry).Compile());

        Assert.Equal(spec.Edges.Select(e => (e.From, e.To)), round.Edges.Select(e => (e.From, e.To)));
        Assert.Equal(0.5, round.Edges[1].When!.Gt);
        Assert.Null(round.Edges[0].When);
        Assert.Equal("v", round.Nodes[0].Config!["channel"]);
        Assert.Equal("last_write", round.Channels["v"].Reducer);
    }

    [Fact(DisplayName = "a node with no type will not serialize")]
    public void UntypedNodeWillNotSerialize()
    {
        var g = Graph.Create("anon")
            .Channel("x", Channels.LastWrite())
            .Node("a", (_, _) => null)
            .Edge(Graph.Start, "a")
            .Compile();
        Assert.Contains("node \"a\" has no type", Assert.Throws<GraphException>(() => Spec.ToSpec(g)).Message);
    }

    [Fact(DisplayName = "a hand-written guard with no declarative equivalent will not serialize")]
    public void HandWrittenGuardWillNotSerialize()
    {
        var g = Graph.Create("guarded")
            .Channel("x", Channels.LastWrite())
            .Node("a", (_, _) => null, type: "noop")
            .Edge(Graph.Start, "a")
            .Edge("a", Graph.End, when: (s, _) => s["x"] is 1L)
            .Compile();
        Assert.Contains("hand-written function", Assert.Throws<GraphException>(() => Spec.ToSpec(g)).Message);
    }

    [Fact(DisplayName = "a custom reducer will not serialize")]
    public void CustomReducerWillNotSerialize()
    {
        var g = Graph.Create("custom")
            .Channel("total", Channels.Reduce((c, i) => i, 0L))
            .Node("a", (_, _) => null, type: "noop")
            .Edge(Graph.Start, "a")
            .Compile();
        Assert.Contains("custom reducer function", Assert.Throws<GraphException>(() => Spec.ToSpec(g)).Message);
    }

    [Fact(DisplayName = "a code guard WITH a declarative twin serializes as the twin")]
    public void GuardWithSpecWhenSerializes()
    {
        var twin = new SpecPredicate { Channel = "x", Eq = 1 };
        var g = Graph.Create("twin")
            .Channel("x", Channels.LastWrite())
            .Node("a", (_, _) => null, type: "noop")
            .Edge(Graph.Start, "a")
            .Edge("a", Graph.End, when: (s, _) => Equals(s["x"], 1), specWhen: twin)
            .Compile();
        Assert.Same(twin, Spec.ToSpec(g).Edges[1].When);
    }
}
