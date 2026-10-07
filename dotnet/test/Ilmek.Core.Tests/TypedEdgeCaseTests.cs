using Ilmek;

namespace Ilmek.Tests;

/// <summary>The typed facade's edges: selector validation, value coercion, typed sends and keyed resumes.</summary>
public class TypedEdgeCaseTests
{
    public sealed class Numbers
    {
        public int Count { get; set; }
        public long? Maybe { get; set; }
        public double Ratio { get; set; }
        public List<int> Ints { get; set; } = new();
        public Dictionary<string, long> Totals { get; set; } = new();
        [Merge] public Dictionary<string, object?> Bag { get; set; } = new();
        [Append] public List<string> Log { get; set; } = new();
        public string ReadOnly => "computed"; // no setter: not a channel
    }

    public sealed class Job
    {
        public string Id { get; set; } = "";
        public int Size { get; set; }
    }

    [Fact(DisplayName = "JSON-shaped values are coerced to the property types: long to int, list and map element types, nullable")]
    public async Task CoercesJsonShapes()
    {
        var g = Graph.Create<Numbers>("coerce")
            .Node("n", (_, _) => new Dictionary<string, object?>
            {
                ["Count"] = 7L,
                ["Maybe"] = 5L,
                ["Ratio"] = 2L,
                ["Ints"] = new List<object?> { 1L, 2L },
                ["Totals"] = new Dictionary<string, object?> { ["a"] = 3L },
            })
            .Edge(Graph.Start, "n")
            .Compile();

        var s = (await g.RunAsync()).State!;
        Assert.Equal(7, s.Count);
        Assert.Equal(5L, s.Maybe);
        Assert.Equal(2.0, s.Ratio);
        Assert.Equal(new List<int> { 1, 2 }, s.Ints);
        Assert.Equal(3L, s.Totals["a"]);
    }

    [Fact(DisplayName = "a read-only property is not a channel, so writing it is an undeclared-channel error")]
    public async Task ReadOnlyIsNotAChannel()
    {
        var g = Graph.Create<Numbers>("ro")
            .Node("n", (_, _) => Update.Of("ReadOnly", "x"))
            .Edge(Graph.Start, "n")
            .Compile();
        Assert.DoesNotContain("ReadOnly", g.Inner.Channels.Keys);
        var result = await g.RunAsync();
        Assert.Equal(RunStatus.Error, result.Status);
        Assert.Contains("undeclared channel \"ReadOnly\"", result.Errors.Single().Error.Message);
    }

    [Fact(DisplayName = "attribute reducers are honoured: [Merge] merges, [Append] appends")]
    public async Task AttributeReducers()
    {
        var g = Graph.Create<Numbers>("attrs")
            .Node("a", (_, _) => Update.For<Numbers>()
                .Set(s => s.Bag, new Dictionary<string, object?> { ["x"] = 1L })
                .Append(s => s.Log, "a"))
            .Node("b", (_, _) => Update.For<Numbers>()
                .Set(s => s.Bag, new Dictionary<string, object?> { ["y"] = 2L })
                .Append(s => s.Log, "b", "c"))
            .Edge(Graph.Start, "a").Edge("a", "b")
            .Compile();

        Assert.Equal("merge", g.Inner.Channels["Bag"].Kind);
        var s = (await g.RunAsync()).State!;
        Assert.Equal(new Dictionary<string, object?> { ["x"] = 1L, ["y"] = 2L }, s.Bag);
        Assert.Equal(new List<string> { "a", "b", "c" }, s.Log);
    }

    [Fact(DisplayName = "a selector that is not a property access is refused with a GraphException")]
    public void BadSelector()
    {
        var ex = Assert.Throws<GraphException>(() => Update.For<Numbers>().Set(s => s.Count + 1, 2));
        Assert.Contains("property selector", ex.Message);
    }

    [Fact(DisplayName = "a nested selector (s => s.Log.Count) is refused, not aliased to the state's own Count channel")]
    public async Task NestedSelectorRefused()
    {
        // Numbers has a Count channel AND Log.Count exists; taking the last member
        // name used to make both selectors address the same channel.
        Assert.Contains("property selector", Assert.Throws<GraphException>(() =>
            Graph.Create<Numbers>().Channel(s => s.Log.Count, Channels.LastWrite())).Message);
        Assert.Throws<GraphException>(() => Update.For<Numbers>().Set(s => s.Log.Count, 3));

        // The intended direct selector still works.
        var g = Graph.Create<Numbers>().Node("n", (_, _) => Update.For<Numbers>().Set(s => s.Count, 3))
            .Edge(Graph.Start, "n").Compile();
        Assert.Equal(3, (await g.RunAsync()).State!.Count);
    }

    [Fact(DisplayName = "a typed fan-out node receives its Send payload as the declared input type")]
    public async Task TypedSendPayload()
    {
        var g = Graph.Create<Numbers>("jobs")
            .Node("plan", (_, _) => Command.Goto_(
                new Send("work", new Job { Id = "a", Size = 2 }),
                new Send("work", new Dictionary<string, object?> { ["Id"] = "b", ["Size"] = 3L })))
            .Node<Job>("work", (job, _) => Update.For<Numbers>().Append(s => s.Log, $"{job.Id}:{job.Size}"))
            .Edge(Graph.Start, "plan")
            .Compile();

        var log = (await g.RunAsync()).State!.Log;
        Assert.Equal(new[] { "a:2", "b:3" }, log.OrderBy(x => x));
    }

    [Fact(DisplayName = "typed ResumeKeyed and StreamEvents behave like the untyped surface")]
    public async Task TypedKeyedResumeAndStream()
    {
        var cp = new InMemoryCheckpointer();
        var g = Graph.Create<Numbers>("typed-keyed")
            .Node("a", async (_, ctx) => Update.For<Numbers>().Append(s => s.Log, await ctx.InterruptAsync<string>()))
            .Node("b", async (_, ctx) => Update.For<Numbers>().Append(s => s.Log, await ctx.InterruptAsync<string>()))
            .Edge(Graph.Start, "a").Edge(Graph.Start, "b")
            .Compile();
        var opts = new RunOptions { ThreadId = "tk", Checkpointer = cp };

        var types = new List<string>();
        await foreach (var ev in g.StreamEvents(null, opts)) types.Add(ev.GetType().Name);
        Assert.Equal("RunEndEvent", types[^1]);
        Assert.Contains("InterruptEvent", types);

        var done = await g.ResumeKeyedAsync(new Dictionary<string, object?>
        {
            ["a:interrupt#0"] = "A", ["b:interrupt#0"] = "B",
        }, opts);
        Assert.Equal(new List<string> { "A", "B" }, done.State!.Log);
        Assert.Equal(RunStatus.Done, done.Status);
    }

    [Fact(DisplayName = "an interrupted typed result has no state but carries the pending interrupts")]
    public async Task TypedInterruptedResult()
    {
        var cp = new InMemoryCheckpointer();
        var g = Graph.Create<Numbers>()
            .Node("a", async (_, ctx) => { await ctx.InterruptAsync<string>("q"); return null; })
            .Edge(Graph.Start, "a")
            .Compile();
        var r = await g.RunAsync(null, new RunOptions { ThreadId = "ti", Checkpointer = cp });
        Assert.Equal(RunStatus.Interrupted, r.Status);
        Assert.Null(r.State);
        Assert.Equal("q", r.Pending.Single().Payload);
    }
}
