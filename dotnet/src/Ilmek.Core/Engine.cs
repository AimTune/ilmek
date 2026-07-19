using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Ilmek;

/// <summary>Options for a run (MODEL.md §11).</summary>
public sealed record RunOptions
{
    public string? ThreadId { get; init; }
    public ICheckpointer? Checkpointer { get; init; }

    /// <summary>Resume from a specific checkpoint instead of the latest — forks a branch (MODEL.md §7).</summary>
    public string? CheckpointId { get; init; }

    /// <summary>Superstep budget.</summary>
    public int RecursionLimit { get; init; } = 25;

    /// <summary>Detect replay divergence (MODEL.md §5.5).</summary>
    public bool Strict { get; init; } = true;

    public IReadOnlyDictionary<string, object?> Meta { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// Cooperative cancellation (MODEL.md §10.3). Checked at every superstep
    /// boundary — an aborted run stops there and ends with
    /// <see cref="RunStatus.Aborted"/>. The same token reaches node code as
    /// <see cref="IContext.CancellationToken"/>; the engine never force-kills a
    /// running task, so cancellation is only as responsive as the node's own
    /// handling.
    /// </summary>
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;
}

internal enum RunMode { Input, Resume }

internal sealed record RunRequest(RunMode Mode, IReadOnlyDictionary<string, object?>? Input,
    IReadOnlyDictionary<string, object?>? Answers, bool SingleAnswer, object? BareAnswer);

/// <summary>
/// The superstep loop (MODEL.md §4).
///
/// <code>plan → run tasks concurrently → reduce → checkpoint → repeat</code>
///
/// Two invariants carry most of the design:
/// <list type="bullet">
/// <item><b>Task isolation</b> — every task in a superstep sees the state as of
/// the previous checkpoint, never a sibling's update.</item>
/// <item><b>Superstep atomicity</b> — if any task pauses on an interrupt, no
/// update is reduced. All tasks of that superstep replay on resume, and the ones
/// that had already finished fast-forward through their journals, so no completed
/// step runs twice.</item>
/// </list>
/// </summary>
internal static class Engine
{
    private abstract record Outcome;
    private sealed record OkOutcome(IReadOnlyDictionary<string, object?> Update, IReadOnlyList<object>? Goto) : Outcome;
    private sealed record InterruptOutcome(string Key, object? Payload) : Outcome;
    private sealed record ErrorOutcome(Exception Error) : Outcome;

    private abstract record Message;
    private sealed record EventMessage(object? Payload) : Message;
    private sealed record RetryMessage(string Node, int Attempt, Exception Error) : Message;
    private sealed record ResultMessage(ScheduledTask Task, string TaskId, Outcome Outcome) : Message;

    private sealed record TaskResult(ScheduledTask Task, string TaskId, int Index, Outcome Outcome);

    public static async IAsyncEnumerable<IlmekEvent> RunStreamAsync(
        CompiledGraph g, RunRequest request, RunOptions opts,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var checkpointer = opts.Checkpointer;
        var threadId = opts.ThreadId ?? GenId("thread");
        var runId = GenId("run");
        var recursionLimit = opts.RecursionLimit;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(opts.CancellationToken, ct);
        var token = linked.Token;

        var seq = 0;
        IReadOnlyList<string> ns = Array.Empty<string>();

        // Assigned in one place so no call site can forget the envelope or hand
        // out a duplicate seq.
        T Stamp<T>(T ev) where T : IlmekEvent =>
            ev with { RunId = runId, ThreadId = threadId, Seq = ++seq, Ns = ns };

        IContext PlanCtx(State state, int step) =>
            new PlanContext(g, state, threadId, runId, step, recursionLimit, opts.Meta, token);

        var latest = checkpointer is not null
            ? await checkpointer.GetAsync(threadId, opts.CheckpointId, token).ConfigureAwait(false)
            : null;

        Dictionary<string, object?> channels;
        List<ScheduledTask> next;
        int step;
        string? parentId;
        string? planId;

        if (request.Mode == RunMode.Input)
        {
            // Fresh input onto a thread parked on a human answer: refuse rather
            // than silently discarding the pause.
            if (latest is { Pending.Count: > 0 })
            {
                throw new ResumeException(
                    $"thread \"{threadId}\" is waiting on interrupt(s) " +
                    $"[{string.Join(", ", latest.Pending.Select(p => p.Key))}]. Answer them with ResumeAsync — " +
                    "a plain RunAsync would drop the pause.");
            }

            channels = FoldUpdate(g, latest?.Channels ?? new Dictionary<string, object?>(),
                request.Input ?? new Dictionary<string, object?>());
            parentId = latest?.Id;
            planId = latest?.Id;
            step = latest?.Step ?? 0;
            // A thread resumed with plain input continues from its checkpoint's
            // plan; a fresh thread enters at START.
            next = latest is { Next.Count: > 0 }
                ? latest.Next.ToList()
                : Routing.Schedule(Routing.Targets(g, Graph.Start, g.Materialize(channels),
                    PlanCtx(g.Materialize(channels), step)));
        }
        else
        {
            if (latest is null)
                throw new ResumeException(
                    $"cannot resume thread \"{threadId}\" — it has no checkpoint. " +
                    "Resume requires a checkpointer and a prior interrupted run.");
            if (latest.Pending.Count == 0)
                throw new ResumeException(
                    $"cannot resume thread \"{threadId}\" — it is not interrupted. Use RunAsync to send new input.");

            var answers = request.SingleAnswer
                ? SingleAnswer(latest.Pending, request.BareAnswer)
                : request.Answers ?? new Dictionary<string, object?>();

            await AnswerPendingAsync(checkpointer!, latest.Pending, answers, token).ConfigureAwait(false);

            channels = new Dictionary<string, object?>(latest.Channels);
            next = latest.Next.ToList();
            step = latest.Step;
            parentId = latest.Id;
            // NOT latest.Id — the tasks were planned from latest.PlanId, and their
            // journals are keyed by it. Losing this re-runs every completed step.
            planId = latest.PlanId;
        }

        async Task<Checkpoint> WriteCheckpointAsync(
            IReadOnlyDictionary<string, object?> ch, IReadOnlyList<ScheduledTask> nx,
            IReadOnlyList<Pending> pending, int stepNo)
        {
            var ckpt = new Checkpoint(Checkpoint.GenerateId(), parentId, planId, threadId,
                ch, nx, pending, stepNo, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (checkpointer is not null) await checkpointer.PutAsync(ckpt, token).ConfigureAwait(false);
            return ckpt;
        }

        yield return Stamp(new RunStartEvent());

        while (true)
        {
            // Cooperative cancellation at the superstep boundary (MODEL.md §10.3).
            // The last committed checkpoint stands, so an aborted run resumes
            // cleanly later — abort stops the stream, it does not roll back.
            if (token.IsCancellationRequested)
            {
                yield return Stamp(new RunEndEvent { Status = RunStatus.Aborted, AbortReason = "cancelled" });
                yield break;
            }

            if (next.Count == 0)
            {
                yield return Stamp(new RunEndEvent
                {
                    Status = RunStatus.Done,
                    FinalState = g.Materialize(channels).AsDictionary(),
                });
                yield break;
            }

            if (step >= recursionLimit)
            {
                throw new RecursionLimitException(
                    $"run {runId} exceeded {recursionLimit} supersteps (still scheduling " +
                    $"[{string.Join(", ", next.Select(t => t.TaskKey))}]). Raise RecursionLimit or check for a cycle.");
            }

            // ── dispatch ──────────────────────────────────────────────────
            string TaskIdOf(ScheduledTask t) => $"{threadId}:{planId ?? "root"}:{t.TaskKey}";

            yield return Stamp(new StepStartEvent { Step = step, Tasks = next.Select(t => t.TaskKey).ToList() });
            foreach (var task in next)
                yield return Stamp(new NodeStartEvent { Node = task.Node, TaskId = TaskIdOf(task) });

            var channelState = g.Materialize(channels);
            var queue = System.Threading.Channels.Channel.CreateUnbounded<Message>();
            var taskIndex = new Dictionary<string, int>();

            var journals = new List<Journal>();
            foreach (var task in next)
            {
                var id = TaskIdOf(task);
                taskIndex[id] = taskIndex.Count;
                journals.Add(checkpointer is not null
                    ? await checkpointer.GetJournalAsync(id, token).ConfigureAwait(false)
                    : new Journal());
            }

            for (var i = 0; i < next.Count; i++)
            {
                var task = next[i];
                var taskId = TaskIdOf(task);
                var journal = journals[i];
                // A send task's state IS its input payload (MODEL.md §14); a plain
                // task reads the shared channel state.
                var taskState = task.IsSend ? ToState(task.Input) : channelState;

                _ = Task.Run(async () =>
                {
                    var outcome = await RunTaskAsync(g, task.Node, taskState, new TaskInit(
                        taskId, threadId, runId, step, recursionLimit, opts.Meta, checkpointer,
                        opts.Strict, token, journal,
                        payload => queue.Writer.TryWrite(new EventMessage(payload)),
                        (attempt, error) => queue.Writer.TryWrite(new RetryMessage(task.Node, attempt, error))
                    )).ConfigureAwait(false);

                    queue.Writer.TryWrite(new ResultMessage(task, taskId, outcome));
                }, CancellationToken.None);
            }

            // ── await ─────────────────────────────────────────────────────
            // One message at a time, so a node's Emit reaches the consumer while
            // its siblings are still running rather than being buffered to the end.
            var results = new List<TaskResult>();
            var remaining = next.Count;

            while (remaining > 0)
            {
                var msg = await queue.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);

                if (msg is EventMessage custom)
                {
                    yield return Stamp(new CustomEvent { Payload = custom.Payload });
                    continue;
                }
                if (msg is RetryMessage retry)
                {
                    yield return Stamp(new NodeRetryEvent
                    {
                        Node = retry.Node,
                        Attempt = retry.Attempt,
                        Error = retry.Error,
                    });
                    continue;
                }

                var result = (ResultMessage)msg;
                remaining--;
                results.Add(new TaskResult(result.Task, result.TaskId,
                    taskIndex.TryGetValue(result.TaskId, out var idx) ? idx : results.Count, result.Outcome));

                if (result.Outcome is OkOutcome ok)
                    yield return Stamp(new NodeEndEvent { Node = result.Task.Node, StateUpdate = ok.Update });
                else if (result.Outcome is ErrorOutcome err)
                    yield return Stamp(new NodeErrorEvent { Node = result.Task.Node, Error = err.Error });
                // A paused task has not ended — the run's interrupt event covers it.
            }

            // ── settle ────────────────────────────────────────────────────
            var errors = results
                .Where(r => r.Outcome is ErrorOutcome)
                .Select(r => (r.Task.Node, ((ErrorOutcome)r.Outcome).Error))
                .ToList();

            if (errors.Count > 0)
            {
                yield return Stamp(new RunEndEvent { Status = RunStatus.Error, Errors = errors });
                yield break;
            }

            var pendingList = results
                .Where(r => r.Outcome is InterruptOutcome)
                .Select(r =>
                {
                    var o = (InterruptOutcome)r.Outcome;
                    // `Id` is thread-scoped, `Key` is task-scoped. It keys off
                    // TaskKey (not Node), so two sends to one node — both
                    // journaling interrupt#0 — still get distinct ids.
                    return new Pending($"{r.Task.TaskKey}:{o.Key}", r.TaskId, r.Task.Node, o.Key, o.Payload);
                })
                .ToList();

            if (pendingList.Count > 0)
            {
                // Superstep atomicity: nothing is reduced. Every task of this
                // superstep is rescheduled (inputs and keys intact), journals
                // intact, so the finished ones replay for free.
                var pausedCkpt = await WriteCheckpointAsync(
                    channels, results.Select(r => r.Task).ToList(), pendingList, step).ConfigureAwait(false);

                yield return Stamp(new CheckpointEvent { Id = pausedCkpt.Id });
                yield return Stamp(new InterruptEvent { Pending = pendingList });
                yield return Stamp(new RunEndEvent { Status = RunStatus.Interrupted, Pending = pendingList });
                yield break;
            }

            // ── advance ───────────────────────────────────────────────────
            Dictionary<string, object?> reduced;
            List<(string Node, Exception Error)>? reduceErrors = null;
            try
            {
                reduced = ReduceUpdates(g, channels, results);
            }
            catch (NodeReduceException ex)
            {
                reduced = new Dictionary<string, object?>(channels);
                reduceErrors = new List<(string, Exception)> { (ex.Node, ex.InnerException!) };
            }

            if (reduceErrors is not null)
            {
                // A reduce failure (e.g. a write to an undeclared channel) is a
                // node bug like any other, so it settles the same way instead of
                // escaping as a raw exception to whoever is consuming the stream.
                foreach (var (node, error) in reduceErrors)
                    yield return Stamp(new NodeErrorEvent { Node = node, Error = error });
                yield return Stamp(new RunEndEvent { Status = RunStatus.Error, Errors = reduceErrors });
                yield break;
            }

            channels = reduced;
            next = NextTasksAfter(g, results, channels, step + 1, PlanCtx);

            var ckpt = await WriteCheckpointAsync(channels, next, Array.Empty<Pending>(), step + 1)
                .ConfigureAwait(false);

            // The superstep committed — these journals are spent (MODEL.md §5.6).
            if (checkpointer is not null)
                foreach (var r in results)
                    await checkpointer.DropJournalAsync(r.TaskId, token).ConfigureAwait(false);

            yield return Stamp(new StateEvent { Channels = g.Materialize(channels).AsDictionary() });
            yield return Stamp(new CheckpointEvent { Id = ckpt.Id });

            step += 1;
            parentId = ckpt.Id;
            planId = ckpt.Id;
        }
    }

    // ── planning ────────────────────────────────────────────────────────────

    private static List<ScheduledTask> NextTasksAfter(
        CompiledGraph g, IReadOnlyList<TaskResult> results, IReadOnlyDictionary<string, object?> channels,
        int step, Func<State, int, IContext> planCtx)
    {
        var state = g.Materialize(channels);
        var ctx = planCtx(state, step);
        var raw = new List<object>();

        foreach (var r in results)
        {
            if (r.Outcome is not OkOutcome ok) continue;
            // A node's `goto` (MODEL.md §15) overrides its static edges.
            raw.AddRange(ok.Goto is not null
                ? Routing.Resolve(g, r.Task.Node, ok.Goto)
                : Routing.Targets(g, r.Task.Node, state, ctx));
        }

        return Routing.Schedule(raw);
    }

    // ── tasks ───────────────────────────────────────────────────────────────

    private sealed record TaskInit(
        string TaskId, string ThreadId, string RunId, int StepIndex, int RecursionLimit,
        IReadOnlyDictionary<string, object?> Meta, ICheckpointer? Checkpointer, bool Strict,
        CancellationToken Token, Journal Journal, Action<object?> Emit, Action<int, Exception> OnRetry);

    private static async Task<Outcome> RunTaskAsync(CompiledGraph g, string nodeId, State state, TaskInit init)
    {
        var node = g.Nodes[nodeId];
        var policy = node.Retry;

        for (var attempt = 1; ; attempt++)
        {
            // A fresh TaskJournal per attempt resets the occurrence counters and
            // the strict-mode trace, but the underlying journal persists — so a
            // step completed on attempt 1 is already recorded and attempt 2
            // replays it instead of re-running it (MODEL.md §16). Safe retries.
            var tj = new TaskJournal(init.Journal);
            var ctx = new NodeContext(g, state, init.ThreadId, init.RunId, nodeId, init.TaskId,
                init.StepIndex, init.RecursionLimit, init.Meta, init.Checkpointer, tj, init.Emit, init.Token);

            try
            {
                var ret = await node.Fn(state, ctx).ConfigureAwait(false);

                // Strict mode compares this pass's requested keys against the
                // journal (MODEL.md §5.5). Only meaningful when the node ran to
                // completion — a task that threw proves nothing. Inside the try so
                // a violation surfaces as a node error, not an unobserved fault.
                if (init.Strict) tj.CheckDeterminism(nodeId);

                return NormalizeReturn(ret, nodeId);
            }
            catch (InterruptSignalException pause)
            {
                return new InterruptOutcome(pause.Key, pause.Payload);
            }
            catch (Exception error)
            {
                if (policy is null || init.Token.IsCancellationRequested || !policy.ShouldRetry(attempt, error))
                    return new ErrorOutcome(error);

                init.OnRetry(attempt + 1, error);
                var delay = policy.BackoffFor(attempt + 1);
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, init.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return new ErrorOutcome(error); }
                }
                // An abort during the backoff ends the retry loop rather than
                // launching an attempt no one is waiting for.
                if (init.Token.IsCancellationRequested) return new ErrorOutcome(error);
            }
        }
    }

    /// <summary>A node returns a channel update, null, or a <see cref="Command"/> (MODEL.md §15).</summary>
    private static Outcome NormalizeReturn(object? ret, string nodeId) => ret switch
    {
        null => new OkOutcome(new Dictionary<string, object?>(), null),
        Command cmd => new OkOutcome(cmd.StateUpdate ?? new Dictionary<string, object?>(), cmd.Goto),
        IReadOnlyDictionary<string, object?> update => new OkOutcome(update, null),
        IDictionary<string, object?> update => new OkOutcome(
            update.ToDictionary(kv => kv.Key, kv => kv.Value), null),
        _ => new ErrorOutcome(new GraphException(
            $"node \"{nodeId}\" returned {ret.GetType().Name}; a node must return a channel update " +
            "dictionary, null, or a Command.")),
    };

    // ── reduce ──────────────────────────────────────────────────────────────

    private sealed class NodeReduceException : Exception
    {
        public string Node { get; }
        public NodeReduceException(string node, Exception inner) : base(inner.Message, inner) => Node = node;
    }

    /// <summary>
    /// Fold in graph declaration order, NOT completion order, so a non-commutative
    /// reducer gives the same result on every run (MODEL.md §2). When several
    /// tasks share a node (fan-out sends, §14) their scheduled index breaks the
    /// tie, so the fold stays deterministic regardless of which finished first.
    /// </summary>
    private static Dictionary<string, object?> ReduceUpdates(
        CompiledGraph g, IReadOnlyDictionary<string, object?> channels, IReadOnlyList<TaskResult> results)
    {
        int Rank(string node)
        {
            var i = g.NodeOrder.ToList().IndexOf(node);
            return i == -1 ? g.NodeOrder.Count : i;
        }

        var ordered = results
            .OrderBy(r => Rank(r.Task.Node))
            .ThenBy(r => r.Index)
            .ToList();

        var acc = new Dictionary<string, object?>(channels);
        foreach (var r in ordered)
        {
            if (r.Outcome is not OkOutcome ok) continue;
            try
            {
                acc = FoldUpdate(g, acc, ok.Update);
            }
            catch (Exception ex)
            {
                throw new NodeReduceException(r.Task.Node, ex);
            }
        }

        return acc;
    }

    private static Dictionary<string, object?> FoldUpdate(
        CompiledGraph g, IReadOnlyDictionary<string, object?> channels,
        IReadOnlyDictionary<string, object?> update)
    {
        var result = new Dictionary<string, object?>(channels);

        foreach (var (key, value) in update)
        {
            if (!g.Channels.TryGetValue(key, out var channel))
            {
                throw new GraphException(
                    $"write to undeclared channel \"{key}\". Declared: [{string.Join(", ", g.Channels.Keys)}]");
            }

            var current = result.TryGetValue(key, out var existing) ? existing : Channel.Unset;
            result[key] = channel.Reduce(key, current, value);
        }

        return result;
    }

    // ── resume ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A bare answer only makes sense against a single pause. Rather than guessing
    /// from the answer's shape — which breaks the moment someone answers one
    /// interrupt with an object — the count decides, and the error names the
    /// alternative.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> SingleAnswer(
        IReadOnlyList<Pending> pending, object? answer)
    {
        if (pending.Count != 1)
        {
            throw new ResumeException(
                $"{pending.Count} interrupts are pending, so a bare answer is ambiguous. Use " +
                "ResumeKeyedAsync with a dictionary keyed by interrupt id: " +
                $"[{string.Join(", ", pending.Select(p => p.Id))}]");
        }
        return new Dictionary<string, object?> { [pending[0].Id] = answer };
    }

    /// <summary>
    /// Write each human answer into the journal entry waiting for it. Answers
    /// arrive keyed by the thread-scoped id; the journal is addressed by the
    /// task-scoped key. Conflating the two silently drops an answer whenever two
    /// nodes pause in the same superstep.
    /// </summary>
    private static async Task AnswerPendingAsync(
        ICheckpointer checkpointer, IReadOnlyList<Pending> pending,
        IReadOnlyDictionary<string, object?> answers, CancellationToken ct)
    {
        foreach (var p in pending)
        {
            if (!answers.TryGetValue(p.Id, out var answer))
            {
                throw new ResumeException(
                    $"no answer supplied for pending interrupt \"{p.Id}\" (node \"{p.Node}\"). " +
                    $"Expected ids: [{string.Join(", ", pending.Select(x => x.Id))}]");
            }

            var journal = await checkpointer.GetJournalAsync(p.TaskId, ct).ConfigureAwait(false);
            var (ok, reason) = journal.Answer(p.Key, answer);
            if (!ok) throw new ResumeException($"cannot answer \"{p.Key}\" on task \"{p.TaskId}\": {reason}");
            await checkpointer.PutJournalAsync(p.TaskId, journal, ct).ConfigureAwait(false);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>A send payload becomes the task's whole state (MODEL.md §14).</summary>
    private static State ToState(object? input) => input switch
    {
        State s => s,
        IReadOnlyDictionary<string, object?> d => new State(d),
        IDictionary<string, object?> d => new State(d.ToDictionary(kv => kv.Key, kv => kv.Value)),
        // A non-dictionary payload is still legal: the node reads it via
        // ctx.State["input"], which keeps `send(node, 42)` usable.
        null => new State(new Dictionary<string, object?>()),
        _ => new State(new Dictionary<string, object?> { ["input"] = input }),
    };

    private static string GenId(string prefix) =>
        $"{prefix}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
}
