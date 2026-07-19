import type { ChannelMap, StateOf } from "./channel.ts";
import type { Checkpointer } from "./checkpoint.ts";
import { InterruptSignal, NondeterminismError, ResumeError } from "./errors.ts";
import type { JournalDump } from "./journal.ts";
import { TaskJournal } from "./journal.ts";
import type { CompiledGraph } from "./graph.ts";
import { token } from "./token.ts";

export interface Logger {
    debug?(...args: unknown[]): void;
    info?(...args: unknown[]): void;
    warn?(...args: unknown[]): void;
    error?(...args: unknown[]): void;
}

/**
 * The single handle passed to every node, step and router (MODEL.md §8).
 *
 * Explicit passing is normative because it ports to every language — nothing in
 * ilmek requires ambient storage. `ambient()` offers AsyncLocalStorage capture as
 * sugar over this same object, but it is never the only path.
 *
 * `state` is deliberately the **checkpoint** view, not a live one: MODEL.md §4
 * guarantees a task never observes a sibling's update within its superstep.
 */
export interface Context<C extends ChannelMap = ChannelMap> {
    /** The compiled graph — nodes, edges, channels. Read-only introspection. */
    readonly graph: CompiledGraph<C>;
    /** Channel values as of the last checkpoint (this task's view). */
    readonly state: StateOf<C>;
    /** Botiva maps `conversationId` here. */
    readonly threadId: string;
    /** Changes across an interrupt/resume boundary. */
    readonly runId: string;
    readonly node: string;
    readonly taskId: string;
    readonly stepIndex: number;
    /** The run's superstep budget (MODEL.md §4). */
    readonly recursionLimit: number;
    /**
     * Supersteps left before `RecursionLimitError` — `recursionLimit - stepIndex`,
     * floored at 0. A node can read this to wind down gracefully (stop looping,
     * return a partial answer) instead of being killed at the limit.
     */
    readonly remainingSteps: number;
    /** Free-form map from the caller (botiva puts its `TurnContext` here). */
    readonly meta: Record<string, unknown>;
    /** This task's journal. Read-only; for debugging. */
    readonly journal: JournalDump;
    readonly log: Logger;
    /**
     * The run's `AbortSignal` (MODEL.md §10.3), or `undefined` if none was
     * passed. Forward it to any long await inside the node — a fetch, an LLM
     * call — so cancellation reaches the work that is actually blocking.
     */
    readonly signal: AbortSignal | undefined;

    /**
     * Run `fn` once and journal its result (MODEL.md §5).
     *
     * On a replay pass — after an interrupt, or after a crashed superstep — the
     * recorded value is returned and `fn` is **not called**. This is what makes
     * "resume from the line" true: not a restored call stack, but an effect that
     * cannot happen twice.
     *
     * Keys are looked up by name, so a node may branch and skip steps. Repeats
     * of the same base key are suffixed by occurrence (`charge#0`, `charge#1`),
     * which makes order matter for that key — so loops should carry a stable key
     * derived from the data: `ctx.step(\`charge:${item.id}\`, ...)`.
     *
     * A journaled value must survive a serializer round-trip: returning a socket
     * or stream handle breaks replay. Return an id and re-resolve it.
     */
    step<T>(key: string, fn: () => T | Promise<T>): Promise<T>;

    /**
     * Pause for a human and return their answer (MODEL.md §6).
     *
     * An interrupt is a step whose value comes from a person instead of a
     * function — same journal, same replay rules. So multiple pauses per node,
     * pauses inside loops, and concurrent pauses all work without special cases.
     *
     * The first pass never returns: it throws `InterruptSignal` to halt the
     * task, and the engine emits an `interrupt` event and ends the run. The
     * answer arrives via `resume()`.
     */
    interrupt<T = unknown>(payload?: unknown, key?: string): Promise<T>;

    /** Push a custom event into the run's stream (MODEL.md §10). Delivered live, mid-superstep. */
    emit(payload: unknown): void;

    /**
     * Stream one text delta (MODEL.md §10.2). Sugar for `emit(token(text, meta))`:
     * it rides the same transient channel as `emit`, so the `messages` stream
     * mode can pick it out, and — like everything on that channel — it is NOT
     * journaled. On replay the node re-streams its tokens; only `step` values are
     * memoized.
     */
    emitToken(text: string, meta?: Record<string, unknown>): void;
}

/**
 * Stop the debugger on every HITL pause, and nothing else.
 *
 * The alternative — VSCode's "Caught Exceptions" — also stops on every other
 * caught throw in the process. This is the inverse knob: silent unless a
 * debugger is attached AND the env var is set, so it costs nothing in
 * production and is switched from launch.json rather than the Breakpoints pane.
 *
 * Read once at load: a per-pause `process.env` lookup would be a needless read
 * on a hot path, and nothing legitimately flips this mid-run.
 */
const BREAK_ON_INTERRUPT = process.env.ILMEK_DEBUG_BREAK_ON_INTERRUPT === "1";

export interface ContextInit<C extends ChannelMap> {
    graph: CompiledGraph<C>;
    state: StateOf<C>;
    threadId: string;
    runId: string;
    node: string;
    taskId: string;
    stepIndex: number;
    recursionLimit: number;
    meta: Record<string, unknown>;
    checkpointer: Checkpointer | null;
    taskJournal: TaskJournal;
    emit: (payload: unknown) => void;
    signal: AbortSignal | undefined;
    log: Logger;
}

export function createContext<C extends ChannelMap>(init: ContextInit<C>): Context<C> {
    const { taskJournal: tj, checkpointer, taskId } = init;

    // Persist per step: a crash between two steps must not re-run the first.
    const persist = async (): Promise<void> => {
        if (checkpointer) await checkpointer.putJournal(taskId, tj.journal);
    };

    return {
        graph: init.graph,
        state: init.state,
        threadId: init.threadId,
        runId: init.runId,
        node: init.node,
        taskId,
        stepIndex: init.stepIndex,
        recursionLimit: init.recursionLimit,
        remainingSteps: Math.max(0, init.recursionLimit - init.stepIndex),
        meta: init.meta,
        log: init.log,
        signal: init.signal,
        emit: init.emit,
        emitToken: (text, meta) => init.emit(token(text, meta)),

        get journal(): JournalDump {
            return tj.journal.dump();
        },

        async step<T>(key: string, fn: () => T | Promise<T>): Promise<T> {
            const full = tj.resolveKey(key);
            const entry = tj.journal.fetch(full);

            if (entry?.status === "done") return entry.value as T;
            if (entry?.status === "pending") {
                throw new NondeterminismError(
                    `step ${JSON.stringify(full)} collides with a pending interrupt of the same ` +
                        `key. Give the step a distinct key.`,
                );
            }

            const value = await fn();
            tj.journal.putDone(full, value);
            await persist();
            return value;
        },

        async interrupt<T = unknown>(payload: unknown = {}, key = "interrupt"): Promise<T> {
            const full = tj.resolveKey(key);
            const entry = tj.journal.fetch(full);

            if (entry?.status === "done") return entry.value as T;
            if (entry?.status === "pending") throw new InterruptSignal(full, payload);

            if (!checkpointer) {
                throw new ResumeError(
                    `ctx.interrupt() needs a checkpointer — the pause is stored in the thread's ` +
                        `journal and there is nowhere to put it. Run with ` +
                        `{ checkpointer: new InMemoryCheckpointer() }.`,
                );
            }

            tj.journal.putPending(full, payload);
            await persist();

            if (BREAK_ON_INTERRUPT) {
                // Paused because ILMEK_DEBUG_BREAK_ON_INTERRUPT=1. `full` is the
                // journal key, `payload` the question going to the human. Step
                // out to see which node asked; the answer arrives in a later run
                // via resume(), not here.
                // eslint-disable-next-line no-debugger
                debugger;
            }

            throw new InterruptSignal(full, payload);
        },
    };
}
