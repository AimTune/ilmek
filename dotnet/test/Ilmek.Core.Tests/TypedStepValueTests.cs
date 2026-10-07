using System.Text.Json;
using Ilmek;

namespace Ilmek.Tests;

/// <summary>
/// What <c>StepAsync&lt;T&gt;</c> / <c>InterruptAsync&lt;T&gt;</c> hand back on a
/// replay pass. In memory the journal keeps the very object the step returned, so
/// replay must return that same instance — no conversion, no copy. A durable
/// checkpointer hands back JSON-shaped data instead; replay must convert it to
/// <c>T</c> (the SQLite suite covers that end to end; here a checkpointer that
/// returns raw <see cref="JsonElement"/>s stands in for any other store).
/// </summary>
public class TypedStepValueTests
{
    public sealed record Order(string Id, decimal Total, IReadOnlyList<string> Tags);

    private static CompiledGraph StepThenPause<T>(Func<T> make, List<T> seen) =>
        Graph.Create("typed")
            .Node("work", async (State _, IContext ctx) =>
            {
                seen.Add(await ctx.StepAsync("make", make));
                await ctx.InterruptAsync<string>("go?");
                return null;
            })
            .Edge(Graph.Start, "work")
            .Edge("work", Graph.End)
            .Compile();

    [Fact(DisplayName = "in memory, replay returns the very instance the step returned")]
    public async Task InMemoryReturnsTheSameInstance()
    {
        var order = new Order("o-1", 1.10m, ["a"]);
        var list = new List<Order> { order };
        var seenOrder = new List<Order>();
        var seenList = new List<List<Order>>();

        var cp = new InMemoryCheckpointer();
        var g1 = StepThenPause(() => order, seenOrder);
        await g1.RunAsync(null, new RunOptions { ThreadId = "a", Checkpointer = cp });
        await g1.ResumeAsync("yes", new RunOptions { ThreadId = "a", Checkpointer = cp });
        Assert.Equal(2, seenOrder.Count);
        Assert.Same(order, seenOrder[0]);
        Assert.Same(order, seenOrder[1]);

        var g2 = StepThenPause(() => list, seenList);
        await g2.RunAsync(null, new RunOptions { ThreadId = "b", Checkpointer = cp });
        await g2.ResumeAsync("yes", new RunOptions { ThreadId = "b", Checkpointer = cp });
        Assert.Same(list, seenList[1]);
    }

    [Fact(DisplayName = "a store that hands back raw JsonElements still replays typed values")]
    public async Task JsonElementJournalConverts()
    {
        var seen = new List<Order>();
        var calls = 0;
        var g = StepThenPause(() => { calls++; return new Order("o-1", 12345678901234567890.123456789m, ["a", "b"]); }, seen);
        var cp = new JsonElementCheckpointer();

        await g.RunAsync(null, new RunOptions { ThreadId = "t", Checkpointer = cp });
        var done = await g.ResumeAsync("yes", new RunOptions { ThreadId = "t", Checkpointer = cp });

        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(1, calls);
        Assert.Equal("o-1", seen[1].Id);
        Assert.Equal(12345678901234567890.123456789m, seen[1].Total);
        Assert.Equal(["a", "b"], seen[1].Tags);
    }

    [Fact(DisplayName = "a value that cannot become T says which step and which type")]
    public async Task UnconvertibleValueIsExplicit()
    {
        var cp = new JsonElementCheckpointer();
        var pass = 0;
        var g = Graph.Create("bad")
            .Node("work", async (State _, IContext ctx) =>
            {
                pass++;
                // The first pass journals a string; the replay asks for an int.
                if (pass == 1) await ctx.StepAsync("make", () => "not a number");
                else await ctx.StepAsync("make", () => 1);
                await ctx.InterruptAsync<string>("go?");
                return null;
            })
            .Edge(Graph.Start, "work")
            .Edge("work", Graph.End)
            .Compile();

        await g.RunAsync(null, new RunOptions { ThreadId = "t", Checkpointer = cp });
        var result = await g.ResumeAsync("yes", new RunOptions { ThreadId = "t", Checkpointer = cp, Strict = false });
        Assert.Equal(RunStatus.Error, result.Status);
        var ex = Assert.IsType<InvalidCastException>(Assert.Single(result.Errors).Error);
        Assert.Contains("make#0", ex.Message);
        Assert.Contains("Int32", ex.Message);
    }

    /// <summary>An in-memory store whose journals come back as raw JsonElements.</summary>
    private sealed class JsonElementCheckpointer : ICheckpointer
    {
        private readonly InMemoryCheckpointer _inner = new();

        public Task PutAsync(Checkpoint checkpoint, CancellationToken ct = default) => _inner.PutAsync(checkpoint, ct);
        public Task<Checkpoint?> GetAsync(string threadId, string? checkpointId = null, CancellationToken ct = default) => _inner.GetAsync(threadId, checkpointId, ct);
        public Task<IReadOnlyList<Checkpoint>> ListAsync(string threadId, int? limit = null, CancellationToken ct = default) => _inner.ListAsync(threadId, limit, ct);
        public Task PutJournalAsync(string taskId, Journal journal, CancellationToken ct = default) => _inner.PutJournalAsync(taskId, journal, ct);
        public Task DropJournalAsync(string taskId, CancellationToken ct = default) => _inner.DropJournalAsync(taskId, ct);
        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) => _inner.DeleteThreadAsync(threadId, ct);

        public async Task<Journal> GetJournalAsync(string taskId, CancellationToken ct = default)
        {
            var journal = await _inner.GetJournalAsync(taskId, ct);
            return Journal.Load(journal.Dump().Select(kv => new KeyValuePair<string, JournalEntry>(
                kv.Key,
                kv.Value with { Value = kv.Value.Value is null ? null : JsonSerializer.SerializeToElement(kv.Value.Value) })));
        }
    }
}
