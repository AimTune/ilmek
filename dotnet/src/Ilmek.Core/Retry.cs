namespace Ilmek;

/// <summary>
/// How a node retries when it throws a non-interrupt exception (MODEL.md §16).
///
/// Retries in ilmek are safe where a pure-replay engine's are not: a retry
/// re-runs the node body, but every step it already completed returns from the
/// journal instead of re-executing. A node that charged a card and then hit a
/// flaky API retries the API call without charging twice — the same guarantee
/// interrupts rely on (§5), turned toward failure instead of a human.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>Total attempts including the first. Must be ≥ 1.</summary>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>Delay before the first retry.</summary>
    public TimeSpan Backoff { get; init; } = TimeSpan.Zero;

    /// <summary>Multiplier applied to the delay after each attempt. 1 = constant.</summary>
    public double Factor { get; init; } = 1;

    /// <summary>Cap on the computed delay.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.MaxValue;

    /// <summary>Retry only exceptions this accepts. Default: every non-interrupt exception.</summary>
    public Func<Exception, bool>? RetryOn { get; init; }

    /// <summary>Delay before <paramref name="attempt"/> (1-based: the first retry is attempt 2).</summary>
    public TimeSpan BackoffFor(int attempt)
    {
        if (Backoff <= TimeSpan.Zero) return TimeSpan.Zero;
        var scaled = Backoff * Math.Pow(Factor, attempt - 2);
        return scaled > MaxBackoff ? MaxBackoff : scaled;
    }

    public bool ShouldRetry(int attempt, Exception error) =>
        attempt < MaxAttempts && (RetryOn is null || RetryOn(error));
}
