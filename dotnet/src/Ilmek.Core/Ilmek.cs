namespace Ilmek;

/// <summary>The settled outcome of a run (MODEL.md §11).</summary>
public sealed record Result
{
    public required RunStatus Status { get; init; }

    /// <summary>Final channel values. Set when <see cref="Status"/> is <see cref="RunStatus.Done"/>.</summary>
    public State? State { get; init; }

    public required string ThreadId { get; init; }
    public required string RunId { get; init; }

    /// <summary>Open interrupts. Set when <see cref="Status"/> is <see cref="RunStatus.Interrupted"/>.</summary>
    public IReadOnlyList<Pending> Pending { get; init; } = Array.Empty<Pending>();

    /// <summary>(node, error) pairs. Set when <see cref="Status"/> is <see cref="RunStatus.Error"/>.</summary>
    public IReadOnlyList<(string Node, Exception Error)> Errors { get; init; } =
        Array.Empty<(string, Exception)>();

    public string? AbortReason { get; init; }

    public IReadOnlyList<IlmekEvent> Events { get; init; } = Array.Empty<IlmekEvent>();
}

/// <summary>
/// ilmek — an agent graph runtime: state, nodes, edges, checkpointed memory, and
/// durable human-in-the-loop. See MODEL.md; this class is its .NET surface.
///
/// <para><b>The one idea worth knowing:</b> a node that pauses for a human is
/// re-executed from the top when the answer arrives. In a pure-replay engine that
/// means every side effect before the pause happens twice, and avoiding it is the
/// author's problem. Here it is the engine's: wrap each effect in
/// <c>ctx.StepAsync</c> and the journal replays its recorded
/// result instead of calling it again.</para>
///
/// <code>
/// var g = Graph.Create("checkout")
///     .Channel("log", Channels.Append())
///     .Node("checkout", async (state, ctx) =>
///     {
///         // Called once, ever. On the resume pass this returns the journaled order.
///         var order = await ctx.StepAsync("create_order", () => Orders.Create(state));
///         var ok = await ctx.InterruptAsync&lt;string&gt;(new { question = "Charge?" });
///         await ctx.StepAsync("charge", () => Payments.Charge(order, ok));
///         return Update.Of("log", "done");
///     })
///     .Edge(Graph.Start, "checkout")
///     .Edge("checkout", Graph.End)
///     .Compile();
///
/// var opts = new RunOptions { ThreadId = "conv-42", Checkpointer = new InMemoryCheckpointer() };
/// var paused = await g.RunAsync(cart, opts);     // Status: Interrupted
/// var done   = await g.ResumeAsync("yes", opts); // Status: Done
/// </code>
///
/// <para>Named <c>IlmekRuntime</c>, not <c>Ilmek</c>: a static class sharing its
/// namespace's name binds ambiguously at call sites (C# resolves the namespace
/// first). <see cref="GraphExtensions"/> offers the same entry points as
/// extension methods, which is the ergonomic form.</para>
/// </summary>
public static class IlmekRuntime
{
    /// <summary>Stream a run's events lazily (MODEL.md §10).</summary>
    public static IAsyncEnumerable<IlmekEvent> Stream(
        CompiledGraph graph, IReadOnlyDictionary<string, object?>? input = null,
        RunOptions? options = null, CancellationToken ct = default) =>
        Engine.RunStreamAsync(graph,
            new RunRequest(RunMode.Input, input, null, false, null), options ?? new RunOptions(), ct);

    /// <summary>Run to settlement.</summary>
    public static Task<Result> RunAsync(
        CompiledGraph graph, IReadOnlyDictionary<string, object?>? input = null,
        RunOptions? options = null, CancellationToken ct = default) =>
        CollectAsync(Stream(graph, input, options, ct));

    /// <summary>
    /// Stream the continuation of a thread parked on exactly one interrupt
    /// (MODEL.md §6). <paramref name="answer"/> is the human's answer, of any
    /// shape — an object answer is never mistaken for a key map, because the
    /// pending count decides, not the type. Use the keyed form for several pauses.
    /// </summary>
    public static IAsyncEnumerable<IlmekEvent> ResumeStream(
        CompiledGraph graph, object? answer, RunOptions? options = null, CancellationToken ct = default) =>
        Engine.RunStreamAsync(graph,
            new RunRequest(RunMode.Resume, null, null, true, answer), options ?? new RunOptions(), ct);

    /// <summary>Resume to settlement.</summary>
    public static Task<Result> ResumeAsync(
        CompiledGraph graph, object? answer, RunOptions? options = null, CancellationToken ct = default) =>
        CollectAsync(ResumeStream(graph, answer, options, ct));

    /// <summary>
    /// Stream the continuation of a thread, answering interrupts by id
    /// (MODEL.md §6.1). Works for any number of pending interrupts, so it is the
    /// form a UI that renders every open pause should use.
    /// </summary>
    public static IAsyncEnumerable<IlmekEvent> ResumeKeyedStream(
        CompiledGraph graph, IReadOnlyDictionary<string, object?> answers,
        RunOptions? options = null, CancellationToken ct = default) =>
        Engine.RunStreamAsync(graph,
            new RunRequest(RunMode.Resume, null, answers, false, null), options ?? new RunOptions(), ct);

    /// <summary>Resume to settlement, answering by id.</summary>
    public static Task<Result> ResumeKeyedAsync(
        CompiledGraph graph, IReadOnlyDictionary<string, object?> answers,
        RunOptions? options = null, CancellationToken ct = default) =>
        CollectAsync(ResumeKeyedStream(graph, answers, options, ct));

    /// <summary>The interrupts a thread is parked on, or empty if it is not parked.</summary>
    public static async Task<IReadOnlyList<Pending>> PendingInterruptsAsync(
        ICheckpointer checkpointer, string threadId, CancellationToken ct = default) =>
        (await checkpointer.GetAsync(threadId, null, ct).ConfigureAwait(false))?.Pending
        ?? Array.Empty<Pending>();

    /// <summary>The current state of a thread, or null if it has no checkpoint.</summary>
    public static async Task<State?> ThreadStateAsync(
        CompiledGraph graph, ICheckpointer checkpointer, string threadId, CancellationToken ct = default)
    {
        var ckpt = await checkpointer.GetAsync(threadId, null, ct).ConfigureAwait(false);
        return ckpt is null ? null : graph.Materialize(ckpt.Channels);
    }

    private static async Task<Result> CollectAsync(IAsyncEnumerable<IlmekEvent> source)
    {
        var events = new List<IlmekEvent>();
        var status = RunStatus.Error;
        State? state = null;
        IReadOnlyList<Pending> pending = Array.Empty<Pending>();
        IReadOnlyList<(string, Exception)> errors = Array.Empty<(string, Exception)>();
        string? abortReason = null;
        var threadId = "";
        var runId = "";

        await foreach (var ev in source.ConfigureAwait(false))
        {
            events.Add(ev);
            threadId = ev.ThreadId;
            runId = ev.RunId;

            if (ev is not RunEndEvent end) continue;

            status = end.Status;
            if (end.FinalState is not null) state = new State(end.FinalState);
            if (end.Pending is not null) pending = end.Pending;
            if (end.Errors is not null) errors = end.Errors;
            abortReason = end.AbortReason;
        }

        return new Result
        {
            Status = status,
            State = state,
            ThreadId = threadId,
            RunId = runId,
            Pending = pending,
            Errors = errors,
            AbortReason = abortReason,
            Events = events,
        };
    }
}

/// <summary>
/// The ergonomic entry points: <c>graph.RunAsync(input, opts)</c> reads the way a
/// .NET caller expects, and avoids the namespace/class ambiguity that a bare
/// <c>Ilmek.RunAsync</c> would hit.
/// </summary>
public static class GraphExtensions
{
    /// <summary>Run to settlement (MODEL.md §4).</summary>
    public static Task<Result> RunAsync(this CompiledGraph graph,
        IReadOnlyDictionary<string, object?>? input = null, RunOptions? options = null,
        CancellationToken ct = default) =>
        IlmekRuntime.RunAsync(graph, input, options, ct);

    /// <summary>Stream a run's events lazily (MODEL.md §10).</summary>
    public static IAsyncEnumerable<IlmekEvent> StreamEvents(this CompiledGraph graph,
        IReadOnlyDictionary<string, object?>? input = null, RunOptions? options = null,
        CancellationToken ct = default) =>
        IlmekRuntime.Stream(graph, input, options, ct);

    /// <summary>Resume a thread parked on exactly one interrupt (MODEL.md §6).</summary>
    public static Task<Result> ResumeAsync(this CompiledGraph graph, object? answer,
        RunOptions? options = null, CancellationToken ct = default) =>
        IlmekRuntime.ResumeAsync(graph, answer, options, ct);

    /// <summary>Resume, answering interrupts by id (MODEL.md §6.1).</summary>
    public static Task<Result> ResumeKeyedAsync(this CompiledGraph graph,
        IReadOnlyDictionary<string, object?> answers, RunOptions? options = null,
        CancellationToken ct = default) =>
        IlmekRuntime.ResumeKeyedAsync(graph, answers, options, ct);
}
