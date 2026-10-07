using Ilmek;
using Ilmek.Tests.Contract;

namespace Ilmek.Tests;

/// <summary>The §7 contract against the reference backend, plus the id helpers it relies on.</summary>
public class InMemoryCheckpointerTests : CheckpointerContract
{
    protected override ICheckpointer Create() => new InMemoryCheckpointer();

    [Fact(DisplayName = "checkpoint ids sort lexically in generation order and are unique within a millisecond")]
    public void GeneratedIdsSortAndAreUnique()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => Checkpoint.GenerateId()).ToList();
        Assert.Equal(ids, ids.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.StartsWith("ckpt-", id));
        Assert.Single(ids.Select(id => id.Length).Distinct()); // fixed width: lexical order is numeric order
    }

    [Fact(DisplayName = "ThreadJournalPrefixes covers the engine's root and checkpoint-planned task ids")]
    public void ThreadJournalPrefixes()
    {
        Assert.Equal(new[] { "t-1:root:", "t-1:ckpt-" }, Checkpoint.ThreadJournalPrefixes("t-1"));
    }

    [Fact(DisplayName = "the engine's task ids for a thread all start with one of its journal prefixes")]
    public async Task EngineTaskIdsMatchPrefixes()
    {
        var cp = new InMemoryCheckpointer();
        var g = Graph.Create("ids")
            .Channel("n", Channels.LastWrite(0L))
            .Node("a", (_, _) => Update.Of("n", 1L))
            .Node("b", async (_, ctx) => { await ctx.InterruptAsync<string>(); return null; })
            .Edge(Graph.Start, "a").Edge("a", "b").Edge("b", Graph.End)
            .Compile();

        var result = await g.RunAsync(null, new RunOptions { ThreadId = "tid", Checkpointer = cp });
        var taskIds = result.Events.OfType<NodeStartEvent>().Select(e => e.TaskId).ToList();
        Assert.Equal(2, taskIds.Count);
        Assert.All(taskIds, id =>
            Assert.Contains(Checkpoint.ThreadJournalPrefixes("tid"), p => id.StartsWith(p, StringComparison.Ordinal)));
    }
}
