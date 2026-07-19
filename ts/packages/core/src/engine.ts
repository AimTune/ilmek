import { randomBytes } from "node:crypto";
import { materialize, reduceChannel, UNSET, type ChannelMap, type StateOf, type UpdateOf } from "./channel.ts";
import {
    generateCheckpointId,
    type Checkpoint,
    type Checkpointer,
    type Pending,
    type ScheduledTask,
} from "./checkpoint.ts";
import { createContext, type Context, type Logger } from "./context.ts";
import { isCommand, isSend, type Goto, type Send } from "./control.ts";
import { GraphError, isInterrupt, RecursionLimitError, ResumeError } from "./errors.ts";
import { resolveTargets, START, targets, type CompiledGraph } from "./graph.ts";
import { Journal, TaskJournal } from "./journal.ts";
import { AsyncQueue } from "./queue.ts";
import { backoffFor, shouldRetry } from "./retry.ts";

// ── events (MODEL.md §10) ───────────────────────────────────────────────────

interface EventBase {
    readonly runId: string;
    readonly threadId: string;
    /**
     * Monotonic within a run, 1-based (MODEL.md §10). A consumer that drops a
     * connection reconnects and skips everything up to its last-seen `seq`, so a
     * streamed run survives a broken pipe without replaying from the top.
     */
    readonly seq: number;
    /**
     * Namespace path to the (sub)graph that emitted this event — `[]` at the
     * root (MODEL.md §10). Always `[]` today; reserved so subgraphs can tag their
     * events with a `["node:taskId", ...]` path without a breaking envelope
     * change. Consumers filtering by graph should key off this, not the run id.
     */
    readonly ns: readonly string[];
}

export type IlmekEvent =
    | (EventBase & { type: "run_start" })
    | (EventBase & { type: "step_start"; step: number; tasks: readonly string[] })
    | (EventBase & { type: "node_start"; node: string; taskId: string })
    | (EventBase & { type: "custom"; payload: unknown })
    | (EventBase & { type: "node_end"; node: string; update: Record<string, unknown> })
    | (EventBase & { type: "node_error"; node: string; error: unknown })
    | (EventBase & { type: "node_retry"; node: string; attempt: number; error: unknown })
    | (EventBase & { type: "state"; channels: Record<string, unknown> })
    | (EventBase & { type: "checkpoint"; id: string })
    | (EventBase & { type: "interrupt"; pending: readonly Pending[] })
    | (EventBase & { type: "run_end"; status: "done"; state: Record<string, unknown> })
    | (EventBase & { type: "run_end"; status: "interrupted"; pending: readonly Pending[] })
    | (EventBase & { type: "run_end"; status: "error"; errors: ReadonlyArray<readonly [string, unknown]> })
    | (EventBase & { type: "run_end"; status: "aborted"; reason: string });

/**
 * `Omit` collapses a union rather than distributing over it, which would fuse
 * the four `run_end` variants into one shape that has none of their fields.
 * The naked `T` conditional restores distribution.
 */
type EventFields<E extends IlmekEvent["type"]> = Extract<IlmekEvent, { type: E }> extends infer V
    ? V extends IlmekEvent
        ? Omit<V, "type" | "runId" | "threadId" | "seq" | "ns">
        : never
    : never;

export interface RunOptions {
    threadId?: string;
    checkpointer?: Checkpointer;
    /** Resume from a specific checkpoint instead of the latest — forks a branch (MODEL.md §7). */
    checkpointId?: string;
    /** Superstep budget. Default 25. */
    recursionLimit?: number;
    /** Detect replay divergence (MODEL.md §5.5). Default true. */
    strict?: boolean;
    meta?: Record<string, unknown>;
    log?: Logger;
    /**
     * Cooperative cancellation (MODEL.md §10.3). Checked at every superstep
     * boundary — an aborted run stops there and ends with `status: "aborted"`.
     * The same signal reaches node code as `ctx.signal`, so a long await inside a
     * node (an LLM call, a fetch) can be cancelled mid-superstep by forwarding
     * it. The engine never force-kills a running task; cancellation is only as
     * responsive as the node's own signal handling.
     */
    signal?: AbortSignal;
}

function abortReason(signal: AbortSignal): string {
    const r = (signal as { reason?: unknown }).reason;
    if (r === undefined) return "aborted";
    if (r instanceof Error) return r.message;
    if (typeof r === "string") return r;
    try {
        return JSON.stringify(r) ?? "aborted";
    } catch {
        return "aborted";
    }
}

export type RunMode =
    | { kind: "input"; update: Record<string, unknown> }
    /** Keyed answers — works for any number of pending interrupts. */
    | { kind: "resume"; answers: ReadonlyMap<string, unknown> }
    /**
     * A bare answer. Legal only when exactly one interrupt is pending, which is
     * what makes it unambiguous: the answer's own type never decides anything,
     * so an object answer (`{approved: true}`) can't be mistaken for a key map.
     */
    | { kind: "resume_single"; answer: unknown };

type Outcome =
    // `goto` is set only when the node returned a command (MODEL.md §15); it
    // overrides the node's static edges when planning the next superstep.
    | { kind: "ok"; update: Record<string, unknown>; goto: Goto | undefined }
    | { kind: "interrupt"; key: string; payload: unknown }
    | { kind: "error"; error: unknown };

type Message =
    | { kind: "event"; payload: unknown }
    | { kind: "retry"; node: string; attempt: number; error: unknown }
    | { kind: "result"; task: ScheduledTask; taskId: string; outcome: Outcome };

interface TaskResult {
    readonly task: ScheduledTask;
    readonly taskId: string;
    /** Position in the dispatched superstep — the stable tie-break for same-node reduce order. */
    readonly index: number;
    readonly outcome: Outcome;
}

/**
 * Turn raw targets (node names + sends) into scheduled tasks (MODEL.md §4, §14).
 * Plain node names dedup — a node runs once per superstep however many
 * predecessors point at it — while every send becomes its own task, keyed
 * `node#n`, so N sends to one node never collide and never merge.
 */
function scheduleTargets(raw: ReadonlyArray<string | Send>): ScheduledTask[] {
    const seen = new Set<string>();
    const sendCounts = new Map<string, number>();
    const out: ScheduledTask[] = [];

    for (const target of raw) {
        if (isSend(target)) {
            const n = sendCounts.get(target.node) ?? 0;
            sendCounts.set(target.node, n + 1);
            out.push({ node: target.node, taskKey: `${target.node}#${n}`, isSend: true, input: target.input });
        } else if (!seen.has(target)) {
            seen.add(target);
            out.push({ node: target, taskKey: target, isSend: false });
        }
    }

    return out;
}

const sleep = (ms: number, signal?: AbortSignal): Promise<void> =>
    new Promise((resolve) => {
        const t = setTimeout(resolve, ms);
        signal?.addEventListener("abort", () => { clearTimeout(t); resolve(); }, { once: true });
    });

/**
 * The superstep loop (MODEL.md §4).
 *
 *     plan → run tasks concurrently → reduce → checkpoint → repeat
 *
 * Two invariants carry most of the design:
 *
 * - **Task isolation** — every task in a superstep sees the state as of the
 *   previous checkpoint, never a sibling's update.
 * - **Superstep atomicity** — if any task pauses on an interrupt, *no* update is
 *   reduced. All tasks of that superstep replay on resume, and the ones that had
 *   already finished fast-forward through their journals, so no completed step
 *   runs twice.
 */
export async function* runStream<C extends ChannelMap>(
    g: CompiledGraph<C>,
    mode: RunMode,
    opts: RunOptions = {},
): AsyncGenerator<IlmekEvent> {
    const checkpointer = opts.checkpointer ?? null;
    const threadId = opts.threadId ?? genId("thread");
    const runId = genId("run");
    const recursionLimit = opts.recursionLimit ?? 25;
    const strict = opts.strict ?? true;
    const meta = opts.meta ?? {};
    const log = opts.log ?? {};

    // Every event carries a monotonic per-run seq (for reconnect) and an ns path
    // (root is []; reserved for subgraphs). Assigned here so no call site can
    // forget them or hand out a duplicate seq.
    let seq = 0;
    const ns: readonly string[] = [];
    const ev = <E extends IlmekEvent["type"]>(type: E, fields: EventFields<E>): IlmekEvent =>
        ({ type, runId, threadId, seq: ++seq, ns, ...(fields as object) }) as IlmekEvent;

    const planContext = (state: StateOf<C>, step: number): Context<C> =>
        createPlanContext(g, state, { threadId, runId, stepIndex: step, recursionLimit, meta, log });

    // The first superstep of a fresh thread: whatever START routes to.
    const planStart = (channels: Record<string, unknown>, step: number): ScheduledTask[] => {
        const state = materialize(g.channels, channels);
        return scheduleTargets(targets(g, START, state, planContext(state, step)));
    };

    // The next superstep: each ran task's `goto` (MODEL.md §15) overrides its
    // static edges; everything else falls through to `targets` (edges + routers).
    const nextTasksAfter = (
        results: ReadonlyArray<TaskResult>,
        channels: Record<string, unknown>,
        step: number,
    ): ScheduledTask[] => {
        const state = materialize(g.channels, channels);
        const ctx = planContext(state, step);
        const raw: Array<string | Send> = [];
        for (const r of results) {
            if (r.outcome.kind !== "ok") continue;
            const goto = r.outcome.goto;
            raw.push(
                ...(goto !== undefined
                    ? resolveTargets(g, r.task.node, ([] as Array<string | Send>).concat(goto))
                    : targets(g, r.task.node, state, ctx)),
            );
        }
        return scheduleTargets(raw);
    };

    const latest = checkpointer ? await checkpointer.get(threadId, opts.checkpointId ?? null) : null;

    let channels: Record<string, unknown>;
    let next: ScheduledTask[];
    let step: number;
    let parentId: string | null;
    let planId: string | null;

    if (mode.kind === "input") {
        // Fresh input onto a thread parked on a human answer: refuse rather than
        // silently discarding the pause.
        if (latest && latest.pending.length > 0) {
            throw new ResumeError(
                `thread ${JSON.stringify(threadId)} is waiting on interrupt(s) ` +
                    `${JSON.stringify(latest.pending.map((p) => p.key))}. Answer them with resume() — ` +
                    `a plain run() would drop the pause.`,
            );
        }

        channels = foldUpdate(g, latest?.channels ?? {}, mode.update);
        parentId = latest?.id ?? null;
        planId = latest?.id ?? null;
        step = latest?.step ?? 0;
        // A thread resumed with plain input continues from its checkpoint's
        // plan; a fresh thread enters at START.
        next = latest && latest.next.length > 0 ? [...latest.next] : planStart(channels, step);
    } else {
        if (!latest) {
            throw new ResumeError(
                `cannot resume thread ${JSON.stringify(threadId)} — it has no checkpoint. ` +
                    `Resume requires a checkpointer and a prior interrupted run.`,
            );
        }
        if (latest.pending.length === 0) {
            throw new ResumeError(
                `cannot resume thread ${JSON.stringify(threadId)} — it is not interrupted. ` +
                    `Use run() to send new input.`,
            );
        }

        const answers =
            mode.kind === "resume_single" ? singleAnswer(latest.pending, mode.answer) : mode.answers;

        await answerPending(checkpointer!, latest.pending, answers);

        channels = { ...latest.channels };
        next = [...latest.next];
        step = latest.step;
        parentId = latest.id;
        // NOT latest.id — the tasks were planned from latest.planId, and their
        // journals are keyed by it. Losing this re-runs every completed step.
        planId = latest.planId;
    }

    const writeCheckpoint = async (fields: {
        channels: Record<string, unknown>;
        next: readonly ScheduledTask[];
        pending: readonly Pending[];
        step: number;
    }): Promise<Checkpoint> => {
        const ckpt: Checkpoint = {
            id: generateCheckpointId(),
            parentId,
            planId,
            threadId,
            ts: Date.now(),
            ...fields,
        };
        if (checkpointer) await checkpointer.put(ckpt);
        return ckpt;
    };

    yield ev("run_start", {});

    for (;;) {
        // Cooperative cancellation at the superstep boundary (MODEL.md §10.3).
        // The last committed checkpoint stands, so an aborted run resumes cleanly
        // later with run()/resume() — abort stops the stream, it does not roll back.
        if (opts.signal?.aborted) {
            yield ev("run_end", { status: "aborted", reason: abortReason(opts.signal) });
            return;
        }

        if (next.length === 0) {
            yield ev("run_end", { status: "done", state: materialize(g.channels, channels) });
            return;
        }

        if (step >= recursionLimit) {
            throw new RecursionLimitError(
                `run ${runId} exceeded ${recursionLimit} supersteps (still scheduling ` +
                    `${JSON.stringify(next)}). Raise recursionLimit or check for a cycle.`,
            );
        }

        // ── dispatch ──────────────────────────────────────────────────────
        const taskIdOf = (task: ScheduledTask): string =>
            `${threadId}:${planId ?? "root"}:${task.taskKey}`;

        yield ev("step_start", { step, tasks: next.map((t) => t.taskKey) });
        for (const task of next) yield ev("node_start", { node: task.node, taskId: taskIdOf(task) });

        const channelState = materialize(g.channels, channels);
        const queue = new AsyncQueue<Message>();
        const taskIndex = new Map<string, number>();
        const journals = await Promise.all(
            next.map((task) => {
                const id = taskIdOf(task);
                taskIndex.set(id, taskIndex.size);
                return checkpointer ? checkpointer.getJournal(id) : Promise.resolve(new Journal());
            }),
        );

        next.forEach((task, i) => {
            const taskId = taskIdOf(task);
            // A send task's state IS its input payload (MODEL.md §14); a plain
            // task reads the shared channel state.
            const taskState = task.isSend ? (task.input as StateOf<C>) : channelState;
            void runTask(g, task.node, taskState, {
                taskId,
                threadId,
                runId,
                stepIndex: step,
                recursionLimit,
                meta,
                log,
                checkpointer,
                strict,
                signal: opts.signal,
                journal: journals[i]!,
                emit: (payload) => queue.push({ kind: "event", payload }),
                onRetry: (attempt, error) => queue.push({ kind: "retry", node: task.node, attempt, error }),
            }).then((outcome) => queue.push({ kind: "result", task, taskId, outcome }));
        });

        // ── await ─────────────────────────────────────────────────────────
        // One message at a time, so a node's emit() reaches the consumer while
        // its siblings are still running rather than being buffered to the end.
        const results: TaskResult[] = [];
        let remaining = next.length;

        while (remaining > 0) {
            const msg = await queue.take();

            if (msg.kind === "event") {
                yield ev("custom", { payload: msg.payload });
                continue;
            }
            if (msg.kind === "retry") {
                yield ev("node_retry", { node: msg.node, attempt: msg.attempt, error: msg.error });
                continue;
            }

            remaining--;
            results.push({ ...msg, index: taskIndex.get(msg.taskId) ?? results.length });

            if (msg.outcome.kind === "ok") {
                yield ev("node_end", { node: msg.task.node, update: msg.outcome.update });
            } else if (msg.outcome.kind === "error") {
                yield ev("node_error", { node: msg.task.node, error: msg.outcome.error });
            }
            // A paused task has not ended — the run's `interrupt` event covers it.
        }

        // ── settle ────────────────────────────────────────────────────────
        const errors = results
            .filter((r) => r.outcome.kind === "error")
            .map((r) => [r.task.node, (r.outcome as { error: unknown }).error] as const);

        if (errors.length > 0) {
            yield ev("run_end", { status: "error", errors });
            return;
        }

        const pending: Pending[] = results
            .filter((r) => r.outcome.kind === "interrupt")
            .map((r) => {
                const o = r.outcome as { key: string; payload: unknown };
                // `id` is thread-scoped, `key` is task-scoped. It keys off taskKey
                // (not node), so two sends to one node — both journaling
                // `interrupt#0` — still get distinct ids (`worker#0:…`, `worker#1:…`).
                return {
                    id: `${r.task.taskKey}:${o.key}`,
                    taskId: r.taskId,
                    node: r.task.node,
                    key: o.key,
                    payload: o.payload,
                };
            });

        if (pending.length > 0) {
            // Superstep atomicity: nothing is reduced. Every task of this
            // superstep is rescheduled (inputs and keys intact), journals intact,
            // so the finished ones replay for free.
            const ckpt = await writeCheckpoint({
                channels,
                next: results.map((r) => r.task),
                pending,
                step,
            });

            yield ev("checkpoint", { id: ckpt.id });
            yield ev("interrupt", { pending });
            yield ev("run_end", { status: "interrupted", pending });
            return;
        }

        // ── advance ───────────────────────────────────────────────────────
        const reduced = reduceUpdates(g, channels, results);

        if (!reduced.ok) {
            // A reduce failure (e.g. a write to an undeclared channel) is a node
            // bug like any other, so it settles the same way instead of escaping
            // as a raw exception to whoever is consuming the stream.
            for (const [node, error] of reduced.errors) yield ev("node_error", { node, error });
            yield ev("run_end", { status: "error", errors: reduced.errors });
            return;
        }

        channels = reduced.channels;
        next = nextTasksAfter(results, channels, step + 1);

        const ckpt = await writeCheckpoint({ channels, next, pending: [], step: step + 1 });

        // The superstep committed — these journals are spent (MODEL.md §5.6).
        if (checkpointer) await Promise.all(results.map((r) => checkpointer.dropJournal(r.taskId)));

        yield ev("state", { channels: materialize(g.channels, channels) });
        yield ev("checkpoint", { id: ckpt.id });

        step += 1;
        parentId = ckpt.id;
        planId = ckpt.id;
    }
}

// ── tasks ───────────────────────────────────────────────────────────────────

interface TaskInit {
    taskId: string;
    threadId: string;
    runId: string;
    stepIndex: number;
    recursionLimit: number;
    meta: Record<string, unknown>;
    log: Logger;
    checkpointer: Checkpointer | null;
    strict: boolean;
    signal: AbortSignal | undefined;
    journal: Journal;
    emit: (payload: unknown) => void;
    /** Announced before each retry attempt (MODEL.md §16); `attempt` is the upcoming one. */
    onRetry: (attempt: number, error: unknown) => void;
}

async function runTask<C extends ChannelMap>(
    g: CompiledGraph<C>,
    nodeId: string,
    state: StateOf<C>,
    init: TaskInit,
): Promise<Outcome> {
    const node = g.nodes.get(nodeId)!;
    const policy = node.retry;

    for (let attempt = 1; ; attempt++) {
        // A fresh TaskJournal per attempt resets the occurrence counters and the
        // strict-mode trace, but the underlying journal (init.journal) persists —
        // so a step completed on attempt 1 is already recorded and attempt 2
        // replays it instead of re-running it (MODEL.md §16). Safe retries.
        const tj = new TaskJournal(init.journal);
        const ctx = createContext<C>({
            graph: g,
            state,
            threadId: init.threadId,
            runId: init.runId,
            node: nodeId,
            taskId: init.taskId,
            stepIndex: init.stepIndex,
            recursionLimit: init.recursionLimit,
            meta: init.meta,
            checkpointer: init.checkpointer,
            taskJournal: tj,
            emit: init.emit,
            signal: init.signal,
            log: init.log,
        });

        try {
            const ret = await node.fn(state, ctx);

            // Strict mode compares this pass's requested keys against the journal
            // (MODEL.md §5.5). Only meaningful when the node ran to completion — a
            // task that threw proves nothing. Inside the try so a violation
            // surfaces as a node error rather than an unhandled rejection.
            if (init.strict) tj.checkDeterminism(nodeId);

            return normalizeReturn(ret, nodeId);
        } catch (error) {
            if (isInterrupt(error)) return { kind: "interrupt", key: error.key, payload: error.payload };
            if (!policy || init.signal?.aborted || !shouldRetry(policy, attempt, error)) {
                return { kind: "error", error };
            }
            init.onRetry(attempt + 1, error);
            const delay = backoffFor(policy, attempt + 1);
            if (delay > 0) await sleep(delay, init.signal);
            // An abort during the backoff ends the retry loop rather than
            // launching an attempt no one is waiting for.
            if (init.signal?.aborted) return { kind: "error", error };
        }
    }
}

/** A node returns a channel update, `void`, or a `command` carrying update + goto (MODEL.md §15). */
function normalizeReturn(ret: unknown, nodeId: string): Outcome {
    if (ret === undefined || ret === null) return { kind: "ok", update: {}, goto: undefined };
    if (isCommand(ret)) return { kind: "ok", update: ret.update ?? {}, goto: ret.goto };
    if (typeof ret !== "object") {
        return {
            kind: "error",
            error: new GraphError(
                `node ${JSON.stringify(nodeId)} returned ${typeof ret}; a node must return a channel ` +
                    `update object, void, or a command(...).`,
            ),
        };
    }
    return { kind: "ok", update: ret as Record<string, unknown>, goto: undefined };
}

// ── reduce ──────────────────────────────────────────────────────────────────

/**
 * Fold in graph declaration order, NOT completion order, so a non-commutative
 * reducer (lastWrite, append) gives the same result on every run (MODEL.md §2).
 * When several tasks share a node (fan-out sends, §14) their scheduled index
 * breaks the tie, so the fold stays deterministic regardless of which finished
 * first. Each fold is isolated so a bad update is attributed to the task that
 * wrote it, and a failure leaves the channels untouched (superstep atomicity).
 */
function reduceUpdates<C extends ChannelMap>(
    g: CompiledGraph<C>,
    channels: Record<string, unknown>,
    results: ReadonlyArray<TaskResult>,
):
    | { ok: true; channels: Record<string, unknown> }
    | { ok: false; errors: ReadonlyArray<readonly [string, unknown]> } {
    const rank = (node: string): number => {
        const i = g.nodeOrder.indexOf(node);
        return i === -1 ? g.nodeOrder.length : i;
    };
    const ordered = [...results].sort((a, b) => {
        const byNode = rank(a.task.node) - rank(b.task.node);
        return byNode !== 0 ? byNode : a.index - b.index;
    });

    let acc = channels;
    for (const r of ordered) {
        if (r.outcome.kind !== "ok") continue;
        try {
            acc = foldUpdate(g, acc, r.outcome.update);
        } catch (error) {
            return { ok: false, errors: [[r.task.node, error]] };
        }
    }

    return { ok: true, channels: acc };
}

function foldUpdate<C extends ChannelMap>(
    g: CompiledGraph<C>,
    channels: Record<string, unknown>,
    update: Record<string, unknown> | UpdateOf<C>,
): Record<string, unknown> {
    const out = { ...channels };

    for (const [key, value] of Object.entries(update)) {
        // `{ messages: undefined }` means "not written" under Partial semantics.
        if (value === undefined) continue;

        const ch = g.channels[key];
        if (!ch) {
            throw new GraphError(
                `write to undeclared channel ${JSON.stringify(key)}. ` +
                    `Declared: ${JSON.stringify(Object.keys(g.channels))}`,
            );
        }

        out[key] = reduceChannel(key, ch, key in out ? out[key] : UNSET, value);
    }

    return out;
}

// ── resume ──────────────────────────────────────────────────────────────────

/**
 * A bare answer only makes sense against a single pause. Rather than guessing
 * from the answer's type — which breaks the moment someone answers one interrupt
 * with an object — the count decides, and the error names the alternative.
 */
function singleAnswer(pending: readonly Pending[], answer: unknown): ReadonlyMap<string, unknown> {
    if (pending.length !== 1) {
        throw new ResumeError(
            `${pending.length} interrupts are pending, so a bare answer is ambiguous. Use ` +
                `resumeKeyed() with an object keyed by interrupt id: ` +
                `${JSON.stringify(Object.fromEntries(pending.map((p) => [p.id, "answer"])))}`,
        );
    }
    return new Map([[pending[0]!.id, answer]]);
}

/**
 * Write each human answer into the journal entry that is waiting for it.
 *
 * Answers arrive keyed by the thread-scoped `id`; the journal is addressed by
 * the task-scoped `key`. Conflating the two silently drops an answer whenever
 * two nodes pause in the same superstep.
 */
async function answerPending(
    checkpointer: Checkpointer,
    pending: readonly Pending[],
    answers: ReadonlyMap<string, unknown>,
): Promise<void> {
    for (const p of pending) {
        if (!answers.has(p.id)) {
            throw new ResumeError(
                `no answer supplied for pending interrupt ${JSON.stringify(p.id)} ` +
                    `(node ${JSON.stringify(p.node)}). Expected ids: ` +
                    `${JSON.stringify(pending.map((x) => x.id))}`,
            );
        }

        const journal = await checkpointer.getJournal(p.taskId);
        const result = journal.answer(p.key, answers.get(p.id));

        if (!result.ok) {
            throw new ResumeError(
                `cannot answer ${JSON.stringify(p.key)} on task ${JSON.stringify(p.taskId)}: ${result.reason}`,
            );
        }

        await checkpointer.putJournal(p.taskId, journal);
    }
}

// ── plan-time context ───────────────────────────────────────────────────────

function createPlanContext<C extends ChannelMap>(
    g: CompiledGraph<C>,
    state: StateOf<C>,
    init: {
        threadId: string;
        runId: string;
        stepIndex: number;
        recursionLimit: number;
        meta: Record<string, unknown>;
        log: Logger;
    },
): Context<C> {
    const unavailable = (what: string): never => {
        throw new GraphError(
            `ctx.${what}() is not available in a router or guard — they run at plan time, outside ` +
                `any task, and so have no journal (MODEL.md §3). Route on state; compute in nodes.`,
        );
    };

    return {
        graph: g,
        state,
        threadId: init.threadId,
        runId: init.runId,
        node: "",
        taskId: "",
        stepIndex: init.stepIndex,
        recursionLimit: init.recursionLimit,
        remainingSteps: Math.max(0, init.recursionLimit - init.stepIndex),
        meta: init.meta,
        journal: [],
        log: init.log,
        signal: undefined,
        emit: () => undefined,
        emitToken: () => undefined,
        step: () => unavailable("step"),
        interrupt: () => unavailable("interrupt"),
    };
}

function genId(prefix: string): string {
    return `${prefix}-${randomBytes(8).toString("base64url")}`;
}
