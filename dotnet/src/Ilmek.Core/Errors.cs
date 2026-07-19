namespace Ilmek;

/// <summary>Raised when a graph is structurally invalid (MODEL.md §3).</summary>
public sealed class GraphException : Exception
{
    public GraphException(string message) : base(message) { }
}

/// <summary>Raised when a reducer cannot fold an update into a channel (MODEL.md §2).</summary>
public sealed class ReducerException : Exception
{
    public ReducerException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Raised when a run exceeds its superstep budget (MODEL.md §4).</summary>
public sealed class RecursionLimitException : Exception
{
    public RecursionLimitException(string message) : base(message) { }
}

/// <summary>
/// Raised by strict mode when a replay diverges from the journal (MODEL.md §5.5).
///
/// It means a node body is not deterministic modulo steps: a side effect or a
/// nondeterministic read escaped a <c>ctx.StepAsync</c>.
/// </summary>
public sealed class NondeterminismException : Exception
{
    public NondeterminismException(string message) : base(message) { }
}

/// <summary>Raised when a resume does not match the thread's pending interrupts (MODEL.md §6).</summary>
public sealed class ResumeException : Exception
{
    public ResumeException(string message) : base(message) { }
}

/// <summary>
/// The control signal <c>ctx.InterruptAsync</c> throws to halt a task (MODEL.md §6).
///
/// A pause is ordinary control flow, not a failure — the engine catches this
/// type specifically and turns it into an <c>interrupt</c> event. Unlike the
/// TypeScript reference, which can throw a non-Error value, the CLR requires
/// every throw to derive from <see cref="Exception"/>. So a node that wraps its
/// work in a blanket <c>catch (Exception)</c> WILL swallow a pause: rethrow when
/// <see cref="IsInterrupt"/> says so.
/// </summary>
public sealed class InterruptSignalException : Exception
{
    /// <summary>The journal key this pause is recorded under (task-scoped).</summary>
    public string Key { get; }

    /// <summary>The question handed to the human.</summary>
    public object? Payload { get; }

    public InterruptSignalException(string key, object? payload)
        : base($"ilmek: paused for a human at {key}")
    {
        Key = key;
        Payload = payload;
    }

    /// <summary>True when <paramref name="ex"/> is the pause signal. Rethrow it.</summary>
    public static bool IsInterrupt(Exception ex) => ex is InterruptSignalException;
}
