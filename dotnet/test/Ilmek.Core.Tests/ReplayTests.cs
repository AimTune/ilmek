using System.Collections.Concurrent;
using Ilmek;

namespace Ilmek.Tests;

/// <summary>
/// Journal replay determinism at the engine level (MODEL.md §5, §6, §16): every
/// way a node body re-runs — resume, crash replay, retry — must leave each
/// completed step executed exactly once.
/// </summary>
public class ReplayTests
{
    private static RunOptions Opts(ICheckpointer cp, string thread, bool strict = true) =>
        new() { ThreadId = thread, Checkpointer = cp, Strict = strict };

    private static CompiledGraph OneNode(NodeFn fn, RetryPolicy? retry = null) =>
        Graph.Create("one")
            .Channel("log", Channels.Append())
            .Node("work", fn, retry)
            .Edge(Graph.Start, "work")
            .Edge("work", Graph.End)
            .Compile();

    [Fact(DisplayName = "three sequential pauses in one node: every step between them runs exactly once over four runs")]
    public async Task SequentialPausesStepsOnce()
    {
        var cp = new InMemoryCheckpointer();
        var calls = new ConcurrentDictionary<string, int>();
        int Hit(string k) => calls.AddOrUpdate(k, 1, (_, n) => n + 1);

        var g = OneNode(async (_, ctx) =>
        {
            await ctx.StepAsync("s1", () => Hit("s1"));
            var a = await ctx.InterruptAsync<string>("q1");
            await ctx.StepAsync("s2", () => Hit("s2"));
            var b = await ctx.InterruptAsync<string>("q2");
            await ctx.StepAsync("s3", () => Hit("s3"));
            var c = await ctx.InterruptAsync<string>("q3");
            await ctx.StepAsync("s4", () => Hit("s4"));
            return Update.Of("log", $"{a}{b}{c}");
        });
        var opts = Opts(cp, "t-seq");

        var r = await g.RunAsync(null, opts);
        var payloads = new List<object?> { r.Pending.Single().Payload };
        foreach (var answer in new[] { "A", "B" })
        {
            r = await g.ResumeAsync(answer, opts);
            Assert.Equal(RunStatus.Interrupted, r.Status);
            payloads.Add(r.Pending.Single().Payload);
        }
        var done = await g.ResumeAsync("C", opts);

        Assert.Equal(new List<object?> { "q1", "q2", "q3" }, payloads);
        Assert.Equal(new List<string> { "ABC" }, done.State!.GetList<string>("log"));
        Assert.All(new[] { "s1", "s2", "s3", "s4" }, k => Assert.Equal(1, calls[k]));
    }

    [Fact(DisplayName = "a repeated base key auto-suffixes charge#0, charge#1, charge#2 and each replays its own value")]
    public async Task AutoSuffixedKeysReplayInOrder()
    {
        var cp = new InMemoryCheckpointer();
        var charges = 0;
        IReadOnlyList<KeyValuePair<string, JournalEntry>>? journalOnResume = null;

        var g = OneNode(async (_, ctx) =>
        {
            var got = new List<long>();
            for (var i = 0; i < 3; i++)
                got.Add(await ctx.StepAsync("charge", () => (long)(++charges * 100)));
            var ok = await ctx.InterruptAsync<string>();
            journalOnResume = ctx.Journal;
            return Update.Of("log", $"{string.Join(",", got)}:{ok}");
        });
        var opts = Opts(cp, "t-suffix");

        var paused = await g.RunAsync(null, opts);
        Assert.Equal(3, charges);
        var journal = await cp.GetJournalAsync(paused.Pending[0].TaskId);
        Assert.Equal(new[] { "charge#0", "charge#1", "charge#2", "interrupt#0" }, journal.Keys);

        var done = await g.ResumeAsync("ok", opts);
        Assert.Equal(3, charges); // none re-ran
        Assert.Equal(new List<string> { "100,200,300:ok" }, done.State!.GetList<string>("log"));
        Assert.Equal(new[] { "charge#0", "charge#1", "charge#2", "interrupt#0" }, journalOnResume!.Select(kv => kv.Key));
    }

    [Fact(DisplayName = "the committed superstep's journals are spent: dropped once its update is reduced")]
    public async Task JournalsDroppedAfterCommit()
    {
        var cp = new InMemoryCheckpointer();
        var g = OneNode(async (_, ctx) =>
        {
            await ctx.StepAsync("s", () => 1L);
            await ctx.InterruptAsync<string>();
            return null;
        });
        var opts = Opts(cp, "t-drop");

        var paused = await g.RunAsync(null, opts);
        var taskId = paused.Pending[0].TaskId;
        Assert.NotEmpty((await cp.GetJournalAsync(taskId)).Keys);

        await g.ResumeAsync("x", opts);
        Assert.Empty((await cp.GetJournalAsync(taskId)).Keys);
    }

    [Fact(DisplayName = "a step colliding with a pending interrupt's key raises NondeterminismException")]
    public async Task StepCollidesWithPendingInterrupt()
    {
        // The journal a crashed earlier pass left behind: "x#0" is a pause that
        // was never answered. A body that now asks for a STEP named "x" took a
        // different path — it must fail loudly, not run the effect.
        var cp = new InMemoryCheckpointer();
        var seeded = new Journal();
        seeded.PutPending("x#0", "who?");
        await cp.PutJournalAsync("t-collide:root:work", seeded);

        var effects = 0;
        var g = OneNode(async (_, ctx) =>
        {
            await ctx.StepAsync("x", () => ++effects);
            return null;
        });

        var result = await g.RunAsync(null, Opts(cp, "t-collide"));
        Assert.Equal(RunStatus.Error, result.Status);
        var error = Assert.IsType<NondeterminismException>(result.Errors.Single().Error);
        Assert.Contains("\"x#0\" collides with a pending interrupt", error.Message);
        Assert.Equal(0, effects);
    }

    [Fact(DisplayName = "an interrupt that finds its own entry still pending re-halts instead of re-asking")]
    public async Task PendingInterruptReHalts()
    {
        var cp = new InMemoryCheckpointer();
        var seeded = new Journal();
        seeded.PutPending("interrupt#0", "original question");
        await cp.PutJournalAsync("t-rehalt:root:work", seeded);

        var g = OneNode(async (_, ctx) => Update.Of("log", await ctx.InterruptAsync<string>("new question")));
        var result = await g.RunAsync(null, Opts(cp, "t-rehalt"));

        Assert.Equal(RunStatus.Interrupted, result.Status);
        var p = Assert.Single(result.Pending);
        Assert.Equal("interrupt#0", p.Key);
        // Pinned .NET behaviour: the journaled payload is reported. (The TS
        // reference re-throws with the payload of the current pass; for a
        // deterministic node the two are equal.)
        Assert.Equal("original question", p.Payload);
        Assert.Equal(new[] { "interrupt#0" }, (await cp.GetJournalAsync(p.TaskId)).Keys);
    }

    [Fact(DisplayName = "a crash in one task replays the whole superstep: siblings' steps do not re-run, nothing reduces twice")]
    public async Task CrashReplaysWholeSuperstep()
    {
        var cp = new InMemoryCheckpointer();
        var aEffects = 0;
        var bFails = true;
        var g = Graph.Create("crash")
            .Channel("log", Channels.Append())
            .Node("a", async (_, ctx) =>
            {
                await ctx.StepAsync("effect", () => Interlocked.Increment(ref aEffects));
                return Update.Of("log", "a");
            })
            .Node("b", (_, _) =>
            {
                if (bFails) { bFails = false; throw new InvalidOperationException("crash"); }
                return Update.Of("log", "b");
            })
            .Edge(Graph.Start, "a").Edge(Graph.Start, "b")
            .Compile();
        var opts = Opts(cp, "t-crash");

        var crashed = await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Error, crashed.Status);
        Assert.Contains(crashed.Events, e => e is NodeEndEvent { Node: "a" }); // a finished...
        Assert.Null(await cp.GetAsync("t-crash"));                            // ...but nothing committed

        var rerun = await g.RunAsync(null, opts); // "crash recovery": same thread, empty input
        Assert.Equal(RunStatus.Done, rerun.Status);
        Assert.Equal(1, aEffects);
        Assert.Equal(new[] { "a", "b" }, rerun.State!.GetList<string>("log")); // a reduced once, in task order
    }

    [Fact(DisplayName = "a crash in a later superstep resumes from the last commit; earlier nodes do not run again")]
    public async Task CrashInLaterSuperstep()
    {
        var cp = new InMemoryCheckpointer();
        var firstRuns = 0;
        var fail = true;
        var g = Graph.Create("later")
            .Channel("log", Channels.Append())
            .Node("first", (_, _) => { firstRuns++; return Update.Of("log", "first"); })
            .Node("second", (_, _) =>
            {
                if (fail) { fail = false; throw new InvalidOperationException("boom"); }
                return Update.Of("log", "second");
            })
            .Edge(Graph.Start, "first").Edge("first", "second").Edge("second", Graph.End)
            .Compile();
        var opts = Opts(cp, "t-later");

        Assert.Equal(RunStatus.Error, (await g.RunAsync(null, opts)).Status);
        var done = await g.RunAsync(null, opts);

        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(1, firstRuns);
        Assert.Equal(new[] { "first", "second" }, done.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "new input on a crashed thread folds in before the replay")]
    public async Task CrashReplayWithNewInput()
    {
        var cp = new InMemoryCheckpointer();
        var fail = true;
        var g = Graph.Create("input-after-crash")
            .Channel("n", Channels.LastWrite(0L))
            .Channel("log", Channels.Append())
            .Node("a", (_, _) => Update.Of("n", 1L))
            .Node("b", (state, _) =>
            {
                if (fail) { fail = false; throw new InvalidOperationException("boom"); }
                return Update.Of("log", $"n={state["n"]}");
            })
            .Edge(Graph.Start, "a").Edge("a", "b")
            .Compile();
        var opts = Opts(cp, "t-input");

        await g.RunAsync(null, opts);
        var done = await g.RunAsync(new Dictionary<string, object?> { ["n"] = 41L }, opts);
        Assert.Equal(new[] { "n=41" }, done.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "strict mode: a step skipped on a retry attempt is a NondeterminismException")]
    public async Task StrictCatchesDivergentRetry()
    {
        var cp = new InMemoryCheckpointer();
        var attempt = 0;
        var g = OneNode(async (_, ctx) =>
        {
            attempt++;
            if (attempt == 1)
            {
                await ctx.StepAsync("only-first-time", () => "x");
                throw new InvalidOperationException("flaky");
            }
            return Update.Of("log", "done");
        }, new RetryPolicy { MaxAttempts = 3 });

        var result = await g.RunAsync(null, Opts(cp, "t-strict-retry"));
        Assert.Equal(RunStatus.Error, result.Status);
        Assert.Contains("only-first-time#0", Assert.IsType<NondeterminismException>(result.Errors[0].Error).Message);
    }

    [Fact(DisplayName = "strict mode off: the same divergent retry completes")]
    public async Task LaxAllowsDivergentRetry()
    {
        var cp = new InMemoryCheckpointer();
        var attempt = 0;
        var g = OneNode(async (_, ctx) =>
        {
            attempt++;
            if (attempt == 1)
            {
                await ctx.StepAsync("only-first-time", () => "x");
                throw new InvalidOperationException("flaky");
            }
            return Update.Of("log", "done");
        }, new RetryPolicy { MaxAttempts = 3 });

        var result = await g.RunAsync(null, Opts(cp, "t-lax-retry", strict: false));
        Assert.Equal(RunStatus.Done, result.Status);
        Assert.Equal(2, attempt);
    }

    [Fact(DisplayName = "strict mode: one fewer occurrence of an auto-suffixed key on replay names the lost ordinal")]
    public async Task StrictCatchesLostOccurrence()
    {
        var cp = new InMemoryCheckpointer();
        var times = 2;
        var g = OneNode(async (_, ctx) =>
        {
            for (var i = 0; i < times; i++) await ctx.StepAsync("charge", () => i);
            await ctx.InterruptAsync<string>();
            return null;
        });
        var opts = Opts(cp, "t-ordinal");

        await g.RunAsync(null, opts);
        times = 1;
        var result = await g.ResumeAsync("go", opts);
        Assert.Contains("charge#1", Assert.IsType<NondeterminismException>(result.Errors.Single().Error).Message);
    }

    [Fact(DisplayName = "a retry after a pre-pause step, then a pause, then resume: the step ran once in total")]
    public async Task RetryThenPauseThenResume()
    {
        var cp = new InMemoryCheckpointer();
        var charges = 0;
        var flaky = 0;
        var g = OneNode(async (_, ctx) =>
        {
            await ctx.StepAsync("charge", () => ++charges);
            await ctx.StepAsync("flaky", () => ++flaky < 2 ? throw new InvalidOperationException("t") : "ok");
            var ok = await ctx.InterruptAsync<string>();
            return Update.Of("log", ok);
        }, new RetryPolicy { MaxAttempts = 2 });
        var opts = Opts(cp, "t-retry-pause");

        var paused = await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Interrupted, paused.Status);
        Assert.Single(paused.Events.OfType<NodeRetryEvent>());

        var done = await g.ResumeAsync("yes", opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(1, charges);
        Assert.Equal(2, flaky);
    }

    [Fact(DisplayName = "without a checkpointer steps still memoize within a pass, and nothing is persisted")]
    public async Task StepsWithoutCheckpointer()
    {
        var calls = 0;
        var g = OneNode(async (_, ctx) =>
        {
            var a = await ctx.StepAsync("s", () => ++calls);
            var b = await ctx.StepAsync("s", () => ++calls); // s#1: a distinct step
            return Update.Of("log", $"{a},{b}");
        });
        var result = await g.RunAsync();
        Assert.Equal(new[] { "1,2" }, result.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "an interrupt without a checkpointer ends the run in error with an explanation")]
    public async Task InterruptWithoutCheckpointer()
    {
        var g = OneNode(async (_, ctx) => Update.Of("log", await ctx.InterruptAsync<string>()));
        var result = await g.RunAsync();
        Assert.Equal(RunStatus.Error, result.Status);
        var error = Assert.IsType<ResumeException>(result.Errors.Single().Error);
        Assert.Contains("needs a checkpointer", error.Message);
    }

    [Fact(DisplayName = "a blanket catch(Exception) swallows a pause — the documented .NET caveat, pinned")]
    public async Task BlanketCatchSwallowsPause()
    {
        var cp = new InMemoryCheckpointer();
        var g = OneNode(async (_, ctx) =>
        {
            try { await ctx.InterruptAsync<string>(); }
            catch (Exception) { return Update.Of("log", "swallowed"); }
            return Update.Of("log", "unreachable");
        });

        var result = await g.RunAsync(null, Opts(cp, "t-swallow"));
        Assert.Equal(RunStatus.Done, result.Status); // no pause surfaced
        Assert.Equal(new[] { "swallowed" }, result.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "rethrowing when IsInterrupt says so keeps the pause")]
    public async Task RethrowKeepsPause()
    {
        var cp = new InMemoryCheckpointer();
        var g = OneNode(async (_, ctx) =>
        {
            try { await ctx.InterruptAsync<string>(); }
            catch (Exception ex) when (!InterruptSignalException.IsInterrupt(ex)) { return null; }
            return null;
        });
        Assert.Equal(RunStatus.Interrupted, (await g.RunAsync(null, Opts(cp, "t-rethrow"))).Status);
    }

    [Fact(DisplayName = "an answer of the wrong type for InterruptAsync<T> fails the node with InvalidCastException")]
    public async Task WrongAnswerType()
    {
        var cp = new InMemoryCheckpointer();
        var g = OneNode(async (_, ctx) => Update.Of("log", await ctx.InterruptAsync<int>()));
        var opts = Opts(cp, "t-cast");
        await g.RunAsync(null, opts);

        var result = await g.ResumeAsync("not an int", opts);
        Assert.Equal(RunStatus.Error, result.Status);
        Assert.IsType<InvalidCastException>(result.Errors.Single().Error);
    }
}
