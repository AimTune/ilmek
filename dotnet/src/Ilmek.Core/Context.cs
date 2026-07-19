namespace Ilmek;

/// <summary>
/// The single handle passed to every node, step and router (MODEL.md §8).
///
/// Explicit passing is normative — nothing in ilmek requires ambient storage.
/// An <c>AsyncLocal</c> capture may be offered as sugar over this same object,
/// but never as a second source of truth.
/// </summary>
public interface IContext
{
    CompiledGraph Graph { get; }

    /// <summary>Channel values as of the last checkpoint (this task's view).</summary>
    State State { get; }

    string ThreadId { get; }

    /// <summary>Changes across an interrupt/resume boundary.</summary>
    string RunId { get; }

    string Node { get; }
    string TaskId { get; }
    int StepIndex { get; }

    /// <summary>The run's superstep budget (MODEL.md §4).</summary>
    int RecursionLimit { get; }

    /// <summary>
    /// Supersteps left before <see cref="RecursionLimitException"/>. A node can
    /// read this to wind down gracefully instead of being killed at the limit.
    /// </summary>
    int RemainingSteps { get; }

    /// <summary>Free-form map from the caller.</summary>
    IReadOnlyDictionary<string, object?> Meta { get; }

    /// <summary>This task's journal. Read-only; for debugging.</summary>
    IReadOnlyList<KeyValuePair<string, JournalEntry>> Journal { get; }

    /// <summary>
    /// The run's cancellation token (MODEL.md §10.3). Forward it to any long await
    /// inside the node — an HTTP call, an LLM call — so cancellation reaches the
    /// work that is actually blocking.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Run <paramref name="fn"/> once and journal its result (MODEL.md §5).
    ///
    /// <para>On a replay pass — after an interrupt, after a crashed superstep, or
    /// between retry attempts — the recorded value is returned and
    /// <paramref name="fn"/> is <b>not called</b>. That is what makes "resume from
    /// the line" true: not a restored call stack, but an effect that cannot happen
    /// twice.</para>
    ///
    /// <para>Keys are looked up by name, so a node may branch and skip steps.
    /// Repeats of the same base key are suffixed by occurrence
    /// (<c>charge#0</c>, <c>charge#1</c>), which makes order matter for that key —
    /// so loops should carry a stable key derived from the data.</para>
    /// </summary>
    ValueTask<T> StepAsync<T>(string key, Func<ValueTask<T>> fn);

    /// <summary>Synchronous overload of <see cref="StepAsync{T}(string, Func{ValueTask{T}})"/>.</summary>
    ValueTask<T> StepAsync<T>(string key, Func<T> fn);

    /// <summary>
    /// Pause for a human and return their answer (MODEL.md §6).
    ///
    /// <para>An interrupt is a step whose value comes from a person instead of a
    /// function — same journal, same replay rules. So multiple pauses per node,
    /// pauses inside loops, and concurrent pauses all work without special
    /// cases.</para>
    ///
    /// <para>The first pass never returns: it throws
    /// <see cref="InterruptSignalException"/> to halt the task, and the engine
    /// emits an <c>interrupt</c> event and ends the run.</para>
    /// </summary>
    ValueTask<T> InterruptAsync<T>(object? payload = null, string key = "interrupt");

    /// <summary>Push a custom event into the run's stream (MODEL.md §10). Delivered live, mid-superstep.</summary>
    void Emit(object? payload);

    /// <summary>
    /// Stream one text delta (MODEL.md §10.2). Rides the same transient channel as
    /// <see cref="Emit"/> and is likewise <b>not</b> journaled: on replay a node
    /// re-streams its tokens, while only step values are memoized.
    /// </summary>
    void EmitToken(string text, IReadOnlyDictionary<string, object?>? meta = null);
}

internal sealed class NodeContext : IContext
{
    // Read once: a per-pause environment lookup would be a needless read on a hot
    // path, and nothing legitimately flips this mid-run.
    private static readonly bool BreakOnInterrupt =
        Environment.GetEnvironmentVariable("ILMEK_DEBUG_BREAK_ON_INTERRUPT") == "1";

    private readonly TaskJournal _tj;
    private readonly ICheckpointer? _checkpointer;
    private readonly Action<object?> _emit;

    public CompiledGraph Graph { get; }
    public State State { get; }
    public string ThreadId { get; }
    public string RunId { get; }
    public string Node { get; }
    public string TaskId { get; }
    public int StepIndex { get; }
    public int RecursionLimit { get; }
    public IReadOnlyDictionary<string, object?> Meta { get; }
    public CancellationToken CancellationToken { get; }

    public int RemainingSteps => Math.Max(0, RecursionLimit - StepIndex);
    public IReadOnlyList<KeyValuePair<string, JournalEntry>> Journal => _tj.Journal.Dump();

    internal NodeContext(
        CompiledGraph graph, State state, string threadId, string runId, string node, string taskId,
        int stepIndex, int recursionLimit, IReadOnlyDictionary<string, object?> meta,
        ICheckpointer? checkpointer, TaskJournal tj, Action<object?> emit, CancellationToken ct)
    {
        Graph = graph;
        State = state;
        ThreadId = threadId;
        RunId = runId;
        Node = node;
        TaskId = taskId;
        StepIndex = stepIndex;
        RecursionLimit = recursionLimit;
        Meta = meta;
        _checkpointer = checkpointer;
        _tj = tj;
        _emit = emit;
        CancellationToken = ct;
    }

    // Persist per step: a crash between two steps must not re-run the first.
    private async ValueTask PersistAsync()
    {
        if (_checkpointer is not null) await _checkpointer.PutJournalAsync(TaskId, _tj.Journal);
    }

    public ValueTask<T> StepAsync<T>(string key, Func<T> fn) =>
        StepAsync(key, () => new ValueTask<T>(fn()));

    public async ValueTask<T> StepAsync<T>(string key, Func<ValueTask<T>> fn)
    {
        var full = _tj.ResolveKey(key);
        var entry = _tj.Journal.Fetch(full);

        if (entry is { Done: true }) return (T)entry.Value!;
        if (entry is { Done: false })
        {
            throw new NondeterminismException(
                $"step \"{full}\" collides with a pending interrupt of the same key. " +
                "Give the step a distinct key.");
        }

        var value = await fn().ConfigureAwait(false);
        _tj.Journal.PutDone(full, value);
        await PersistAsync().ConfigureAwait(false);
        return value;
    }

    public async ValueTask<T> InterruptAsync<T>(object? payload = null, string key = "interrupt")
    {
        var full = _tj.ResolveKey(key);
        var entry = _tj.Journal.Fetch(full);

        if (entry is { Done: true }) return (T)entry.Value!;
        if (entry is { Done: false }) throw new InterruptSignalException(full, entry.Payload);

        if (_checkpointer is null)
        {
            throw new ResumeException(
                "ctx.InterruptAsync needs a checkpointer — the pause is stored in the thread's journal " +
                "and there is nowhere to put it. Run with new RunOptions { Checkpointer = new InMemoryCheckpointer() }.");
        }

        _tj.Journal.PutPending(full, payload);
        await PersistAsync().ConfigureAwait(false);

        if (BreakOnInterrupt && System.Diagnostics.Debugger.IsAttached)
        {
            // Paused because ILMEK_DEBUG_BREAK_ON_INTERRUPT=1. `full` is the
            // journal key, `payload` the question going to the human.
            System.Diagnostics.Debugger.Break();
        }

        throw new InterruptSignalException(full, payload);
    }

    public void Emit(object? payload) => _emit(payload);

    public void EmitToken(string text, IReadOnlyDictionary<string, object?>? meta = null) =>
        _emit(new TokenChunk(text, meta));
}

/// <summary>
/// The context handed to routers and guards (MODEL.md §3). They run at plan time,
/// outside any task, so they have no journal — <c>StepAsync</c> and
/// <c>InterruptAsync</c> throw. Route on state; compute in nodes.
/// </summary>
internal sealed class PlanContext : IContext
{
    public CompiledGraph Graph { get; }
    public State State { get; }
    public string ThreadId { get; }
    public string RunId { get; }
    public string Node => "";
    public string TaskId => "";
    public int StepIndex { get; }
    public int RecursionLimit { get; }
    public IReadOnlyDictionary<string, object?> Meta { get; }
    public IReadOnlyList<KeyValuePair<string, JournalEntry>> Journal => Array.Empty<KeyValuePair<string, JournalEntry>>();
    public CancellationToken CancellationToken { get; }
    public int RemainingSteps => Math.Max(0, RecursionLimit - StepIndex);

    internal PlanContext(CompiledGraph graph, State state, string threadId, string runId, int stepIndex,
        int recursionLimit, IReadOnlyDictionary<string, object?> meta, CancellationToken ct)
    {
        Graph = graph;
        State = state;
        ThreadId = threadId;
        RunId = runId;
        StepIndex = stepIndex;
        RecursionLimit = recursionLimit;
        Meta = meta;
        CancellationToken = ct;
    }

    private static GraphException Unavailable(string what) => new(
        $"ctx.{what} is not available in a router or guard — they run at plan time, outside any task, " +
        "and so have no journal (MODEL.md §3). Route on state; compute in nodes.");

    public ValueTask<T> StepAsync<T>(string key, Func<ValueTask<T>> fn) => throw Unavailable("StepAsync");
    public ValueTask<T> StepAsync<T>(string key, Func<T> fn) => throw Unavailable("StepAsync");
    public ValueTask<T> InterruptAsync<T>(object? payload = null, string key = "interrupt") => throw Unavailable("InterruptAsync");
    public void Emit(object? payload) { /* plan-time emits go nowhere: no run stream is open */ }
    public void EmitToken(string text, IReadOnlyDictionary<string, object?>? meta = null) { }
}
