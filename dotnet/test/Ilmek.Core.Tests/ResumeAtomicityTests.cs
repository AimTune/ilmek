using Ilmek;

namespace Ilmek.Tests;

/// <summary>
/// Resume must never strand a thread (MODEL.md §6). Two ways it used to:
///
/// <list type="number">
/// <item>A keyed resume that answered only some of several pauses persisted the
/// answers it had, then threw — those entries were answered while the checkpoint
/// still listed them pending, and every later resume failed with
/// <c>already_answered</c>.</item>
/// <item>A resume whose superstep did not commit (node failed, run cancelled)
/// left its answers journaled with the pause still pending — same dead end.</item>
/// </list>
///
/// Both are pinned here, mirroring the TypeScript suite.
/// </summary>
public class ResumeAtomicityTests
{
    private static RunOptions Opts(ICheckpointer cp, string threadId, CancellationToken ct = default) =>
        new() { ThreadId = threadId, Checkpointer = cp, CancellationToken = ct };

    /// <summary>Two nodes pausing in one superstep, each logging its own answer.</summary>
    private static CompiledGraph TwoPauses() =>
        Graph.Create("two-pauses")
            .Channel("log", Channels.Append())
            .Node("a", async (_, ctx) => Update.Of("log", "a=" + await ctx.InterruptAsync<string>(new { q = "a?" })))
            .Node("b", async (_, ctx) => Update.Of("log", "b=" + await ctx.InterruptAsync<string>(new { q = "b?" })))
            .Edge(Graph.Start, "a")
            .Edge(Graph.Start, "b")
            .Edge("a", Graph.End)
            .Edge("b", Graph.End)
            .Compile();

    [Fact(DisplayName = "a keyed resume missing one answer is refused, names the missing id, and writes nothing")]
    public async Task PartialKeyedResumeIsAtomic()
    {
        var cp = new InMemoryCheckpointer();
        var g = TwoPauses();
        var opts = Opts(cp, "t-partial");

        var paused = await g.RunAsync(null, opts);
        Assert.Equal(2, paused.Pending.Count);

        var ex = await Assert.ThrowsAsync<ResumeException>(() =>
            g.ResumeKeyedAsync(new Dictionary<string, object?> { ["a:interrupt#0"] = "A" }, opts));
        Assert.Contains("\"b:interrupt#0\"", ex.Message);
        Assert.Contains("no answer supplied", ex.Message);

        // Nothing was persisted: a's journal entry is still pending.
        var aTask = paused.Pending.Single(p => p.Node == "a").TaskId;
        var journal = await cp.GetJournalAsync(aTask);
        Assert.False(journal.Fetch("interrupt#0")!.Done);

        // The thread is not stranded: the full answer set completes it, and each
        // node gets its own answer.
        var done = await g.ResumeKeyedAsync(new Dictionary<string, object?>
        {
            ["a:interrupt#0"] = "A",
            ["b:interrupt#0"] = "B",
        }, opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new[] { "a=A", "b=B" }, done.State!.GetList<string>("log").OrderBy(x => x));
    }

    [Fact(DisplayName = "answers for ids that are not pending are ignored, as in the TypeScript reference")]
    public async Task ExtraAnswerIdsAreIgnored()
    {
        var cp = new InMemoryCheckpointer();
        var g = TwoPauses();
        var opts = Opts(cp, "t-extra");
        await g.RunAsync(null, opts);

        var done = await g.ResumeKeyedAsync(new Dictionary<string, object?>
        {
            ["a:interrupt#0"] = "A",
            ["b:interrupt#0"] = "B",
            ["c:interrupt#0"] = "nobody asked",
            ["a:interrupt#1"] = "not yet asked",
        }, opts);

        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new[] { "a=A", "b=B" }, done.State!.GetList<string>("log").OrderBy(x => x));
    }

    /// <summary>A node that pauses, then fails exactly once on the pass after its answer.</summary>
    private static CompiledGraph FailsOnceAfterAnswer(Func<int> onPrepare, Func<bool> shouldFail) =>
        Graph.Create("fails-after-answer")
            .Channel("log", Channels.Append())
            .Node("work", async (_, ctx) =>
            {
                await ctx.StepAsync("prepare", () => onPrepare());
                var answer = await ctx.InterruptAsync<string>(new { q = "go?" });
                if (shouldFail()) throw new InvalidOperationException("post-answer boom");
                return Update.Of("log", answer);
            })
            .Edge(Graph.Start, "work")
            .Edge("work", Graph.End)
            .Compile();

    [Fact(DisplayName = "a resume whose node fails can be retried with the same answer")]
    public async Task FailedResumeRetriesWithSameAnswer()
    {
        var cp = new InMemoryCheckpointer();
        var prepares = 0;
        var fail = true;
        var g = FailsOnceAfterAnswer(() => ++prepares, () => { var f = fail; fail = false; return f; });
        var opts = Opts(cp, "t-retry-resume");

        Assert.Equal(RunStatus.Interrupted, (await g.RunAsync(null, opts)).Status);

        var failed = await g.ResumeAsync("yes", opts);
        Assert.Equal(RunStatus.Error, failed.Status);
        Assert.Equal("post-answer boom", failed.Errors[0].Error.Message);

        // Still parked on the same pause — the failed superstep committed nothing.
        Assert.Single(await IlmekRuntime.PendingInterruptsAsync(cp, "t-retry-resume"));

        var done = await g.ResumeAsync("yes", opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new List<string> { "yes" }, done.State!.GetList<string>("log"));
        Assert.Equal(1, prepares); // the pre-pause step ran once across all three runs
    }

    [Fact(DisplayName = "a resume cancelled before its first superstep can be retried with the same answer")]
    public async Task CancelledResumeRetriesWithSameAnswer()
    {
        var cp = new InMemoryCheckpointer();
        var prepares = 0;
        var g = FailsOnceAfterAnswer(() => ++prepares, () => false);

        Assert.Equal(RunStatus.Interrupted, (await g.RunAsync(null, Opts(cp, "t-cancel-resume"))).Status);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var aborted = await g.ResumeAsync("yes", Opts(cp, "t-cancel-resume", cancelled.Token));
        Assert.Equal(RunStatus.Aborted, aborted.Status);
        Assert.Equal("cancelled", aborted.AbortReason);

        var done = await g.ResumeAsync("yes", Opts(cp, "t-cancel-resume"));
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new List<string> { "yes" }, done.State!.GetList<string>("log"));
        Assert.Equal(1, prepares);
    }

    [Fact(DisplayName = "after a resume that did not commit, a DIFFERENT answer is refused and the original still works")]
    public async Task ChangedAnswerAfterFailedResumeIsRefused()
    {
        var cp = new InMemoryCheckpointer();
        var fail = true;
        var g = FailsOnceAfterAnswer(() => 0, () => { var f = fail; fail = false; return f; });
        var opts = Opts(cp, "t-changed");

        await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Error, (await g.ResumeAsync("yes", opts)).Status);

        var ex = await Assert.ThrowsAsync<ResumeException>(() => g.ResumeAsync("no", opts));
        Assert.Contains("already answered", ex.Message);
        Assert.Contains("\"yes\"", ex.Message); // names what it was answered with
        Assert.Contains("same answer", ex.Message);

        var done = await g.ResumeAsync("yes", opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new List<string> { "yes" }, done.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "the same-answer check compares structurally, so an equal object answer is accepted")]
    public async Task SameAnswerComparesStructurally()
    {
        var cp = new InMemoryCheckpointer();
        var fail = true;
        var g = Graph.Create("object-answer")
            .Channel("log", Channels.Append())
            .Node("work", async (_, ctx) =>
            {
                var answer = await ctx.InterruptAsync<object>(new { q = "go?" });
                if (fail) { fail = false; throw new InvalidOperationException("boom"); }
                return Update.Of("log", answer);
            })
            .Edge(Graph.Start, "work")
            .Edge("work", Graph.End)
            .Compile();
        var opts = Opts(cp, "t-object");

        await g.RunAsync(null, opts);
        var first = new Dictionary<string, object?> { ["approved"] = true, ["note"] = "ok" };
        Assert.Equal(RunStatus.Error, (await g.ResumeAsync(first, opts)).Status);

        // A different instance with the same content is the same answer.
        var again = new Dictionary<string, object?> { ["approved"] = true, ["note"] = "ok" };
        var done = await g.ResumeAsync(again, opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Same(first, done.State!.GetList<object>("log").Single()); // the journaled answer stands
    }

    [Fact(DisplayName = "after a failed keyed resume of concurrent pauses, the same answers complete the thread")]
    public async Task FailedConcurrentResumeRetries()
    {
        var cp = new InMemoryCheckpointer();
        var bFails = true;
        var g = Graph.Create("concurrent-fail")
            .Channel("log", Channels.Append())
            .Node("a", async (_, ctx) => Update.Of("log", "a=" + await ctx.InterruptAsync<string>()))
            .Node("b", async (_, ctx) =>
            {
                var ans = await ctx.InterruptAsync<string>();
                if (bFails) { bFails = false; throw new InvalidOperationException("b boom"); }
                return Update.Of("log", "b=" + ans);
            })
            .Edge(Graph.Start, "a").Edge(Graph.Start, "b")
            .Edge("a", Graph.End).Edge("b", Graph.End)
            .Compile();
        var opts = Opts(cp, "t-conc-fail");
        await g.RunAsync(null, opts);

        var answers = new Dictionary<string, object?> { ["a:interrupt#0"] = "A", ["b:interrupt#0"] = "B" };
        var failed = await g.ResumeKeyedAsync(answers, opts);
        Assert.Equal(RunStatus.Error, failed.Status);
        Assert.Equal("b", failed.Errors.Single().Node);

        var done = await g.ResumeKeyedAsync(answers, opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new[] { "a=A", "b=B" }, done.State!.GetList<string>("log").OrderBy(x => x));
    }
}
