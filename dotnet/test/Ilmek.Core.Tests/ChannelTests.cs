using Ilmek;

namespace Ilmek.Tests;

/// <summary>The reducers of MODEL.md §2 at the unit level, mirroring ts/packages/core/test/channel.test.ts.</summary>
public class ChannelTests
{
    // ── last_write ──────────────────────────────────────────────────────────

    [Fact(DisplayName = "last_write: incoming wins, including over an unset channel")]
    public void LastWriteIncomingWins()
    {
        var ch = Channels.LastWrite(0L);
        Assert.Equal(1L, ch.Reduce("n", Channel.Unset, 1L));
        Assert.Equal(2L, ch.Reduce("n", 1L, 2L));
        Assert.Equal("last_write", ch.Kind);
    }

    [Fact(DisplayName = "last_write: writing null is a write, not a no-op")]
    public void LastWriteNullIsAWrite()
    {
        Assert.Null(Channels.LastWrite(7L).Reduce("n", 7L, null));
    }

    // ── append ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "append: folds one item and many items alike")]
    public void AppendOneAndMany()
    {
        var ch = Channels.Append();
        Assert.Equal(new List<object?> { "a" }, ch.Reduce("log", Channel.Unset, "a"));
        Assert.Equal(new List<object?> { "a", "b", "c" },
            ch.Reduce("log", new List<object?> { "a" }, new[] { "b", "c" }));
    }

    [Fact(DisplayName = "append: a string is one item, never split into characters")]
    public void AppendStringIsOneItem()
    {
        Assert.Equal(new List<object?> { "hello" }, Channels.Append().Reduce("log", Channel.Unset, "hello"));
    }

    [Fact(DisplayName = "append: a dictionary is one item, not a list of its key/value pairs")]
    public void AppendDictionaryIsOneItem()
    {
        // MODEL.md §2: append is `current ++ List.wrap(incoming)`, and wrapping a
        // map gives [map]. The TS reference appends the object; .NET used to
        // enumerate it into KeyValuePairs.
        var message = new Dictionary<string, object?> { ["role"] = "user", ["text"] = "hi" };
        var result = Assert.IsType<List<object?>>(Channels.Append().Reduce("messages", Channel.Unset, message));
        Assert.Same(message, Assert.Single(result));
    }

    [Fact(DisplayName = "append: a dictionary appended through a run lands as one message")]
    public async Task AppendDictionaryThroughARun()
    {
        var g = Graph.Create("messages")
            .Channel("messages", Channels.Append())
            .Node("a", (_, _) => Update.Of("messages", new Dictionary<string, object?> { ["role"] = "user" }))
            .Node("b", (_, _) => Update.Of("messages", new Dictionary<string, object?> { ["role"] = "assistant" }))
            .Edge(Graph.Start, "a").Edge("a", "b").Edge("b", Graph.End)
            .Compile();

        var result = await g.RunAsync();
        var messages = result.State!.GetList<Dictionary<string, object?>>("messages");
        Assert.Equal(new[] { "user", "assistant" }, messages.Select(m => m["role"]));
    }

    [Fact(DisplayName = "append: does not mutate the current value")]
    public void AppendDoesNotMutate()
    {
        var current = new List<object?> { "a" };
        var next = Channels.Append().Reduce("log", current, "b");
        Assert.Equal(new List<object?> { "a" }, current);
        Assert.NotSame(current, next);
    }

    [Fact(DisplayName = "append: an empty list is a no-op that still copies")]
    public void AppendEmptyList()
    {
        var current = new List<object?> { "a" };
        var next = Channels.Append().Reduce("log", current, new List<object?>());
        Assert.Equal(new List<object?> { "a" }, next);
        Assert.NotSame(current, next);
    }

    [Fact(DisplayName = "append: a non-list current value is a ReducerException, not silently discarded history")]
    public void AppendNonListCurrentThrows()
    {
        // A checkpoint written while the channel was last_write, read by a graph
        // that now declares it append: the TS reference throws; dropping the
        // existing value would lose data without a trace.
        var ex = Assert.Throws<ReducerException>(() => Channels.Append().Reduce("items", 5L, "a"));
        Assert.Contains("\"items\"", ex.Message);
    }

    [Fact(DisplayName = "append: an array current value is extended, not replaced")]
    public void AppendArrayCurrent()
    {
        Assert.Equal(new List<object?> { "a", "b" }, Channels.Append().Reduce("log", new object?[] { "a" }, "b"));
    }

    [Fact(DisplayName = "append: the default is a fresh list per read, so in-place mutation cannot leak")]
    public async Task AppendDefaultIsNotShared()
    {
        var ch = Channels.Append();
        Assert.NotSame(ch.Default, ch.Default);

        // A node that (wrongly) mutates its state instead of returning an update
        // must not poison the next run's view of an unwritten channel.
        var g = Graph.Create("mutator")
            .Channel("log", ch)
            .Node("bad", (state, _) =>
            {
                Assert.Empty(state.Get<List<object?>>("log"));
                state.Get<List<object?>>("log").Add("leaked");
                return null;
            })
            .Edge(Graph.Start, "bad").Edge("bad", Graph.End)
            .Compile();

        await g.RunAsync();
        var second = await g.RunAsync();
        Assert.Equal(RunStatus.Done, second.Status); // the in-node Assert.Empty held on run 2
        Assert.Empty(second.State!.GetList<object>("log"));
    }

    // ── merge ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "merge: incoming wins per key")]
    public void MergeIncomingWins()
    {
        var ch = Channels.Merge();
        Assert.Equal(new Dictionary<string, object?> { ["a"] = 1L },
            ch.Reduce("m", Channel.Unset, new Dictionary<string, object?> { ["a"] = 1L }));
        Assert.Equal(new Dictionary<string, object?> { ["a"] = 1L, ["b"] = 3L },
            ch.Reduce("m", new Dictionary<string, object?> { ["a"] = 1L, ["b"] = 2L },
                new Dictionary<string, object?> { ["b"] = 3L }));
    }

    [Fact(DisplayName = "merge: the merge is shallow")]
    public void MergeIsShallow()
    {
        var result = (Dictionary<string, object?>)Channels.Merge().Reduce("m",
            new Dictionary<string, object?> { ["nested"] = new Dictionary<string, object?> { ["keep"] = 1L } },
            new Dictionary<string, object?> { ["nested"] = new Dictionary<string, object?> { ["other"] = 2L } })!;
        Assert.Equal(new Dictionary<string, object?> { ["other"] = 2L }, result["nested"]);
    }

    [Fact(DisplayName = "merge: does not mutate the current value")]
    public void MergeDoesNotMutate()
    {
        var current = new Dictionary<string, object?> { ["a"] = 1L };
        Channels.Merge().Reduce("m", current, new Dictionary<string, object?> { ["a"] = 2L });
        Assert.Equal(1L, current["a"]);
    }

    [Fact(DisplayName = "merge: a non-dictionary update is a ReducerException naming the channel")]
    public void MergeNonDictionaryThrows()
    {
        var ex = Assert.Throws<ReducerException>(() => Channels.Merge().Reduce("profile", Channel.Unset, "nope"));
        Assert.Contains("dictionary", ex.Message);
    }

    [Fact(DisplayName = "merge: the default is a fresh dictionary per read")]
    public void MergeDefaultIsNotShared()
    {
        var ch = Channels.Merge();
        Assert.NotSame(ch.Default, ch.Default);
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(ch.Default));
    }

    // ── custom ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "custom: current is null on the first write, never the Unset sentinel")]
    public void CustomSeesNullFirst()
    {
        var seen = new List<object?>();
        var ch = Channels.Reduce((cur, inc) => { seen.Add(cur); return (long)(cur ?? 0L) + (long)inc!; }, 0L);

        Assert.Equal(5L, ch.Reduce("sum", Channel.Unset, 5L));
        Assert.Equal(8L, ch.Reduce("sum", 5L, 3L));
        Assert.Equal(new List<object?> { null, 5L }, seen);
        Assert.Equal("custom", ch.Kind);
        Assert.Equal(0L, ch.Default);
    }

    [Fact(DisplayName = "a throwing reducer surfaces as a ReducerException naming the channel, cause attached")]
    public void ThrowingReducerIsWrapped()
    {
        var boom = Channels.Reduce((_, _) => throw new InvalidOperationException("nope"));
        var ex = Assert.Throws<ReducerException>(() => boom.Reduce("count", Channel.Unset, 1L));
        Assert.Contains("\"count\"", ex.Message);
        Assert.Contains("(unset)", ex.Message);
        Assert.Equal("nope", ex.InnerException!.Message);
    }

    [Fact(DisplayName = "a reducer type mismatch inside a run ends it in error, attributed to the writing node")]
    public async Task ReducerMismatchInRun()
    {
        var g = Graph.Create("mismatch")
            .Channel("total", Channels.Reduce((cur, inc) => (long)(cur ?? 0L) + (long)inc!, 0L))
            .Node("writer", (_, _) => Update.Of("total", "not a number"))
            .Edge(Graph.Start, "writer").Edge("writer", Graph.End)
            .Compile();

        var result = await g.RunAsync();
        Assert.Equal(RunStatus.Error, result.Status);
        var (node, error) = Assert.Single(result.Errors);
        Assert.Equal("writer", node);
        Assert.IsType<ReducerException>(error);
        Assert.Contains(result.Events, e => e is NodeErrorEvent { Node: "writer" });
    }

    // ── materialize ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "materialize: unwritten, null and Unset all read as the channel default")]
    public void MaterializeDefaults()
    {
        var g = Graph.Create("m")
            .Channel("log", Channels.Append())
            .Channel("intent", Channels.LastWrite("none"))
            .Channel("other", Channels.LastWrite("x"))
            .Node("n", (_, _) => null)
            .Edge(Graph.Start, "n")
            .Compile();

        var state = g.Materialize(new Dictionary<string, object?> { ["intent"] = null, ["other"] = Channel.Unset });
        Assert.Empty(state.GetList<object>("log"));
        Assert.Equal("none", state["intent"]);
        Assert.Equal("x", state["other"]);
    }

    [Fact(DisplayName = "materialize: values for channels the graph does not declare are dropped")]
    public void MaterializeDropsStrays()
    {
        var g = Graph.Create("m")
            .Channel("intent", Channels.LastWrite("none"))
            .Node("n", (_, _) => null)
            .Edge(Graph.Start, "n")
            .Compile();

        var state = g.Materialize(new Dictionary<string, object?> { ["stray"] = 1L, ["intent"] = "buy" });
        Assert.Equal(new[] { "intent" }, state.Keys);
        Assert.False(state.Has("stray"));
        Assert.Equal("buy", state["intent"]);
    }

    // ── State accessors ─────────────────────────────────────────────────────

    [Fact(DisplayName = "State.Get refuses a wrong type with a message naming the channel")]
    public void StateGetWrongType()
    {
        var state = new State(new Dictionary<string, object?> { ["n"] = "text" });
        var ex = Assert.Throws<GraphException>(() => state.Get<long>("n"));
        Assert.Contains("\"n\"", ex.Message);
        Assert.Equal(0L, new State(new Dictionary<string, object?>()).Get<long>("missing"));
        Assert.Equal(9L, state.GetOr("n", 9L));
        Assert.Equal("text", state.GetOr("n", "fallback"));
    }

    [Fact(DisplayName = "State.GetList filters to the requested element type and tolerates a non-list")]
    public void StateGetList()
    {
        var state = new State(new Dictionary<string, object?>
        {
            ["mixed"] = new List<object?> { "a", 1L, "b", null },
            ["scalar"] = 5L,
        });
        Assert.Equal(new List<string> { "a", "b" }, state.GetList<string>("mixed"));
        Assert.Empty(state.GetList<string>("scalar"));
        Assert.Empty(state.GetList<string>("absent"));
    }
}
