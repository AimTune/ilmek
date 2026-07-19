// Node-level retry policy (MODEL.md §16).
//
// Retries in ilmek are safe where a pure-replay engine's are not: a retry
// re-runs the node body, but every `ctx.step` it already completed returns from
// the journal instead of re-executing. A node that charged a card and then hit a
// flaky API retries the API call without charging twice — the same guarantee
// interrupts rely on (§5), turned toward failure instead of a human.

/** How a node retries when it throws a non-interrupt error (MODEL.md §16). */
export interface RetryPolicy {
    /** Total attempts including the first. Must be ≥ 1. Default 1 (no retry). */
    readonly maxAttempts: number;
    /** Delay before the first retry, in ms. Default 0. */
    readonly backoffMs?: number;
    /** Multiplier applied to the delay after each attempt. Default 1 (constant). */
    readonly factor?: number;
    /** Cap on the computed delay, in ms. Default Infinity. */
    readonly maxBackoffMs?: number;
    /** Retry only errors this accepts. Default: retry every non-interrupt error. */
    readonly retryOn?: (error: unknown) => boolean;
}

/** Delay before attempt `n` (1-based: the first *retry* is n=2). */
export function backoffFor(policy: RetryPolicy, attempt: number): number {
    const base = policy.backoffMs ?? 0;
    if (base <= 0) return 0;
    const factor = policy.factor ?? 1;
    const delay = base * factor ** (attempt - 2); // attempt 2 → base, 3 → base*factor, …
    return Math.min(delay, policy.maxBackoffMs ?? Infinity);
}

export function shouldRetry(policy: RetryPolicy, attempt: number, error: unknown): boolean {
    if (attempt >= policy.maxAttempts) return false;
    return policy.retryOn ? policy.retryOn(error) : true;
}
