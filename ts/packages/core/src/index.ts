/**
 * ilmek — an agent graph runtime: state, nodes, edges, checkpointed memory, and
 * durable human-in-the-loop.
 *
 * See MODEL.md — it is the normative spec and this module is its TypeScript
 * surface. Ilmek is not a server: it knows nothing about transports, chat
 * protocols or HTTP. Botiva adapts it behind its `Runtime` port, and it works
 * just as well for batch jobs and background workflows.
 *
 * ## The one idea worth knowing
 *
 * A node that pauses for a human is re-executed from the top when the answer
 * arrives. In a pure-replay engine that means every side effect before the pause
 * happens **twice**, and avoiding it is the author's problem. Here it is the
 * engine's: wrap each effect in `ctx.step()` and the journal replays its
 * recorded result instead of calling it again.
 *
 * ```ts
 * const g = graph("checkout")
 *     .channel("cart", channel.lastWrite<Item[]>([]))
 *     .channel("log", channel.append<string>())
 *     .node("checkout", async (state, ctx) => {
 *         // Called once, ever. On the resume pass this returns the journaled order.
 *         const order = await ctx.step("create_order", () => Orders.create(state.cart));
 *
 *         // First pass: the task halts here. Resume pass: returns the answer.
 *         const ok = await ctx.interrupt<string>({ question: `Charge ${order.total}?` });
 *
 *         await ctx.step("charge", () => Payments.charge(order, ok));
 *         return { log: ["done"] };
 *     })
 *     .edge(START, "checkout")
 *     .edge("checkout", END)
 *     .compile();
 *
 * const opts = { threadId: "conv-42", checkpointer: new InMemoryCheckpointer() };
 * const paused = await run(g, { cart }, opts);   // status: "interrupted"
 * const done = await resume(g, "yes", opts);     // status: "done"
 * ```
 */

import type { ChannelMap, StateOf } from "./channel.ts";
import { materialize } from "./channel.ts";
import type { Checkpointer, Pending } from "./checkpoint.ts";
import type { CompiledGraph } from "./graph.ts";
import { runStream, type RunOptions, type IlmekEvent } from "./engine.ts";

export { channel, lastWrite, append, merge, reduce, UNSET } from "./channel.ts";
export type { Channel, ChannelMap, Reducer, ReducerKind, StateOf, UpdateOf, Unset } from "./channel.ts";

export { InMemoryCheckpointer, generateCheckpointId, isInterrupted } from "./checkpoint.ts";
export type { Checkpoint, Checkpointer, Pending, ScheduledTask } from "./checkpoint.ts";

export type { Context, Logger } from "./context.ts";

export {
    GraphError,
    InterruptSignal,
    NondeterminismError,
    RecursionLimitError,
    ReducerError,
    ResumeError,
    isInterrupt,
} from "./errors.ts";

export { graph, GraphBuilder, START, END, targets, inTaskOrder } from "./graph.ts";
export type {
    CompiledGraph,
    GraphEdge,
    GraphNode,
    GuardFn,
    NodeFn,
    NodeFnIn,
    RouterFn,
    NodeOptions,
    EdgeOptions,
} from "./graph.ts";

export { Journal, TaskJournal } from "./journal.ts";
export type { JournalDump, JournalEntry, PendingEntry } from "./journal.ts";

export { AsyncQueue } from "./queue.ts";

export { send, command, isSend, isCommand } from "./control.ts";
export type { Send, Command, Goto } from "./control.ts";

export type { RetryPolicy } from "./retry.ts";

export { fromSpec, toSpec } from "./spec.ts";
export type { GraphSpec, NodeRegistry, SpecPredicate } from "./spec.ts";

export { project, projected } from "./stream.ts";
export type { StreamMode, StreamPart } from "./stream.ts";

export { token, isToken } from "./token.ts";
export type { TokenChunk } from "./token.ts";

export type { RunOptions, RunMode, IlmekEvent } from "./engine.ts";

import { projected, type StreamMode, type StreamPart } from "./stream.ts";

/** The settled outcome of `run()`, `resume()` or `resumeKeyed()`. */
export interface Result<C extends ChannelMap = ChannelMap> {
    readonly status: "done" | "interrupted" | "error" | "aborted";
    /** Final channel values. Only set when `status` is `"done"`. */
    readonly state: StateOf<C> | null;
    readonly threadId: string;
    readonly runId: string;
    /** Open interrupts. Only set when `status` is `"interrupted"`. */
    readonly pending: readonly Pending[];
    /** `[node, error]` pairs. Only set when `status` is `"error"`. */
    readonly errors: ReadonlyArray<readonly [string, unknown]>;
    /** Why the run was cancelled. Only set when `status` is `"aborted"`. */
    readonly abortReason: string | null;
    readonly events: readonly IlmekEvent[];
}

// ── running ─────────────────────────────────────────────────────────────────

/**
 * Stream a run's events lazily (MODEL.md §10).
 *
 * `input` is a partial state update, folded into the channels through their
 * reducers before the first superstep.
 */
export function stream<C extends ChannelMap>(
    g: CompiledGraph<C>,
    input: Record<string, unknown> = {},
    opts: RunOptions = {},
): AsyncGenerator<IlmekEvent> {
    return runStream(g, { kind: "input", update: input }, opts);
}

/** Run to settlement. See `stream()` for options. */
export async function run<C extends ChannelMap>(
    g: CompiledGraph<C>,
    input: Record<string, unknown> = {},
    opts: RunOptions = {},
): Promise<Result<C>> {
    return collect<C>(stream(g, input, opts));
}

/**
 * Stream a run as `StreamPart`s in the given modes (MODEL.md §10.1) — the
 * LangGraph-style `stream_mode` ergonomics over ilmek's one canonical stream.
 *
 *     for await (const { mode, data } of streamModes(g, input, ["updates", "messages"])) {
 *         if (mode === "messages") process.stdout.write((data as TokenChunk).text);
 *     }
 *
 * For the resume case, compose the primitive directly:
 * `projected(resumeStream(g, answer, opts), modes)`.
 */
export function streamModes<C extends ChannelMap>(
    g: CompiledGraph<C>,
    input: Record<string, unknown>,
    modes: readonly StreamMode[],
    opts: RunOptions = {},
): AsyncGenerator<StreamPart> {
    return projected(stream(g, input, opts), modes);
}

/**
 * Stream the continuation of a thread parked on exactly one interrupt
 * (MODEL.md §6). `answer` is the human's answer, of any type — an object answer
 * is never mistaken for a key map, because the pending count decides, not the
 * type. Use `resumeKeyed()` when several interrupts are pending.
 *
 * Requires `threadId` and `checkpointer`.
 */
export function resumeStream<C extends ChannelMap>(
    g: CompiledGraph<C>,
    answer: unknown,
    opts: RunOptions = {},
): AsyncGenerator<IlmekEvent> {
    return runStream(g, { kind: "resume_single", answer }, opts);
}

/** Resume to settlement. See `resumeStream()`. */
export async function resume<C extends ChannelMap>(
    g: CompiledGraph<C>,
    answer: unknown,
    opts: RunOptions = {},
): Promise<Result<C>> {
    return collect<C>(resumeStream(g, answer, opts));
}

/**
 * Stream the continuation of a thread, answering interrupts by key (MODEL.md §6).
 *
 * Works for any number of pending interrupts, so it is the form a UI that
 * renders every open pause should use.
 */
export function resumeKeyedStream<C extends ChannelMap>(
    g: CompiledGraph<C>,
    answers: Record<string, unknown> | ReadonlyMap<string, unknown>,
    opts: RunOptions = {},
): AsyncGenerator<IlmekEvent> {
    const map = answers instanceof Map ? answers : new Map(Object.entries(answers));
    return runStream(g, { kind: "resume", answers: map }, opts);
}

/** Resume to settlement, answering by key. See `resumeKeyedStream()`. */
export async function resumeKeyed<C extends ChannelMap>(
    g: CompiledGraph<C>,
    answers: Record<string, unknown> | ReadonlyMap<string, unknown>,
    opts: RunOptions = {},
): Promise<Result<C>> {
    return collect<C>(resumeKeyedStream(g, answers, opts));
}

/** The interrupts a thread is parked on, or `[]` if it is not parked. */
export async function pendingInterrupts(
    checkpointer: Checkpointer,
    threadId: string,
): Promise<readonly Pending[]> {
    return (await checkpointer.get(threadId))?.pending ?? [];
}

/** The current state of a thread, or `null` if it has no checkpoint. */
export async function threadState<C extends ChannelMap>(
    g: CompiledGraph<C>,
    checkpointer: Checkpointer,
    threadId: string,
): Promise<StateOf<C> | null> {
    const ckpt = await checkpointer.get(threadId);
    return ckpt ? materialize(g.channels, ckpt.channels) : null;
}

async function collect<C extends ChannelMap>(source: AsyncGenerator<IlmekEvent>): Promise<Result<C>> {
    const events: IlmekEvent[] = [];
    let status: Result<C>["status"] = "error";
    let state: StateOf<C> | null = null;
    let pending: readonly Pending[] = [];
    let errors: ReadonlyArray<readonly [string, unknown]> = [];
    let abortReason: string | null = null;
    let threadId = "";
    let runId = "";

    for await (const ev of source) {
        events.push(ev);
        threadId = ev.threadId;
        runId = ev.runId;

        if (ev.type !== "run_end") continue;

        status = ev.status;
        if (ev.status === "done") state = ev.state as StateOf<C>;
        else if (ev.status === "interrupted") pending = ev.pending;
        else if (ev.status === "aborted") abortReason = ev.reason;
        else errors = ev.errors;
    }

    return { status, state, threadId, runId, pending, errors, abortReason, events };
}
