using Ilmek;

namespace Ilmek.Tests;

/// <summary>
/// The conformance scenarios of MODEL.md §12 — the same list the TypeScript
/// reference passes (see ../../conformance/README.md).
///
/// These are not incidental unit tests; they are the arbiter of the semantics. If
/// a change makes one fail, either the change is wrong or MODEL.md needs a major
/// bump.
/// </summary>
public class ConformanceTests
{
    private static RunOptions Opts(ICheckpointer cp, string threadId) =>
        new() { ThreadId = threadId, Checkpointer = cp };

    private static CompiledGraph Linear(string name, NodeFn fn) =>
        Graph.Create(name)
            .Channel("log", Channels.Append())
            .Channel("items", Channels.LastWrite(new List<object?>()))
            .Node("work", fn)
            .Edge(Graph.Start, "work")
            .Edge("work", Graph.End)
            .Compile();

    // ── §12.1 ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.1 a step executes exactly once across an interrupt/resume cycle")]
    public async Task StepRunsOnceAcrossResume()
    {
        var cp = new InMemoryCheckpointer();
        var creates = 0;
        var charges = 0;

        var g = Linear("checkout", async (_, ctx) =>
        {
            // The effect that must never happen twice.
            var order = await ctx.StepAsync("create_order", () => { creates++; return "order-1"; });
            var answer = await ctx.InterruptAsync<string>(new { question = "Charge order?" });
            var charged = await ctx.StepAsync("charge", () => { charges++; return $"charged-{order}"; });
            return Update.Of("log", $"{order}/{answer}/{charged}");
        });

        var opts = Opts(cp, "t-checkout");

        var paused = await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Interrupted, paused.Status);
        Assert.Equal("interrupt#0", paused.Pending[0].Key);
        Assert.Equal("work", paused.Pending[0].Node);
        Assert.Equal(1, creates);
        Assert.Equal(0, charges); // the pause gates the charge

        var done = await g.ResumeAsync("yes", opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new List<string> { "order-1/yes/charged-order-1" }, done.State!.GetList<string>("log"));

        // THE assertion this whole engine exists for: the node body re-ran from
        // the top, but create_order came back from the journal instead of opening
        // a second order. A pure-replay engine scores 2 here.
        Assert.Equal(1, creates);
        Assert.Equal(1, charges);
    }

    // ── §12.2 ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.2 two interrupts in one node resolve independently, one resume each")]
    public async Task TwoInterruptsResolveIndependently()
    {
        var cp = new InMemoryCheckpointer();
        var g = Linear("double", async (_, ctx) =>
        {
            var first = await ctx.InterruptAsync<string>(new { q = "first" });
            var second = await ctx.InterruptAsync<string>(new { q = "second" });
            return Update.Of("log", $"{first}+{second}");
        });

        var opts = Opts(cp, "t-double");

        var p1 = await g.RunAsync(null, opts);
        Assert.Equal("interrupt#0", p1.Pending[0].Key);

        // Replays: interrupt#0 returns "A" from the journal, interrupt#1 is new.
        var p2 = await g.ResumeAsync("A", opts);
        Assert.Equal(RunStatus.Interrupted, p2.Status);
        Assert.Equal("interrupt#1", p2.Pending[0].Key);

        var done = await g.ResumeAsync("B", opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new List<string> { "A+B" }, done.State!.GetList<string>("log"));
    }

    // ── §12.3 ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.3 an interrupt in a loop with stable keys resumes at the right iteration")]
    public async Task InterruptInLoopResumesCorrectly()
    {
        var cp = new InMemoryCheckpointer();
        var prepared = new List<string>();

        var g = Linear("loop", async (state, ctx) =>
        {
            var approvals = new List<object?>();
            foreach (var item in state.GetList<string>("items"))
            {
                await ctx.StepAsync($"prepare:{item}", () => { prepared.Add(item); return $"prep-{item}"; });
                approvals.Add(await ctx.InterruptAsync<string>(new { q = $"approve {item}" }, $"approve:{item}"));
            }
            return Update.Of("log", approvals);
        });

        var opts = Opts(cp, "t-loop");
        var input = new Dictionary<string, object?> { ["items"] = new List<object?> { "a", "b" } };

        var p1 = await g.RunAsync(input, opts);
        Assert.Equal("approve:a#0", p1.Pending[0].Key);
        Assert.Equal(new List<string> { "a" }, prepared); // the loop halted on a's pause

        var p2 = await g.ResumeAsync("yes-a", opts);
        Assert.Equal("approve:b#0", p2.Pending[0].Key);

        var done = await g.ResumeAsync("yes-b", opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new List<string> { "yes-a", "yes-b" }, done.State!.GetList<string>("log"));

        // Two replays of the loop, one prepare each. Stable keys did the work.
        Assert.Equal(new List<string> { "a", "b" }, prepared);
    }

    // ── §12.4 ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.4 superstep reduce order is task order, not completion order")]
    public async Task ReduceOrderIsTaskOrder()
    {
        var g = Graph.Create("race")
            .Channel("winner", Channels.LastWrite())
            // Declared first, finishes LAST.
            .Node("declaredFirst", async (_, _) =>
            {
                await Task.Delay(60);
                return Update.Of("winner", "declaredFirst");
            })
            // Declared second, finishes FIRST.
            .Node("declaredSecond", (_, _) => Update.Of("winner", "declaredSecond"))
            .Edge(Graph.Start, "declaredFirst")
            .Edge(Graph.Start, "declaredSecond")
            .Edge("declaredFirst", Graph.End)
            .Edge("declaredSecond", Graph.End)
            .Compile();

        // Folding in completion order would let declaredFirst win, since it lands
        // last. Task order makes the result independent of scheduling.
        var result = await g.RunAsync();
        Assert.Equal("declaredSecond", result.State!["winner"]);
    }

    // ── §12.5 ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.5 a crashed superstep replays with zero re-executed steps")]
    public async Task CrashReplaysWithoutReRunningSteps()
    {
        var cp = new InMemoryCheckpointer();
        var expensiveCalls = 0;
        var shouldFail = true;

        var g = Linear("flaky", async (_, ctx) =>
        {
            await ctx.StepAsync("expensive", () => { expensiveCalls++; return "computed"; });

            // Crash after the step, but only on the first attempt.
            if (shouldFail)
            {
                shouldFail = false;
                throw new InvalidOperationException("transient boom");
            }

            return Update.Of("log", "survived");
        });

        var opts = Opts(cp, "t-flaky");

        var crashed = await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Error, crashed.Status);
        Assert.Contains("transient boom", crashed.Errors[0].Error.Message);
        Assert.Equal(1, expensiveCalls);

        var retried = await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Done, retried.Status);
        // The retry replayed the node but the journal had already paid for the step.
        Assert.Equal(1, expensiveCalls);
    }

    // ── §12.6 ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.6 compile(spec) -> ToSpec() round-trips")]
    public void SpecRoundTrips()
    {
        var spec = new GraphSpec
        {
            Name = "support",
            Channels = new Dictionary<string, SpecChannel>
            {
                ["messages"] = new("append"),
                ["intent"] = new("last_write"),
            },
            Nodes = new List<SpecNode>
            {
                new("classify", "set_intent", new Dictionary<string, object?> { ["intent"] = "buy" }),
                new("buy", "say", new Dictionary<string, object?> { ["text"] = "bought" }),
            },
            Edges = new List<SpecEdge>
            {
                new("__start__", "classify"),
                new("classify", "buy", new SpecPredicate { Channel = "intent", Eq = "buy" }),
                new("buy", "__end__"),
            },
        };

        var registry = new Dictionary<string, NodeBuilder>
        {
            ["set_intent"] = cfg => (_, _) => new ValueTask<object?>(Update.Of("intent", cfg["intent"])),
            ["say"] = cfg => (_, _) => new ValueTask<object?>(Update.Of("messages", cfg["text"])),
        };

        var g = Spec.FromSpec(spec, registry).Compile();
        var round = Spec.ToSpec(g);

        Assert.Equal(spec.Name, round.Name);
        Assert.Equal(spec.Channels.Keys.OrderBy(k => k), round.Channels.Keys.OrderBy(k => k));
        Assert.Equal("append", round.Channels["messages"].Reducer);
        Assert.Equal(spec.Nodes.Select(n => (n.Id, n.Type)), round.Nodes.Select(n => (n.Id, n.Type)));
        Assert.Equal(spec.Edges.Select(e => (e.From, e.To)), round.Edges.Select(e => (e.From, e.To)));
        Assert.Equal("buy", round.Edges[1].When!.Eq);
    }

    [Fact(DisplayName = "a spec-built graph runs, and its declarative predicate routes")]
    public async Task SpecGraphRuns()
    {
        var spec = new GraphSpec
        {
            Channels = new Dictionary<string, SpecChannel>
            {
                ["messages"] = new("append"),
                ["intent"] = new("last_write"),
            },
            Nodes = new List<SpecNode>
            {
                new("classify", "set_intent", new Dictionary<string, object?> { ["intent"] = "buy" }),
                new("buy", "say", new Dictionary<string, object?> { ["text"] = "bought" }),
                new("browse", "say", new Dictionary<string, object?> { ["text"] = "browsed" }),
            },
            Edges = new List<SpecEdge>
            {
                new("__start__", "classify"),
                new("classify", "buy", new SpecPredicate { Channel = "intent", Eq = "buy" }),
                new("classify", "browse", new SpecPredicate { Channel = "intent", Neq = "buy" }),
                new("buy", "__end__"),
                new("browse", "__end__"),
            },
        };

        var registry = new Dictionary<string, NodeBuilder>
        {
            ["set_intent"] = cfg => (_, _) => new ValueTask<object?>(Update.Of("intent", cfg["intent"])),
            ["say"] = cfg => (_, _) => new ValueTask<object?>(Update.Of("messages", cfg["text"])),
        };

        var result = await Spec.FromSpec(spec, registry).Compile().RunAsync();
        Assert.Equal(RunStatus.Done, result.Status);
        Assert.Equal(new List<string> { "bought" }, result.State!.GetList<string>("messages"));
    }

    [Fact(DisplayName = "a code router refuses to serialize")]
    public void CodeRouterWillNotSerialize()
    {
        var g = Graph.Create("coded")
            .Channel("x", Channels.LastWrite())
            .Node("a", (_, _) => null, type: "noop")
            .Edge(Graph.Start, "a")
            .Router("a", (_, _) => new[] { Graph.End })
            .Compile();

        var ex = Assert.Throws<GraphException>(() => Spec.ToSpec(g));
        Assert.Contains("cannot be serialized", ex.Message);
    }

    // ── §12.7 ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.7 strict mode raises when a journaled step vanishes on replay")]
    public async Task StrictModeCatchesDivergence()
    {
        var cp = new InMemoryCheckpointer();
        var takeBranch = true;

        var g = Linear("wobbly", async (_, ctx) =>
        {
            // Reading this outside a step is exactly the sin strict mode detects:
            // the node's path is not a function of state + journal.
            if (takeBranch) await ctx.StepAsync("sometimes", () => "did_it");
            await ctx.InterruptAsync<string>(new { q = "ok?" });
            return Update.Of("log", "end");
        });

        var opts = Opts(cp, "t-wobbly");
        Assert.Equal(RunStatus.Interrupted, (await g.RunAsync(null, opts)).Status);

        takeBranch = false; // the world changed under the node

        var result = await g.ResumeAsync("yes", opts);
        Assert.Equal(RunStatus.Error, result.Status);
        var error = Assert.IsType<NondeterminismException>(result.Errors[0].Error);
        Assert.Contains("sometimes#0", error.Message);
        Assert.Contains("deterministic modulo steps", error.Message);
    }

    [Fact(DisplayName = "strict mode can be turned off for a run")]
    public async Task StrictModeOptional()
    {
        var cp = new InMemoryCheckpointer();
        var takeBranch = true;

        var g = Linear("lax", async (_, ctx) =>
        {
            if (takeBranch) await ctx.StepAsync("sometimes", () => "did_it");
            await ctx.InterruptAsync<string>(new { q = "ok?" });
            return Update.Of("log", "end");
        });

        var opts = new RunOptions { ThreadId = "t-lax", Checkpointer = cp, Strict = false };
        Assert.Equal(RunStatus.Interrupted, (await g.RunAsync(null, opts)).Status);
        takeBranch = false;
        Assert.Equal(RunStatus.Done, (await g.ResumeAsync("yes", opts)).Status);
    }

    // ── §12.8 ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.8 concurrent pauses resolve to their own answers")]
    public async Task ConcurrentPausesGetOwnAnswers()
    {
        var cp = new InMemoryCheckpointer();
        var g = Graph.Create("fanout")
            .Channel("log", Channels.Append())
            .Node("a", async (_, ctx) => Update.Of("log", await ctx.InterruptAsync<string>(new { q = "a?" })))
            .Node("b", async (_, ctx) => Update.Of("log", await ctx.InterruptAsync<string>(new { q = "b?" })))
            .Edge(Graph.Start, "a")
            .Edge(Graph.Start, "b")
            .Edge("a", Graph.End)
            .Edge("b", Graph.End)
            .Compile();

        var opts = Opts(cp, "t-fanout");

        var paused = await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Interrupted, paused.Status);
        Assert.Equal(2, paused.Pending.Count);

        // Both tasks journaled the same task-scoped key; only Id separates them.
        Assert.All(paused.Pending, p => Assert.Equal("interrupt#0", p.Key));
        Assert.Equal(new[] { "a:interrupt#0", "b:interrupt#0" }, paused.Pending.Select(p => p.Id).OrderBy(x => x));

        // A bare answer to two pauses is refused, and names the alternative.
        var ex = await Assert.ThrowsAsync<ResumeException>(() => g.ResumeAsync("yes", opts));
        Assert.Contains("2 interrupts are pending", ex.Message);

        var answers = paused.Pending.ToDictionary(p => p.Id, p => (object?)$"ans-{p.Node}");
        var done = await g.ResumeKeyedAsync(answers, opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new[] { "ans-a", "ans-b" }, done.State!.GetList<string>("log").OrderBy(x => x));
    }

    // ── §12.9 ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.9 send fans out once per payload, each with its own input state")]
    public async Task SendFansOut()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<long>();

        var g = Graph.Create("mapreduce")
            .Channel("items", Channels.LastWrite(new List<object?>()))
            .Channel("results", Channels.Append())
            .Node("map", (_, _) => null)
            .Node("worker", (state, _) =>
            {
                var n = Convert.ToInt64(state["n"]);
                seen.Add(n);
                return Update.Of("results", n * 10);
            })
            .Node("join", (_, _) => null)
            .Edge(Graph.Start, "map")
            .Router("map", (state, _) => state.GetList<object>("items")
                .Select(i => (object)new Send("worker", new Dictionary<string, object?> { ["n"] = i })))
            .Edge("worker", "join")
            .Edge("join", Graph.End)
            .Compile();

        var input = new Dictionary<string, object?> { ["items"] = new List<object?> { 1L, 2L, 3L } };
        var result = await g.RunAsync(input);

        Assert.Equal(RunStatus.Done, result.Status);
        // Each worker saw its own payload, not the shared channel state.
        Assert.Equal(new long[] { 1, 2, 3 }, seen.OrderBy(x => x));
        // Fan-in: all three results, order independent of completion.
        Assert.Equal(new long[] { 10, 20, 30 },
            result.State!.GetList<long>("results").OrderBy(x => x));
    }

    [Fact(DisplayName = "an interrupt in one fan-out branch does not collide with its siblings")]
    public async Task FanOutInterruptsAreDistinct()
    {
        var cp = new InMemoryCheckpointer();
        var g = Graph.Create("fanout-hitl")
            .Channel("out", Channels.Append())
            .Node("map", (_, _) => null)
            .Node("worker", async (state, ctx) =>
            {
                var id = (string)state["id"]!;
                var ok = await ctx.InterruptAsync<string>(new { q = $"approve {id}?" });
                return Update.Of("out", $"{id}:{ok}");
            })
            .Edge(Graph.Start, "map")
            .Router("map", (_, _) => new object[]
            {
                new Send("worker", new Dictionary<string, object?> { ["id"] = "a" }),
                new Send("worker", new Dictionary<string, object?> { ["id"] = "b" }),
            })
            .Edge("worker", Graph.End)
            .Compile();

        var opts = Opts(cp, "t-fanout-hitl");
        var paused = await g.RunAsync(null, opts);

        Assert.Equal(2, paused.Pending.Count);
        // Distinct ids despite both journaling interrupt#0 (keyed off TaskKey).
        Assert.Equal(new[] { "worker#0:interrupt#0", "worker#1:interrupt#0" },
            paused.Pending.Select(p => p.Id).OrderBy(x => x));

        var answers = paused.Pending.ToDictionary(p => p.Id, p => (object?)"yes");
        var done = await g.ResumeKeyedAsync(answers, opts);
        Assert.Equal(new[] { "a:yes", "b:yes" }, done.State!.GetList<string>("out").OrderBy(x => x));
    }

    // ── §12.10 ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.10 command goto overrides static edges; update reduces first")]
    public async Task CommandGotoOverridesEdges()
    {
        var g = Graph.Create("router-node")
            .Channel("log", Channels.Append())
            .Channel("intent", Channels.LastWrite(""))
            // Sets intent AND routes on the value it just wrote.
            .Node("classify", (_, _) => Command.Create(
                Update.Of("intent", "buy", "log", "classified"), "buy"))
            .Node("buy", (_, _) => Update.Of("log", "bought"))
            .Node("browse", (_, _) => Update.Of("log", "browsed"))
            .Edge(Graph.Start, "classify")
            .Edge("classify", "browse") // a misleading static edge that goto must override
            .Edge("buy", Graph.End)
            .Edge("browse", Graph.End)
            .Compile();

        var result = await g.RunAsync();
        Assert.Equal(RunStatus.Done, result.Status);
        Assert.Equal(new List<string> { "classified", "bought" }, result.State!.GetList<string>("log"));
        Assert.Equal("buy", result.State!["intent"]);
    }

    [Fact(DisplayName = "command with no goto falls back to static edges")]
    public async Task CommandWithoutGotoFallsBack()
    {
        var g = Graph.Create("cmd-fallback")
            .Channel("log", Channels.Append())
            .Node("a", (_, _) => Command.Update_(Update.Of("log", "a")))
            .Node("b", (_, _) => Update.Of("log", "b"))
            .Edge(Graph.Start, "a")
            .Edge("a", "b")
            .Edge("b", Graph.End)
            .Compile();

        var result = await g.RunAsync();
        Assert.Equal(new List<string> { "a", "b" }, result.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "command goto can carry sends discovered inside the node")]
    public async Task CommandGotoCarriesSends()
    {
        var g = Graph.Create("cmd-send")
            .Channel("out", Channels.Append())
            .Node("plan", (_, _) => Command.Goto_(
                new Send("worker", new Dictionary<string, object?> { ["n"] = 1L }),
                new Send("worker", new Dictionary<string, object?> { ["n"] = 2L })))
            .Node("worker", (state, _) => Update.Of("out", Convert.ToInt64(state["n"])))
            .Edge(Graph.Start, "plan")
            .Edge("worker", Graph.End)
            .Compile();

        var result = await g.RunAsync();
        Assert.Equal(new long[] { 1, 2 }, result.State!.GetList<long>("out").OrderBy(x => x));
    }

    // ── §12.11 ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "§12.11 a retried node re-runs its body but NOT its completed steps")]
    public async Task RetryDoesNotReRunCompletedSteps()
    {
        var cp = new InMemoryCheckpointer();
        var charges = 0;
        var apiAttempts = 0;

        var g = Graph.Create("resilient")
            .Channel("log", Channels.Append())
            .Node("charge_then_call", async (_, ctx) =>
            {
                // Journaled: must happen exactly once even across retries.
                await ctx.StepAsync("charge", () => { charges++; return "charged"; });
                // Flaky: throws twice, succeeds on the third attempt.
                await ctx.StepAsync("call_api", () =>
                {
                    apiAttempts++;
                    if (apiAttempts < 3) throw new InvalidOperationException($"transient {apiAttempts}");
                    return "ok";
                });
                return Update.Of("log", "done");
            }, retry: new RetryPolicy { MaxAttempts = 3 })
            .Edge(Graph.Start, "charge_then_call")
            .Edge("charge_then_call", Graph.End)
            .Compile();

        var result = await g.RunAsync(null, Opts(cp, "t-retry"));

        Assert.Equal(RunStatus.Done, result.Status);
        Assert.Equal(1, charges); // ran ONCE across 3 attempts — journaled
        Assert.Equal(3, apiAttempts); // the flaky step itself did re-run
    }

    [Fact(DisplayName = "node_retry events announce each new attempt")]
    public async Task RetryEventsEmitted()
    {
        var n = 0;
        var g = Graph.Create("retry-events")
            .Channel("log", Channels.Append())
            .Node("flaky", (_, _) =>
            {
                n++;
                if (n < 3) throw new InvalidOperationException($"boom {n}");
                return Update.Of("log", "ok");
            }, retry: new RetryPolicy { MaxAttempts = 5 })
            .Edge(Graph.Start, "flaky")
            .Edge("flaky", Graph.End)
            .Compile();

        var attempts = new List<int>();
        await foreach (var ev in g.StreamEvents())
            if (ev is NodeRetryEvent r) attempts.Add(r.Attempt);

        Assert.Equal(new[] { 2, 3 }, attempts);
    }

    [Fact(DisplayName = "an interrupt is never treated as a retryable error")]
    public async Task InterruptIsNotRetried()
    {
        var cp = new InMemoryCheckpointer();
        var attempts = 0;

        var g = Graph.Create("interrupt-not-error")
            .Channel("log", Channels.Append())
            .Node("asks", async (_, ctx) =>
            {
                attempts++;
                await ctx.InterruptAsync<string>(new { q = "?" });
                return Update.Of("log", "resumed");
            }, retry: new RetryPolicy { MaxAttempts = 3 })
            .Edge(Graph.Start, "asks")
            .Edge("asks", Graph.End)
            .Compile();

        var paused = await g.RunAsync(null, Opts(cp, "t-int-retry"));
        Assert.Equal(RunStatus.Interrupted, paused.Status);
        Assert.Equal(1, attempts); // the pause did not trigger the retry loop
    }
}
