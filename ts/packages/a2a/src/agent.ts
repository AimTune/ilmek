// One remote A2A agent, as a graph uses it: send a message and get back a
// normalized task, every round-trip journaled through `ctx.step` so a
// pause/resume never re-sends a message the remote agent already acted on.

import type { Context } from "@ilmek/core";

import {
    A2aError,
    dataOf,
    textOf,
    type A2aAgentCard,
    type A2aMessage,
    type A2aPart,
    type A2aTask,
    type A2aTaskState,
    type A2aTransport,
    type JsonRpcResponse,
} from "./client.ts";

/** A task as a node wants it. Plain data — it goes in the journal. */
export interface A2aResult {
    readonly taskId: string;
    /** The remote agent's conversation id; pass it back to continue. */
    readonly contextId: string;
    readonly state: A2aTaskState;
    /** The text of the task's artifacts (the reply), joined; `""` when there is none. */
    readonly text: string;
    /** The text of the agent's status message — what it needs, or why it stopped. */
    readonly statusText: string;
    /** The first data part of the status message (a mekik agent puts its open interrupts here as `pending`). */
    readonly statusData?: Record<string, unknown>;
    /** `state === "input-required"`: the caller must send another message on `taskId`. */
    readonly needsInput: boolean;
    /** The task, verbatim. */
    readonly task: A2aTask;
}

export interface SendOptions {
    /** Continue the remote conversation this id names. */
    contextId?: string;
    /** Answer an `input-required` task: the message goes to this task. */
    taskId?: string;
    /** A data part sent alongside the text (e.g. `{ answers: {…} }` for a mekik agent with several pauses). */
    data?: Record<string, unknown>;
    /** Journal key; defaults to `a2a:<agent>:send`, so one node can talk to several agents. */
    key?: string;
    /** The message id; minted when absent. */
    messageId?: string;
}

/**
 * A remote A2A agent.
 *
 * ```ts
 * const desk = await A2aAgent.connect(new HttpA2aTransport("https://bot.example.com"), { name: "desk" });
 * const r = await desk.send(ctx, "Where is ORD-42?");                  // journaled
 * if (r.needsInput) await desk.send(ctx, "Approve", { taskId: r.taskId, key: "answer" });
 * ```
 *
 * `connect` fetches the Agent Card once; `card` is that snapshot. `send` runs
 * inside `ctx.step` — on the replay pass after an interrupt the recorded task
 * comes back and the remote agent is **not** messaged again. `invoke` is the
 * raw, unjournaled call, for a host that journals on its own.
 */
export class A2aAgent {
    readonly name: string;
    readonly card: A2aAgentCard;
    private readonly transport: A2aTransport;
    private readonly mint: () => string;
    private rpcSeq = 0;

    private constructor(transport: A2aTransport, name: string, card: A2aAgentCard, mint: () => string) {
        this.transport = transport;
        this.name = name;
        this.card = card;
        this.mint = mint;
    }

    /**
     * Fetch the card and wrap the agent. `name` is the journal-key namespace
     * (default: the card's name, slugified).
     */
    static async connect(transport: A2aTransport, options: { name?: string; mintId?: () => string } = {}): Promise<A2aAgent> {
        const card = await transport.getAgentCard();
        const name = options.name ?? slug(card.name);
        if (!name) throw new Error("an A2A agent needs a name");
        let n = 0;
        const mint = options.mintId ?? (() => `msg-${name}-${++n}-${Date.now().toString(36)}`);
        return new A2aAgent(transport, name, card, mint);
    }

    /** Raw, **unjournaled** `message/send`. Prefer {@link send} inside a node. */
    async invoke(text: string, opts: Omit<SendOptions, "key"> = {}): Promise<A2aResult> {
        const parts: A2aPart[] = [];
        if (text.length > 0) parts.push({ kind: "text", text });
        if (opts.data !== undefined) parts.push({ kind: "data", data: opts.data });
        if (parts.length === 0) throw new Error("an A2A message needs text or data");
        const message: A2aMessage = {
            kind: "message",
            messageId: opts.messageId ?? this.mint(),
            role: "user",
            parts,
            ...(opts.taskId !== undefined ? { taskId: opts.taskId } : {}),
            ...(opts.contextId !== undefined ? { contextId: opts.contextId } : {}),
        };
        const task = await this.rpc<A2aTask | A2aMessage>("message/send", { message });
        return normalizeTask(asTask(task, this.name));
    }

    /**
     * Send a message **once** across any number of resumes: the round-trip runs
     * in `ctx.step`, keyed `a2a:<agent>:send` unless `opts.key` says otherwise.
     * A node that sends twice (ask, then answer the agent's pause) must give the
     * second send its own key.
     */
    send(ctx: Context<any>, text: string, opts: SendOptions = {}): Promise<A2aResult> {
        const { key, ...rest } = opts;
        return ctx.step(key ?? `a2a:${this.name}:send`, () => this.invoke(text, rest));
    }

    /** Raw `tasks/get`. */
    async getTask(taskId: string, historyLength?: number): Promise<A2aResult> {
        const task = await this.rpc<A2aTask>("tasks/get", { id: taskId, ...(historyLength !== undefined ? { historyLength } : {}) });
        return normalizeTask(task);
    }

    /** Raw `tasks/cancel`. */
    async cancelTask(taskId: string): Promise<A2aResult> {
        return normalizeTask(await this.rpc<A2aTask>("tasks/cancel", { id: taskId }));
    }

    private async rpc<T>(method: string, params: unknown): Promise<T> {
        const response: JsonRpcResponse = await this.transport.post({ jsonrpc: "2.0", id: ++this.rpcSeq, method, params });
        if (response?.error) throw new A2aError(response.error.code, response.error.message, response.error.data);
        if (response?.result === undefined || response.result === null || typeof response.result !== "object") {
            throw new A2aError(-32603, `A2A ${method}: malformed JSON-RPC response — no result object and no error`, response);
        }
        return response.result as T;
    }
}

/** `message/send` may return a bare Message for agents that skip tasks; wrap it as a completed task. */
function asTask(result: A2aTask | A2aMessage, agent: string): A2aTask {
    if ((result as A2aTask).kind === "task" || ("status" in result && "id" in result)) return result as A2aTask;
    const m = result as A2aMessage;
    if (typeof m.messageId !== "string" || !Array.isArray(m.parts)) {
        throw new A2aError(-32603, "A2A message/send: the result is neither a task nor a message", result);
    }
    return {
        kind: "task",
        id: m.taskId ?? `message:${m.messageId}`,
        contextId: m.contextId ?? agent,
        status: { state: "completed" },
        artifacts: [{ artifactId: m.messageId, parts: m.parts }],
    };
}

/** Reduce a task to {@link A2aResult}. Pure — both languages pin it through conformance/a2a. */
export function normalizeTask(task: A2aTask): A2aResult {
    if (typeof task?.status !== "object" || task.status === null) {
        throw new A2aError(-32603, `A2A task ${JSON.stringify(task?.id)} has no status`, task);
    }
    const statusMessage = task.status.message;
    const statusData = dataOf(statusMessage?.parts);
    const out: { -readonly [K in keyof A2aResult]: A2aResult[K] } = {
        taskId: task.id,
        contextId: task.contextId,
        state: task.status.state,
        text: (task.artifacts ?? []).map((a) => textOf(a.parts)).filter((t) => t.length > 0).join("\n"),
        statusText: textOf(statusMessage?.parts),
        needsInput: task.status.state === "input-required",
        task,
    };
    if (statusData !== undefined) out.statusData = statusData;
    return out;
}

function slug(raw: string | undefined): string {
    return (raw ?? "")
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, "-")
        .replace(/^-+|-+$/g, "")
        .slice(0, 64);
}
