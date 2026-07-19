namespace Ilmek;

/// <summary>
/// One event from a run (MODEL.md §10).
///
/// Every event carries the envelope: <see cref="Seq"/> is monotonic within a run
/// (1-based, no gaps) so a consumer that drops its connection reconnects and
/// skips everything up to its last-seen seq; <see cref="Ns"/> is the namespace
/// path to the (sub)graph that emitted it — empty at the root, reserved so
/// subgraphs can tag their events without a breaking envelope change.
/// </summary>
public abstract record IlmekEvent
{
    // Not `required`: the engine stamps every event through one helper right
    // before yielding it, so demanding them at construction would only force each
    // call site to repeat what that helper already guarantees.
    public string RunId { get; init; } = "";
    public string ThreadId { get; init; } = "";
    public int Seq { get; init; }
    public IReadOnlyList<string> Ns { get; init; } = Array.Empty<string>();
}

public sealed record RunStartEvent : IlmekEvent;

public sealed record StepStartEvent : IlmekEvent
{
    public required int Step { get; init; }
    public required IReadOnlyList<string> Tasks { get; init; }
}

public sealed record NodeStartEvent : IlmekEvent
{
    public required string Node { get; init; }
    public required string TaskId { get; init; }
}

/// <summary>A payload pushed through <see cref="IContext.Emit"/>. Delivered live, mid-superstep.</summary>
public sealed record CustomEvent : IlmekEvent
{
    public required object? Payload { get; init; }
}

public sealed record NodeEndEvent : IlmekEvent
{
    public required string Node { get; init; }
    public required IReadOnlyDictionary<string, object?> StateUpdate { get; init; }
}

public sealed record NodeErrorEvent : IlmekEvent
{
    public required string Node { get; init; }
    public required Exception Error { get; init; }
}

/// <summary>Announced before each retry attempt (MODEL.md §16); <see cref="Attempt"/> is the upcoming one.</summary>
public sealed record NodeRetryEvent : IlmekEvent
{
    public required string Node { get; init; }
    public required int Attempt { get; init; }
    public required Exception Error { get; init; }
}

public sealed record StateEvent : IlmekEvent
{
    public required IReadOnlyDictionary<string, object?> Channels { get; init; }
}

public sealed record CheckpointEvent : IlmekEvent
{
    public required string Id { get; init; }
}

/// <summary>
/// The run halted on a pause. A distinct event type on purpose: consumers must
/// never parse error text or poll graph state to discover a pause — the defect
/// this design exists to remove.
/// </summary>
public sealed record InterruptEvent : IlmekEvent
{
    public required IReadOnlyList<Pending> Pending { get; init; }
}

public enum RunStatus { Done, Interrupted, Error, Aborted }

public sealed record RunEndEvent : IlmekEvent
{
    public required RunStatus Status { get; init; }

    /// <summary>Final channel values. Set when <see cref="Status"/> is <see cref="RunStatus.Done"/>.</summary>
    public IReadOnlyDictionary<string, object?>? FinalState { get; init; }

    /// <summary>Open interrupts. Set when <see cref="Status"/> is <see cref="RunStatus.Interrupted"/>.</summary>
    public IReadOnlyList<Pending>? Pending { get; init; }

    /// <summary>(node, error) pairs. Set when <see cref="Status"/> is <see cref="RunStatus.Error"/>.</summary>
    public IReadOnlyList<(string Node, Exception Error)>? Errors { get; init; }

    /// <summary>Why the run was cancelled. Set when <see cref="Status"/> is <see cref="RunStatus.Aborted"/>.</summary>
    public string? AbortReason { get; init; }
}
