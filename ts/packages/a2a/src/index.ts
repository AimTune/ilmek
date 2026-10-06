/**
 * `@ilmek/a2a` — call Agent2Agent agents from a graph.
 *
 * ```ts
 * import { A2aAgent, HttpA2aTransport } from "@ilmek/a2a";
 *
 * const desk = await A2aAgent.connect(new HttpA2aTransport("https://bot.example.com"), { name: "desk" });
 * const r = await desk.send(ctx, "Where is ORD-42?");                       // journaled: once across resumes
 * if (r.needsInput) await desk.send(ctx, "Approve", { taskId: r.taskId, key: "answer" });
 * ```
 *
 * Zero dependencies: the transport port is two calls (the card, a JSON-RPC
 * post) and the HTTP one rides the global `fetch`. A remote agent's
 * human-in-the-loop pause arrives as an `input-required` task with the open
 * questions in `statusText` / `statusData`; the node decides whether to answer
 * itself, ask its own human (`ctx.interrupt`), or give up.
 */

export { A2A_PROTOCOL_VERSION, A2aError, AGENT_CARD_PATH, dataOf, HttpA2aTransport, textOf } from "./client.ts";
export type {
    A2aAgentCard,
    A2aAgentSkill,
    A2aArtifact,
    A2aMessage,
    A2aPart,
    A2aTask,
    A2aTaskState,
    A2aTransport,
    JsonRpcRequest,
    JsonRpcResponse,
} from "./client.ts";

export { A2aAgent, normalizeTask } from "./agent.ts";
export type { A2aResult, SendOptions } from "./agent.ts";

export { a2aNodes, DEFAULT_A2A_CHANNEL } from "./nodes.ts";
