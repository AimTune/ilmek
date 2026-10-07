using Ilmek;

namespace Ilmek.Tests;

/// <summary>
/// Malformed and hostile graphs at every public boundary (MODEL.md §3, §4, §14,
/// §15): build-time refusals, then the routing and node-return errors that can
/// only surface at run time.
/// </summary>
public class GraphValidationTests
{
    private static NodeFn Noop => (_, _) => new ValueTask<object?>((object?)null);

    // ── builder ─────────────────────────────────────────────────────────────

    [Theory(DisplayName = "the reserved names __start__ and __end__ cannot be nodes")]
    [InlineData(Graph.Start)]
    [InlineData(Graph.End)]
    public void ReservedNodeNames(string id)
    {
        var ex = Assert.Throws<GraphException>(() => Graph.Create().Node(id, Noop));
        Assert.Contains("reserved", ex.Message);
    }

    [Fact(DisplayName = "a duplicate node or channel is refused, naming it")]
    public void Duplicates()
    {
        Assert.Contains("duplicate node \"a\"",
            Assert.Throws<GraphException>(() => Graph.Create().Node("a", Noop).Node("a", Noop)).Message);
        Assert.Contains("duplicate channel \"c\"",
            Assert.Throws<GraphException>(() =>
                Graph.Create().Channel("c", Channels.Append()).Channel("c", Channels.LastWrite())).Message);
    }

    [Theory(DisplayName = "a retry policy with MaxAttempts below 1 is refused")]
    [InlineData(0)]
    [InlineData(-3)]
    public void RetryMaxAttemptsBelowOne(int attempts)
    {
        var ex = Assert.Throws<GraphException>(() =>
            Graph.Create().Node("a", Noop, retry: new RetryPolicy { MaxAttempts = attempts }));
        Assert.Contains("MaxAttempts", ex.Message);
    }

    [Fact(DisplayName = "a null node id or channel name is rejected at the builder")]
    public void NullNames()
    {
        Assert.ThrowsAny<ArgumentException>(() => Graph.Create().Node(null!, Noop));
        Assert.ThrowsAny<ArgumentException>(() => Graph.Create().Channel(null!, Channels.LastWrite()));
    }

    [Fact(DisplayName = "an empty-string node id is a legal, if odd, name — pinned as accepted")]
    public async Task EmptyNodeIdAccepted()
    {
        var g = Graph.Create()
            .Channel("log", Channels.Append())
            .Node("", (_, _) => Update.Of("log", "ran"))
            .Edge(Graph.Start, "").Edge("", Graph.End)
            .Compile();
        Assert.Equal(new List<string> { "ran" }, (await g.RunAsync()).State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "Compile refuses an edge from an unknown node, including from __end__")]
    public void EdgeFromUnknown()
    {
        Assert.Contains("edge from unknown node \"ghost\"", Assert.Throws<GraphException>(() =>
            Graph.Create().Node("a", Noop).Edge(Graph.Start, "a").Edge("ghost", "a").Compile()).Message);
        Assert.Throws<GraphException>(() =>
            Graph.Create().Node("a", Noop).Edge(Graph.Start, "a").Edge(Graph.End, "a").Compile());
    }

    [Fact(DisplayName = "Compile refuses an edge to an unknown node, including back to __start__")]
    public void EdgeToUnknown()
    {
        Assert.Contains("unknown node \"ghost\"", Assert.Throws<GraphException>(() =>
            Graph.Create().Node("a", Noop).Edge(Graph.Start, "a").Edge("a", "ghost").Compile()).Message);
        Assert.Throws<GraphException>(() =>
            Graph.Create().Node("a", Noop).Edge(Graph.Start, "a").Edge("a", Graph.Start).Compile());
    }

    [Fact(DisplayName = "Compile refuses a graph with no entry edge")]
    public void NoEntryEdge()
    {
        Assert.Contains("no entry edge", Assert.Throws<GraphException>(() =>
            Graph.Create().Node("a", Noop).Edge("a", Graph.End).Compile()).Message);
        Assert.Throws<GraphException>(() => Graph.Create().Compile()); // empty graph
    }

    [Fact(DisplayName = "an entry router alone is a valid entry edge")]
    public async Task EntryRouterIsAnEntry()
    {
        var g = Graph.Create()
            .Channel("log", Channels.Append())
            .Node("a", (_, _) => Update.Of("log", "a"))
            .Router(Graph.Start, (_, _) => new[] { "a" })
            .Compile();
        Assert.Equal(new List<string> { "a" }, (await g.RunAsync()).State!.GetList<string>("log"));
    }

    // ── routing at run time ─────────────────────────────────────────────────

    private static CompiledGraph RouterGraph(RouterFn router) =>
        Graph.Create("routed")
            .Channel("log", Channels.Append())
            .Node("a", (_, _) => Update.Of("log", "a"))
            .Node("b", (_, _) => Update.Of("log", "b"))
            .Edge(Graph.Start, "a")
            .Router("a", router)
            .Compile();

    [Fact(DisplayName = "a router naming an unknown node fails the run with a GraphException listing known nodes")]
    public async Task RouterToUnknownNode()
    {
        var ex = await Assert.ThrowsAsync<GraphException>(() => RouterGraph((_, _) => new[] { "nowhere" }).RunAsync());
        Assert.Contains("produced \"nowhere\"", ex.Message);
        Assert.Contains("Known nodes: [a, b]", ex.Message);
    }

    [Fact(DisplayName = "a router yielding a null target fails with a GraphException")]
    public async Task RouterNullTarget()
    {
        var ex = await Assert.ThrowsAsync<GraphException>(() => RouterGraph((_, _) => new object[] { null! }).RunAsync());
        Assert.Contains("produced null", ex.Message);
    }

    [Fact(DisplayName = "a router returning null itself fails with a GraphException, as in TS — not ArgumentNullException")]
    public async Task RouterReturningNull()
    {
        var ex = await Assert.ThrowsAsync<GraphException>(() => RouterGraph((_, _) => null!).RunAsync());
        Assert.Contains("produced null", ex.Message);
    }

    [Fact(DisplayName = "a router yielding a non-string, non-Send value fails naming its type")]
    public async Task RouterWrongType()
    {
        var ex = await Assert.ThrowsAsync<GraphException>(() => RouterGraph((_, _) => new object[] { 42 }).RunAsync());
        Assert.Contains("Int32", ex.Message);
    }

    [Fact(DisplayName = "a router may end the run, and duplicate targets run once")]
    public async Task RouterEndAndDedup()
    {
        Assert.Equal(new List<string> { "a" },
            (await RouterGraph((_, _) => new[] { Graph.End }).RunAsync()).State!.GetList<string>("log"));
        Assert.Equal(new List<string> { "a" },
            (await RouterGraph((_, _) => Array.Empty<object>()).RunAsync()).State!.GetList<string>("log"));
        Assert.Equal(new List<string> { "a", "b" },
            (await RouterGraph((_, _) => new[] { "b", "b", Graph.End, "b" }).RunAsync()).State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "a send to an unknown node fails the run with a GraphException")]
    public async Task SendToUnknownNode()
    {
        var ex = await Assert.ThrowsAsync<GraphException>(() =>
            RouterGraph((_, _) => new object[] { new Send("ghost", null) }).RunAsync());
        Assert.Contains("\"ghost\"", ex.Message);
    }

    [Fact(DisplayName = "a command goto to an unknown node fails the run with a GraphException")]
    public async Task CommandGotoUnknown()
    {
        var g = Graph.Create()
            .Node("a", (_, _) => Command.Goto_("ghost"))
            .Edge(Graph.Start, "a")
            .Compile();
        await Assert.ThrowsAsync<GraphException>(() => g.RunAsync());
    }

    [Fact(DisplayName = "two predecessors pointing at one node schedule it once (fan-in dedup)")]
    public async Task FanInDedups()
    {
        var runs = 0;
        var g = Graph.Create()
            .Node("a", Noop).Node("b", Noop)
            .Node("join", (_, _) => { Interlocked.Increment(ref runs); return null; })
            .Edge(Graph.Start, "a").Edge(Graph.Start, "b")
            .Edge("a", "join").Edge("b", "join").Edge("a", "join")
            .Compile();
        await g.RunAsync();
        Assert.Equal(1, runs);
    }

    [Fact(DisplayName = "a node with no outgoing edges simply ends its branch")]
    public async Task NoOutgoingEdgeEnds()
    {
        var g = Graph.Create().Channel("log", Channels.Append())
            .Node("a", (_, _) => Update.Of("log", "a"))
            .Edge(Graph.Start, "a")
            .Compile();
        var result = await g.RunAsync();
        Assert.Equal(RunStatus.Done, result.Status);
        Assert.Equal(new List<string> { "a" }, result.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "a throwing guard propagates out of the run — routers and guards run at plan time")]
    public async Task ThrowingGuardPropagates()
    {
        var g = Graph.Create()
            .Node("a", Noop).Node("b", Noop)
            .Edge(Graph.Start, "a")
            .Edge("a", "b", when: (_, _) => throw new InvalidOperationException("guard broke"))
            .Compile();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => g.RunAsync());
        Assert.Equal("guard broke", ex.Message);
    }

    [Fact(DisplayName = "a guard cannot step or interrupt — it has no journal — and its Emit goes nowhere")]
    public async Task GuardHasNoJournal()
    {
        Exception? stepError = null, interruptError = null;
        var g = Graph.Create()
            .Node("a", Noop).Node("b", Noop)
            .Edge(Graph.Start, "a")
            .Edge("a", "b", when: (_, ctx) =>
            {
                try { ctx.StepAsync("s", () => 1); } catch (GraphException e) { stepError = e; }
                try { ctx.InterruptAsync<string>(); } catch (GraphException e) { interruptError = e; }
                ctx.Emit("from a guard");
                ctx.EmitToken("tok");
                Assert.Empty(ctx.Journal);
                Assert.Equal("", ctx.Node);
                return true;
            })
            .Compile();

        var result = await g.RunAsync();
        Assert.Equal(RunStatus.Done, result.Status);
        Assert.Contains("not available in a router or guard", stepError!.Message);
        Assert.Contains("InterruptAsync", interruptError!.Message);
        Assert.DoesNotContain(result.Events, e => e is CustomEvent);
    }

    // ── node returns ────────────────────────────────────────────────────────

    [Theory(DisplayName = "a node returning something other than an update, null or a Command ends the run in error")]
    [InlineData(42)]
    [InlineData("a string")]
    [InlineData(true)]
    public async Task NonDictionaryReturn(object returned)
    {
        var g = Graph.Create().Node("bad", (_, _) => returned).Edge(Graph.Start, "bad").Compile();
        var result = await g.RunAsync();

        Assert.Equal(RunStatus.Error, result.Status);
        var (node, error) = Assert.Single(result.Errors);
        Assert.Equal("bad", node);
        Assert.IsType<GraphException>(error);
        Assert.Contains($"returned {returned.GetType().Name}", error.Message);
    }

    [Fact(DisplayName = "a node may return a mutable IDictionary, not only a read-only one")]
    public async Task MutableDictionaryReturn()
    {
        var g = Graph.Create().Channel("v", Channels.LastWrite())
            .Node("a", (_, _) => (IDictionary<string, object?>)new SortedDictionary<string, object?> { ["v"] = "ok" })
            .Edge(Graph.Start, "a")
            .Compile();
        Assert.Equal("ok", (await g.RunAsync()).State!["v"]);
    }

    [Fact(DisplayName = "a write to an undeclared channel ends the run in error, naming node and channel, state untouched")]
    public async Task UndeclaredChannelWrite()
    {
        var cp = new InMemoryCheckpointer();
        var g = Graph.Create()
            .Channel("log", Channels.Append())
            .Node("ok", (_, _) => Update.Of("log", "fine"))
            .Node("rogue", (_, _) => Update.Of("ghost", 1L))
            .Edge(Graph.Start, "ok").Edge(Graph.Start, "rogue")
            .Compile();

        var result = await g.RunAsync(null, new RunOptions { ThreadId = "t", Checkpointer = cp });

        Assert.Equal(RunStatus.Error, result.Status);
        var (node, error) = Assert.Single(result.Errors);
        Assert.Equal("rogue", node);
        Assert.Contains("undeclared channel \"ghost\"", error.Message);
        // Superstep atomicity: "ok"'s update was not committed either.
        Assert.Null(await cp.GetAsync("t"));
    }

    [Fact(DisplayName = "input naming an undeclared channel is refused before anything runs")]
    public async Task UndeclaredInput()
    {
        var ran = false;
        var g = Graph.Create().Channel("log", Channels.Append())
            .Node("a", (_, _) => { ran = true; return null; })
            .Edge(Graph.Start, "a").Compile();

        await Assert.ThrowsAsync<GraphException>(() => g.RunAsync(new Dictionary<string, object?> { ["nope"] = 1L }));
        Assert.False(ran);
    }

    // ── recursion limit ─────────────────────────────────────────────────────

    [Fact(DisplayName = "a cycle hits the recursion limit after exactly RecursionLimit supersteps")]
    public async Task RecursionLimit()
    {
        var runs = 0;
        var remaining = new List<int>();
        var g = Graph.Create()
            .Node("loop", (_, ctx) => { runs++; remaining.Add(ctx.RemainingSteps); return null; })
            .Edge(Graph.Start, "loop").Edge("loop", "loop")
            .Compile();

        var ex = await Assert.ThrowsAsync<RecursionLimitException>(() =>
            g.RunAsync(null, new RunOptions { RecursionLimit = 3 }));
        Assert.Contains("exceeded 3 supersteps", ex.Message);
        Assert.Contains("loop", ex.Message);
        Assert.Equal(3, runs);
        Assert.Equal(new[] { 3, 2, 1 }, remaining);
    }

    [Fact(DisplayName = "a recursion limit of zero refuses to run even one superstep")]
    public async Task RecursionLimitZero()
    {
        var g = Graph.Create().Node("a", Noop).Edge(Graph.Start, "a").Compile();
        await Assert.ThrowsAsync<RecursionLimitException>(() => g.RunAsync(null, new RunOptions { RecursionLimit = 0 }));
    }
}
