using Ilmek;

namespace Ilmek.Tests;

/// <summary>
/// The event stream and its projections (MODEL.md §10, §10.1, §10.2), plus the
/// retry policy arithmetic (§16). Mirrors ts stream.test.ts and retry.test.ts.
/// </summary>
public class StreamTests
{
    private static CompiledGraph TwoSteps() =>
        Graph.Create("two")
            .Channel("log", Channels.Append())
            .Node("a", (_, ctx) => { ctx.Emit("hello"); ctx.EmitToken("tok", new Dictionary<string, object?> { ["i"] = 1L }); return Update.Of("log", "a"); })
            .Node("b", (_, _) => Update.Of("log", "b"))
            .Edge(Graph.Start, "a").Edge("a", "b").Edge("b", Graph.End)
            .Compile();

    [Fact(DisplayName = "every event carries a 1-based, gap-free seq, a root ns, and the run's ids")]
    public async Task Envelope()
    {
        var result = await TwoSteps().RunAsync(null, new RunOptions { ThreadId = "t-env" });

        Assert.Equal(Enumerable.Range(1, result.Events.Count), result.Events.Select(e => e.Seq));
        Assert.All(result.Events, e =>
        {
            Assert.Empty(e.Ns);
            Assert.Equal("t-env", e.ThreadId);
            Assert.Equal(result.RunId, e.RunId);
        });
    }

    [Fact(DisplayName = "events arrive in the catalog order: run_start, step_start, node_start, custom, node_end, state, checkpoint, run_end")]
    public async Task CatalogOrder()
    {
        var types = (await TwoSteps().RunAsync()).Events.Select(e => e.GetType().Name).ToList();
        Assert.Equal(new[]
        {
            "RunStartEvent",
            "StepStartEvent", "NodeStartEvent", "CustomEvent", "CustomEvent", "NodeEndEvent", "StateEvent", "CheckpointEvent",
            "StepStartEvent", "NodeStartEvent", "NodeEndEvent", "StateEvent", "CheckpointEvent",
            "RunEndEvent",
        }, types);
    }

    [Fact(DisplayName = "an errored run ends node_error then run_end with the errors, and no state event")]
    public async Task ErrorOrder()
    {
        var g = Graph.Create().Node("x", (_, _) => throw new InvalidOperationException("bad")).Edge(Graph.Start, "x").Compile();
        var events = (await g.RunAsync()).Events;
        var err = Assert.IsType<NodeErrorEvent>(events[^2]);
        Assert.Equal("x", err.Node);
        var end = Assert.IsType<RunEndEvent>(events[^1]);
        Assert.Equal(RunStatus.Error, end.Status);
        Assert.Equal("bad", end.Errors!.Single().Error.Message);
        Assert.DoesNotContain(events, e => e is StateEvent);
    }

    [Fact(DisplayName = "a custom event reaches the consumer while its sibling is still running")]
    public async Task EmitIsLive()
    {
        var release = new TaskCompletionSource();
        var g = Graph.Create()
            .Node("talker", (_, ctx) => { ctx.Emit("early"); return null; })
            .Node("slow", async (_, _) => { await release.Task; return null; })
            .Edge(Graph.Start, "talker").Edge(Graph.Start, "slow")
            .Compile();

        var sawEarlyBeforeSlowEnded = false;
        await foreach (var ev in g.StreamEvents())
        {
            if (ev is CustomEvent { Payload: "early" })
            {
                sawEarlyBeforeSlowEnded = true;
                release.SetResult(); // only now may the slow sibling finish
            }
        }
        Assert.True(sawEarlyBeforeSlowEnded);
    }

    // ── projection ──────────────────────────────────────────────────────────

    [Fact(DisplayName = "updates, values, custom and messages modes project the right events, carrying seq")]
    public async Task ProjectionModes()
    {
        var events = (await TwoSteps().RunAsync()).Events;
        StreamPart[] Parts(StreamMode m) => events.SelectMany(e => Streaming.Project(e, new[] { m })).ToArray();

        var updates = Parts(StreamMode.Updates);
        Assert.Equal(new[] { "a", "b" }, updates.Select(p => ((Dictionary<string, object?>)p.Data!).Keys.Single()));

        var values = Parts(StreamMode.Values);
        Assert.Equal(2, values.Length);
        Assert.Equal(new List<object?> { "a", "b" }, ((IReadOnlyDictionary<string, object?>)values[^1].Data!)["log"]);

        var custom = Parts(StreamMode.Custom);
        Assert.Equal("hello", custom[0].Data);
        Assert.IsType<TokenChunk>(custom[1].Data);

        var token = Assert.IsType<TokenChunk>(Assert.Single(Parts(StreamMode.Messages)).Data);
        Assert.Equal("tok", token.Text);
        Assert.Equal(1L, token.Meta!["i"]);

        Assert.Equal(events.Count, Parts(StreamMode.Debug).Length);
        Assert.All(updates.Concat(values).Concat(custom), p => Assert.Equal(events.Single(e => e.Seq == p.Seq).Seq, p.Seq));
    }

    [Fact(DisplayName = "several modes multiplex through one pass in seq order; no modes yields nothing")]
    public async Task Multiplex()
    {
        var parts = new List<StreamPart>();
        await foreach (var p in Streaming.StreamModes(TwoSteps(), null, new[] { StreamMode.Updates, StreamMode.Messages }))
            parts.Add(p);

        Assert.Equal(new[] { StreamMode.Messages, StreamMode.Updates, StreamMode.Updates }, parts.Select(p => p.Mode));
        Assert.Equal(parts.Select(p => p.Seq).OrderBy(s => s), parts.Select(p => p.Seq));

        var none = (await TwoSteps().RunAsync()).Events.SelectMany(e => Streaming.Project(e, Array.Empty<StreamMode>()));
        Assert.Empty(none);
    }

    [Fact(DisplayName = "tokens are not journaled: a resumed node re-streams them while its steps do not re-run")]
    public async Task TokensReStreamOnResume()
    {
        var cp = new InMemoryCheckpointer();
        var calls = 0;
        var g = Graph.Create()
            .Node("n", async (_, ctx) =>
            {
                ctx.EmitToken("thinking");
                await ctx.StepAsync("llm", () => ++calls);
                await ctx.InterruptAsync<string>();
                return null;
            })
            .Edge(Graph.Start, "n")
            .Compile();
        var opts = new RunOptions { ThreadId = "t-tok", Checkpointer = cp };

        var first = new List<StreamPart>();
        await foreach (var p in Streaming.Projected(g.StreamEvents(null, opts), new[] { StreamMode.Messages })) first.Add(p);
        var second = new List<StreamPart>();
        await foreach (var p in Streaming.Projected(IlmekRuntime.ResumeStream(g, "go", opts), new[] { StreamMode.Messages }))
            second.Add(p);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(1, calls);
    }

    // ── retry policy ────────────────────────────────────────────────────────

    [Fact(DisplayName = "BackoffFor: zero without backoff; exact on the first retry; geometric after; capped by MaxBackoff")]
    public void BackoffArithmetic()
    {
        Assert.Equal(TimeSpan.Zero, new RetryPolicy().BackoffFor(2));
        Assert.Equal(TimeSpan.Zero, new RetryPolicy { Backoff = TimeSpan.FromMilliseconds(-5) }.BackoffFor(2));

        var p = new RetryPolicy { Backoff = TimeSpan.FromMilliseconds(100), Factor = 2 };
        Assert.Equal(TimeSpan.FromMilliseconds(100), p.BackoffFor(2));
        Assert.Equal(TimeSpan.FromMilliseconds(200), p.BackoffFor(3));
        Assert.Equal(TimeSpan.FromMilliseconds(400), p.BackoffFor(4));

        var capped = p with { MaxBackoff = TimeSpan.FromMilliseconds(250) };
        Assert.Equal(TimeSpan.FromMilliseconds(250), capped.BackoffFor(4));
        Assert.Equal(TimeSpan.FromMilliseconds(50), (p with { MaxBackoff = TimeSpan.FromMilliseconds(50) }).BackoffFor(2));

        Assert.Equal(TimeSpan.FromMilliseconds(100), (p with { Factor = 1 }).BackoffFor(5));
        Assert.Equal(TimeSpan.FromMilliseconds(50), (p with { Factor = 0.5 }).BackoffFor(3));
    }

    [Fact(DisplayName = "ShouldRetry: N attempts means N-1 retries, and RetryOn filters without extending the budget")]
    public void ShouldRetryRules()
    {
        var boom = new InvalidOperationException("transient");
        Assert.False(new RetryPolicy().ShouldRetry(1, boom));

        var three = new RetryPolicy { MaxAttempts = 3 };
        Assert.True(three.ShouldRetry(1, boom));
        Assert.True(three.ShouldRetry(2, boom));
        Assert.False(three.ShouldRetry(3, boom));
        Assert.False(three.ShouldRetry(9, boom));

        var picky = three with { RetryOn = e => e.Message.Contains("transient") };
        Assert.True(picky.ShouldRetry(1, boom));
        Assert.False(picky.ShouldRetry(1, new InvalidOperationException("fatal")));
        Assert.False(picky.ShouldRetry(3, boom));
    }

    [Fact(DisplayName = "exhausting attempts ends the run in error with the last error; RetryOn can decline")]
    public async Task RetryExhaustion()
    {
        var n = 0;
        var g = Graph.Create()
            .Node("x", (_, _) => throw new InvalidOperationException($"fail {++n}"), retry: new RetryPolicy { MaxAttempts = 3 })
            .Edge(Graph.Start, "x").Compile();
        var result = await g.RunAsync();
        Assert.Equal(RunStatus.Error, result.Status);
        Assert.Equal("fail 3", result.Errors.Single().Error.Message);

        var m = 0;
        var declines = Graph.Create()
            .Node("x", (_, _) => throw new InvalidOperationException($"fatal {++m}"),
                retry: new RetryPolicy { MaxAttempts = 5, RetryOn = e => !e.Message.StartsWith("fatal") })
            .Edge(Graph.Start, "x").Compile();
        Assert.Equal(RunStatus.Error, (await declines.RunAsync()).Status);
        Assert.Equal(1, m);
    }
}
