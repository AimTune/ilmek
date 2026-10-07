using System.Collections.Concurrent;
using Ilmek;

namespace Ilmek.Tests;

/// <summary>
/// The resume surface (MODEL.md §6, §6.1, §10.3): every way a caller can get a
/// resume wrong must be refused without damaging the thread, and cancellation
/// must leave the last commit standing.
/// </summary>
public class InterruptTests
{
    private static RunOptions Opts(ICheckpointer cp, string thread, CancellationToken ct = default) =>
        new() { ThreadId = thread, Checkpointer = cp, CancellationToken = ct };

    private static CompiledGraph Ask(Action? beforePause = null) =>
        Graph.Create("ask")
            .Channel("log", Channels.Append())
            .Node("work", async (_, ctx) =>
            {
                await ctx.StepAsync("before", () => { beforePause?.Invoke(); return 0L; });
                var answer = await ctx.InterruptAsync<object?>(new Dictionary<string, object?> { ["q"] = "ok?" });
                return Update.Of("log", answer);
            })
            .Edge(Graph.Start, "work")
            .Edge("work", Graph.End)
            .Compile();

    // ── refusals ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "resuming a thread that already completed is refused: it is not interrupted")]
    public async Task DoubleResumeRefused()
    {
        var cp = new InMemoryCheckpointer();
        var g = Ask();
        var opts = Opts(cp, "t-double");
        await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Done, (await g.ResumeAsync("yes", opts)).Status);

        var ex = await Assert.ThrowsAsync<ResumeException>(() => g.ResumeAsync("again", opts));
        Assert.Contains("not interrupted", ex.Message);
        var keyed = await Assert.ThrowsAsync<ResumeException>(() =>
            g.ResumeKeyedAsync(new Dictionary<string, object?> { ["work:interrupt#0"] = "again" }, opts));
        Assert.Contains("not interrupted", keyed.Message);
    }

    [Fact(DisplayName = "a keyed resume with an unknown id is refused, and the thread stays resumable by the right id")]
    public async Task WrongIdRefusedThreadSurvives()
    {
        var cp = new InMemoryCheckpointer();
        var g = Ask();
        var opts = Opts(cp, "t-wrong-id");
        await g.RunAsync(null, opts);

        foreach (var wrong in new[] { "interrupt#0", "work", "other:interrupt#0", "" })
        {
            var ex = await Assert.ThrowsAsync<ResumeException>(() =>
                g.ResumeKeyedAsync(new Dictionary<string, object?> { [wrong] = "x" }, opts));
            Assert.Contains("work:interrupt#0", ex.Message); // names the id it expected
        }

        var done = await g.ResumeKeyedAsync(new Dictionary<string, object?> { ["work:interrupt#0"] = "right" }, opts);
        Assert.Equal(new List<string> { "right" }, done.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "an empty keyed answer set is refused without touching the journal")]
    public async Task EmptyKeyedRefused()
    {
        var cp = new InMemoryCheckpointer();
        var g = Ask();
        var opts = Opts(cp, "t-empty");
        await g.RunAsync(null, opts);

        await Assert.ThrowsAsync<ResumeException>(() => g.ResumeKeyedAsync(new Dictionary<string, object?>(), opts));
        Assert.Equal(RunStatus.Done, (await g.ResumeAsync("fine", opts)).Status);
    }

    [Fact(DisplayName = "resuming a thread with no checkpoint is refused — with or without a checkpointer")]
    public async Task ResumeWithoutCheckpoint()
    {
        var g = Ask();
        var ex = await Assert.ThrowsAsync<ResumeException>(() => g.ResumeAsync("x", Opts(new InMemoryCheckpointer(), "never-ran")));
        Assert.Contains("has no checkpoint", ex.Message);

        var noCp = await Assert.ThrowsAsync<ResumeException>(() => g.ResumeAsync("x", new RunOptions { ThreadId = "t" }));
        Assert.Contains("has no checkpoint", noCp.Message);
    }

    [Fact(DisplayName = "new input on a parked thread is refused, the input is not folded, and the pause survives")]
    public async Task NewInputOnParkedThread()
    {
        var cp = new InMemoryCheckpointer();
        var g = Ask();
        var opts = Opts(cp, "t-parked");
        await g.RunAsync(null, opts);

        var ex = await Assert.ThrowsAsync<ResumeException>(() =>
            g.RunAsync(new Dictionary<string, object?> { ["log"] = "sneaky" }, opts));
        Assert.Contains("waiting on interrupt(s)", ex.Message);
        Assert.Contains("ResumeAsync", ex.Message);

        Assert.Empty((await IlmekRuntime.ThreadStateAsync(g, cp, "t-parked"))!.GetList<string>("log"));
        var done = await g.ResumeAsync("yes", opts);
        Assert.Equal(new List<string> { "yes" }, done.State!.GetList<string>("log"));
    }

    // ── answer shapes ───────────────────────────────────────────────────────

    [Fact(DisplayName = "an object answer to a single pause is the answer — even one keyed exactly like the pending id")]
    public async Task ObjectAnswerIsTheAnswer()
    {
        var cp = new InMemoryCheckpointer();
        var g = Ask();
        var opts = Opts(cp, "t-object");
        await g.RunAsync(null, opts);

        // Shaped like a keyed-answer map; the pending COUNT decides, not the type.
        var answer = new Dictionary<string, object?> { ["work:interrupt#0"] = "decoy" };
        var done = await g.ResumeAsync(answer, opts);
        Assert.Same(answer, done.State!.GetList<object>("log").Single());
    }

    [Fact(DisplayName = "null is a legal answer")]
    public async Task NullAnswer()
    {
        var cp = new InMemoryCheckpointer();
        var g = Ask();
        var opts = Opts(cp, "t-null");
        await g.RunAsync(null, opts);

        var done = await g.ResumeAsync(null, opts);
        Assert.Equal(RunStatus.Done, done.Status);
        // The raw channel: GetList<T> filters by type and would drop the null.
        Assert.Equal(new List<object?> { null }, done.State!["log"]);
    }

    [Fact(DisplayName = "ResumeKeyed works for a single pause, so a generic UI needs no special case")]
    public async Task KeyedSinglePause()
    {
        var cp = new InMemoryCheckpointer();
        var g = Ask();
        var opts = Opts(cp, "t-keyed-single");
        var paused = await g.RunAsync(null, opts);

        var done = await g.ResumeKeyedAsync(paused.Pending.ToDictionary(p => p.Id, _ => (object?)"k"), opts);
        Assert.Equal(new List<string> { "k" }, done.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "PendingInterruptsAsync and ThreadStateAsync report nothing for an unknown thread")]
    public async Task IntrospectionUnknownThread()
    {
        var cp = new InMemoryCheckpointer();
        Assert.Empty(await IlmekRuntime.PendingInterruptsAsync(cp, "nobody"));
        Assert.Null(await IlmekRuntime.ThreadStateAsync(Ask(), cp, "nobody"));
    }

    [Fact(DisplayName = "an interrupted run emits checkpoint, interrupt, run_end — and no node_end for the paused task")]
    public async Task InterruptEventShape()
    {
        var cp = new InMemoryCheckpointer();
        var result = await Ask().RunAsync(null, Opts(cp, "t-events"));

        var tail = result.Events.TakeLast(3).ToList();
        Assert.IsType<CheckpointEvent>(tail[0]);
        var interrupt = Assert.IsType<InterruptEvent>(tail[1]);
        var end = Assert.IsType<RunEndEvent>(tail[2]);
        Assert.Equal(RunStatus.Interrupted, end.Status);
        Assert.Equal(interrupt.Pending, end.Pending);
        Assert.DoesNotContain(result.Events, e => e is NodeEndEvent or StateEvent);
        Assert.Equal("ok?", ((IReadOnlyDictionary<string, object?>)interrupt.Pending[0].Payload!)["q"]);
    }

    [Fact(DisplayName = "the run id changes across an interrupt/resume boundary; the thread id does not")]
    public async Task RunIdChangesAcrossResume()
    {
        var cp = new InMemoryCheckpointer();
        var g = Ask();
        var opts = Opts(cp, "t-ids");
        var first = await g.RunAsync(null, opts);
        var second = await g.ResumeAsync("x", opts);

        Assert.Equal("t-ids", first.ThreadId);
        Assert.Equal("t-ids", second.ThreadId);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.StartsWith("run-", first.RunId);
    }

    [Fact(DisplayName = "a run without a ThreadId gets a generated one")]
    public async Task GeneratedThreadId()
    {
        var g = Graph.Create().Node("a", (_, _) => null).Edge(Graph.Start, "a").Compile();
        var a = await g.RunAsync();
        var b = await g.RunAsync();
        Assert.StartsWith("thread-", a.ThreadId);
        Assert.NotEqual(a.ThreadId, b.ThreadId);
    }

    // ── time travel ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "running from an older CheckpointId forks a branch; the original history stays intact")]
    public async Task ForkFromOlderCheckpoint()
    {
        var cp = new InMemoryCheckpointer();
        var g = Graph.Create("fork")
            .Channel("log", Channels.Append())
            .Node("a", (_, _) => Update.Of("log", "a"))
            .Node("b", (_, _) => Update.Of("log", "b"))
            .Edge(Graph.Start, "a").Edge("a", "b").Edge("b", Graph.End)
            .Compile();
        await g.RunAsync(null, Opts(cp, "t-fork"));

        var history = await cp.ListAsync("t-fork");
        var afterA = history.Single(c => c.Step == 1); // a committed, b planned
        var forked = await g.RunAsync(new Dictionary<string, object?> { ["log"] = "edited" },
            new RunOptions { ThreadId = "t-fork", Checkpointer = cp, CheckpointId = afterA.Id });

        Assert.Equal(new[] { "a", "edited", "b" }, forked.State!.GetList<string>("log"));
        var all = await cp.ListAsync("t-fork");
        Assert.Equal(2, all.Count(c => c.ParentId == afterA.Id)); // original child + the fork
    }

    [Fact(DisplayName = "resuming from an unknown CheckpointId is refused as having no checkpoint")]
    public async Task ResumeUnknownCheckpointId()
    {
        var cp = new InMemoryCheckpointer();
        var g = Ask();
        await g.RunAsync(null, Opts(cp, "t-unknown-ckpt"));
        await Assert.ThrowsAsync<ResumeException>(() =>
            g.ResumeAsync("x", new RunOptions { ThreadId = "t-unknown-ckpt", Checkpointer = cp, CheckpointId = "ckpt-nope" }));
    }

    // ── cancellation (§10.3) ────────────────────────────────────────────────

    [Fact(DisplayName = "an already-cancelled run stops before any node runs and writes nothing")]
    public async Task PreCancelled()
    {
        var cp = new InMemoryCheckpointer();
        var ran = false;
        var g = Graph.Create().Node("a", (_, _) => { ran = true; return null; }).Edge(Graph.Start, "a").Compile();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await g.RunAsync(null, Opts(cp, "t-pre", cts.Token));
        Assert.Equal(RunStatus.Aborted, result.Status);
        Assert.Equal("cancelled", result.AbortReason);
        Assert.False(ran);
        Assert.Equal(new[] { typeof(RunStartEvent), typeof(RunEndEvent) }, result.Events.Select(e => e.GetType()));
        Assert.Null(await cp.GetAsync("t-pre"));
    }

    private static CompiledGraph Pipeline(CancellationTokenSource cts, ConcurrentDictionary<string, int> runs,
        bool cancelInFirst) =>
        Graph.Create("pipeline")
            .Channel("log", Channels.Append())
            .Node("first", (_, ctx) =>
            {
                runs.AddOrUpdate("first", 1, (_, n) => n + 1);
                if (cancelInFirst) cts.Cancel();
                Assert.Equal(cancelInFirst, ctx.CancellationToken.IsCancellationRequested);
                return Update.Of("log", "first");
            })
            .Node("second", (_, _) => { runs.AddOrUpdate("second", 1, (_, n) => n + 1); return Update.Of("log", "second"); })
            .Edge(Graph.Start, "first").Edge("first", "second").Edge("second", Graph.End)
            .Compile();

    [Fact(DisplayName = "cancelling mid-run stops at the superstep boundary; the committed superstep stands and the thread continues")]
    public async Task CancelMidRunThenContinue()
    {
        var cp = new InMemoryCheckpointer();
        var runs = new ConcurrentDictionary<string, int>();
        using var cts = new CancellationTokenSource();
        var g = Pipeline(cts, runs, cancelInFirst: true);

        var aborted = await g.RunAsync(null, Opts(cp, "t-mid", cts.Token));
        Assert.Equal(RunStatus.Aborted, aborted.Status);
        Assert.False(runs.ContainsKey("second"));
        var latest = (await cp.GetAsync("t-mid"))!;
        Assert.Equal(new[] { "second" }, latest.Next.Select(t => t.Node)); // first's superstep committed

        // A fresh token: the thread picks up at "second" — "first" does not re-run.
        var cont = await Pipeline(cts, runs, cancelInFirst: false).RunAsync(null, Opts(cp, "t-mid"));
        Assert.Equal(RunStatus.Done, cont.Status);
        Assert.Equal(1, runs["first"]);
        Assert.Equal(1, runs["second"]);
        Assert.Equal(new[] { "first", "second" }, cont.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "the CancellationToken argument and RunOptions.CancellationToken are linked")]
    public async Task ArgumentTokenIsLinked()
    {
        var cp = new InMemoryCheckpointer();
        var runs = new ConcurrentDictionary<string, int>();
        using var cts = new CancellationTokenSource();
        var g = Pipeline(cts, runs, cancelInFirst: true);

        var aborted = await g.RunAsync(null, new RunOptions { ThreadId = "t-arg", Checkpointer = cp }, cts.Token);
        Assert.Equal(RunStatus.Aborted, aborted.Status);
        Assert.False(runs.ContainsKey("second"));
    }

    [Fact(DisplayName = "cancelling during a retry backoff ends the retry loop promptly, in error, without another attempt")]
    public async Task CancelDuringBackoff()
    {
        var attempts = 0;
        using var cts = new CancellationTokenSource();
        var g = Graph.Create()
            .Node("flaky", (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                cts.CancelAfter(50);
                throw new InvalidOperationException("transient");
            }, retry: new RetryPolicy { MaxAttempts = 5, Backoff = TimeSpan.FromSeconds(30) })
            .Edge(Graph.Start, "flaky")
            .Compile();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await g.RunAsync(null, new RunOptions { CancellationToken = cts.Token });
        sw.Stop();

        Assert.Equal(RunStatus.Error, result.Status);
        Assert.Equal("transient", result.Errors.Single().Error.Message);
        Assert.Equal(1, attempts);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact(DisplayName = "a run aborted while parked work is pending resumes cleanly afterwards")]
    public async Task AbortThenResume()
    {
        var cp = new InMemoryCheckpointer();
        var befores = 0;
        var g = Ask(() => befores++);
        await g.RunAsync(null, Opts(cp, "t-abort-resume"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(RunStatus.Aborted, (await g.ResumeAsync("yes", Opts(cp, "t-abort-resume", cts.Token))).Status);

        var done = await g.ResumeAsync("yes", Opts(cp, "t-abort-resume"));
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(1, befores);
    }

    // ── concurrency ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "concurrent runs on distinct threads sharing one checkpointer are isolated")]
    public async Task ConcurrentThreadsIsolated()
    {
        var cp = new InMemoryCheckpointer();
        var g = Graph.Create("per-thread")
            .Channel("who", Channels.LastWrite(""))
            .Channel("log", Channels.Append())
            .Node("w", async (state, ctx) =>
            {
                var who = state.Get<string>("who");
                await Task.Yield();
                var greeting = await ctx.StepAsync("greet", () => $"hi {who}");
                var ok = await ctx.InterruptAsync<string>(who);
                return Update.Of("log", $"{greeting}:{ok}");
            })
            .Edge(Graph.Start, "w").Edge("w", Graph.End)
            .Compile();

        var names = Enumerable.Range(0, 32).Select(i => $"u{i}").ToList();
        var paused = await Task.WhenAll(names.Select(n => g.RunAsync(
            new Dictionary<string, object?> { ["who"] = n }, Opts(cp, n))));
        Assert.All(paused.Zip(names), p => Assert.Equal(p.Second, p.First.Pending.Single().Payload));

        var done = await Task.WhenAll(names.Select(n => g.ResumeAsync($"ok-{n}", Opts(cp, n))));
        for (var i = 0; i < names.Count; i++)
            Assert.Equal(new List<string> { $"hi {names[i]}:ok-{names[i]}" }, done[i].State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "concurrent resumes of one pause never re-run the pre-pause step")]
    public async Task ConcurrentResumesOfOnePause()
    {
        var cp = new InMemoryCheckpointer();
        var befores = 0;
        var g = Ask(() => Interlocked.Increment(ref befores));
        await g.RunAsync(null, Opts(cp, "t-race"));

        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            try { return (await g.ResumeAsync("same", Opts(cp, "t-race"))).Status; }
            catch (ResumeException) { return (RunStatus?)null; } // lost the race: already resumed
        });
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, befores);
        Assert.Contains(outcomes, s => s == RunStatus.Done);
        Assert.All(outcomes, s => Assert.True(s is null or RunStatus.Done));
        Assert.Empty(await IlmekRuntime.PendingInterruptsAsync(cp, "t-race"));
    }
}
