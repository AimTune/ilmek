using Ilmek;

namespace Ilmek.Tests;

/// <summary>
/// The typed graph facade (MODEL.md §11 note on state typing): channels as
/// properties of a state class, so `state.Cart` is a `List&lt;string&gt;` and the
/// state type is a thing you can name and export. It compiles to the same channel
/// map as the untyped builder, so this suite also proves the two produce
/// identical behavior.
/// </summary>
public class TypedGraphTests
{
    // The state, in one named place — importable by a node in another file, a
    // test, or an adapter. The reducers live ON the class (a plain property is
    // last_write; [Append] accumulates), so the graph derives every channel from
    // it: no .Channel() restatement. This is the answer to "why aren't graphs typed?".
    public sealed class CheckoutState
    {
        public List<string> Cart { get; set; } = new();
        [Append] public List<string> Log { get; set; } = new();
        public string Intent { get; set; } = "";
    }

    private static Graph<CheckoutState> CheckoutGraph() => Graph.Create<CheckoutState>("checkout");

    [Fact(DisplayName = "a node reads and writes the typed state")]
    public async Task TypedNodeReadsAndWrites()
    {
        var g = CheckoutGraph()
            .Node("checkout", (state, _) =>
            {
                // state.Cart is List<string> — no casts, no string keys.
                var first = state.Cart[0];
                return Update.For<CheckoutState>()
                    .Set(s => s.Intent, "buy")
                    .Append(s => s.Log, $"saw {first}");
            })
            .Edge(Graph.Start, "checkout")
            .Edge("checkout", Graph.End)
            .Compile();

        var input = Update.For<CheckoutState>().Set(s => s.Cart, new List<string> { "kahve" });
        var result = await g.RunAsync(input);

        Assert.Equal(RunStatus.Done, result.Status);
        // result.State is CheckoutState, typed all the way out.
        Assert.Equal("buy", result.State!.Intent);
        Assert.Equal(new List<string> { "saw kahve" }, result.State.Log);
    }

    [Fact(DisplayName = "a typed graph runs the §12.1 interrupt/resume cycle, step-once")]
    public async Task TypedInterruptResume()
    {
        var cp = new InMemoryCheckpointer();
        var creates = 0;

        var g = CheckoutGraph()
            .Node("checkout", async (state, ctx) =>
            {
                await ctx.StepAsync("create_order", () => { creates++; return "order-1"; });
                var ok = await ctx.InterruptAsync<string>(new { q = "charge?" });
                return Update.For<CheckoutState>().Append(s => s.Log, ok);
            })
            .Edge(Graph.Start, "checkout")
            .Edge("checkout", Graph.End)
            .Compile();

        var opts = new RunOptions { ThreadId = "typed-1", Checkpointer = cp };

        var paused = await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Interrupted, paused.Status);
        Assert.Equal(1, creates);

        var done = await g.ResumeAsync("yes", opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new List<string> { "yes" }, done.State!.Log);
        Assert.Equal(1, creates); // journal held it, exactly as the untyped path does
    }

    [Fact(DisplayName = "a typed guard routes on the typed state")]
    public async Task TypedGuardRoutes()
    {
        var g = Graph.Create<CheckoutState>("route")
            .Node("classify", (_, _) => Update.For<CheckoutState>().Set(s => s.Intent, "buy"))
            .Node("buy", (_, _) => Update.For<CheckoutState>().Append(s => s.Log, "bought"))
            .Node("browse", (_, _) => Update.For<CheckoutState>().Append(s => s.Log, "browsed"))
            .Edge(Graph.Start, "classify")
            // guard reads state.Intent (a string), not state["intent"] (an object?)
            .Edge("classify", "buy", when: (state, _) => state.Intent == "buy")
            .Edge("classify", "browse", when: (state, _) => state.Intent != "buy")
            .Edge("buy", Graph.End)
            .Edge("browse", Graph.End)
            .Compile();

        var result = await g.RunAsync();
        Assert.Equal(new List<string> { "bought" }, result.State!.Log);
    }

    [Fact(DisplayName = "the state type materializes correctly even after a JSON round-trip")]
    public async Task TypedStateSurvivesSerializedValues()
    {
        // Simulate what a durable checkpointer hands back: List<object?> instead of
        // List<string>. The typed layer's Coerce must fit it to the property type.
        var g = CheckoutGraph()
            .Node("n", (_, _) => new Dictionary<string, object?>
            {
                // deliberately the loose shape a JSON decoder produces
                ["Log"] = new List<object?> { "a", "b" },
                ["Cart"] = new List<object?> { "x" },
            })
            .Edge(Graph.Start, "n")
            .Edge("n", Graph.End)
            .Compile();

        var result = await g.RunAsync();
        Assert.Equal(RunStatus.Done, result.Status);
        // Coerced back to List<string>, not List<object?>.
        Assert.Equal(new List<string> { "a", "b" }, result.State!.Log);
        Assert.Equal(new List<string> { "x" }, result.State.Cart);
    }

    [Fact(DisplayName = "channels are derived from the class with no .Channel() calls")]
    public async Task ChannelsDerivedFromClass()
    {
        // Not one .Channel() call anywhere, yet Log accumulates (its [Append]) and
        // Intent replaces (default last_write) — the reducers came off the class.
        var g = Graph.Create<CheckoutState>("derived")
            .Node("a", (_, _) => Update.For<CheckoutState>().Append(s => s.Log, "a").Set(s => s.Intent, "x"))
            .Node("b", (_, _) => Update.For<CheckoutState>().Append(s => s.Log, "b").Set(s => s.Intent, "y"))
            .Edge(Graph.Start, "a").Edge("a", "b").Edge("b", Graph.End)
            .Compile();

        var result = await g.RunAsync();
        Assert.Equal(new List<string> { "a", "b" }, result.State!.Log); // [Append]
        Assert.Equal("y", result.State.Intent);                          // last_write
    }

    // A custom reducer (a lambda) can't be an attribute, so it stays an explicit
    // .Channel() override — the one remaining reason to call it.
    public sealed class SumState
    {
        public long Total { get; set; }
    }

    [Fact(DisplayName = ".Channel() overrides a channel with a custom reducer")]
    public async Task CustomReducerOverride()
    {
        var g = Graph.Create<SumState>("sum")
            .Channel(s => s.Total, Channels.Reduce((cur, inc) => Convert.ToInt64(cur ?? 0L) + Convert.ToInt64(inc), 0L))
            .Node("a", (_, _) => Update.For<SumState>().Set(s => s.Total, 3L))
            .Node("b", (_, _) => Update.For<SumState>().Set(s => s.Total, 4L))
            .Edge(Graph.Start, "a").Edge(Graph.Start, "b")
            .Edge("a", Graph.End).Edge("b", Graph.End)
            .Compile();

        // Both nodes write Total in one superstep; the custom reducer sums them.
        var result = await g.RunAsync();
        Assert.Equal(7L, result.State!.Total);
    }

    [Fact(DisplayName = "a read-only property cannot be overridden as a channel")]
    public void ReadOnlyPropertyIsRejected()
    {
        var ex = Assert.Throws<GraphException>(() =>
            Graph.Create<BadState>("bad").Channel(s => s.ReadOnly, Channels.LastWrite("")));
        Assert.Contains("no setter", ex.Message);
    }

    private sealed class BadState
    {
        public string ReadOnly { get; } = "";
    }
}
