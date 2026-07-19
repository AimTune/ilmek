/** Raised when a graph is structurally invalid (MODEL.md §3). */
export class GraphError extends Error {
    override readonly name = "GraphError";
}

/** Raised when a reducer cannot fold an update into a channel (MODEL.md §2). */
export class ReducerError extends Error {
    override readonly name = "ReducerError";
}

/** Raised when a run exceeds its superstep budget (MODEL.md §4). */
export class RecursionLimitError extends Error {
    override readonly name = "RecursionLimitError";
}

/**
 * Raised by strict mode when a replay diverges from the journal (MODEL.md §5.5).
 *
 * This means a node body is not deterministic modulo steps: a side effect or a
 * nondeterministic read escaped a `ctx.step()`.
 */
export class NondeterminismError extends Error {
    override readonly name = "NondeterminismError";
}

/** Raised when a resume does not match the thread's pending interrupts (MODEL.md §6). */
export class ResumeError extends Error {
    override readonly name = "ResumeError";
}

/**
 * The control signal `ctx.interrupt()` throws to halt a task (MODEL.md §6).
 *
 * Deliberately NOT an `Error` subclass: a node that wraps its work in
 * `catch (e) { if (e instanceof Error) ... }` must not swallow a pause. A bare
 * `catch {}` still would — so if a node catches everything, rethrow what
 * `isInterrupt()` recognises.
 */
export class InterruptSignal {
    /**
     * A stable, enumerable brand — the debugger's filter handle.
     *
     * A pause is ordinary control flow, but to V8 it is still a `throw` that the
     * engine catches. So VSCode's **Caught Exceptions** breakpoint stops on
     * every single HITL pause, drowning the errors you actually turned it on
     * for. `instanceof` is no help there: the breakpoint condition is evaluated
     * in the paused frame, where ilmek's exports are not in scope.
     *
     * Hence a plain property. Set the Caught Exceptions condition to:
     *
     *     !error?.isIlmekInterrupt
     *
     * See `.vscode/launch.json` for the full recipe, including the opposite
     * knob — `ILMEK_DEBUG_BREAK_ON_INTERRUPT=1`, which stops *only* on pauses.
     */
    readonly isIlmekInterrupt = true;

    readonly key: string;
    readonly payload: unknown;

    constructor(key: string, payload: unknown) {
        this.key = key;
        this.payload = payload;
    }
}

/** True when `value` is the pause signal thrown by `ctx.interrupt()`. Rethrow it. */
export function isInterrupt(value: unknown): value is InterruptSignal {
    return value instanceof InterruptSignal;
}
