// A2A as graph data (MODEL.md §9): a node type a stored spec can name. The
// spec carries the agent's name and which channels feed it — data — and the
// registry resolves the agent at build time.

import { GraphError, type NodeRegistry } from "@ilmek/core";

import type { A2aAgent } from "./agent.ts";

/** The default channel the `a2a_call` node writes to. */
export const DEFAULT_A2A_CHANNEL = "result";

/**
 * A node registry with one type, over agents keyed by name:
 *
 * - `a2a_call` — config `{ agent, text?, textFrom?, contextFrom?, taskFrom?, to?, textOnly? }`.
 *   Sends `text` (or the string in channel `textFrom`) to `agent`, continuing the
 *   remote conversation in channel `contextFrom` and answering the task in
 *   channel `taskFrom` when those are set, journaled; writes the result to
 *   channel `to` (default `"result"`): the whole `A2aResult`, or just its `text`
 *   when `textOnly: true`. An unknown agent fails at build time.
 *
 * ```json
 * { "id": "ask", "type": "a2a_call", "config": { "agent": "desk", "textFrom": "question", "to": "answer", "textOnly": true } }
 * ```
 */
export function a2aNodes(agents: Readonly<Record<string, A2aAgent>>): NodeRegistry {
    return {
        a2a_call: (config) => {
            const name = config.agent;
            if (typeof name !== "string" || name.length === 0) throw new GraphError(`an "a2a_call" node needs config.agent`);
            const agent = agents[name];
            if (!agent) throw new GraphError(`"a2a_call" node references A2A agent ${JSON.stringify(name)}; known: ${JSON.stringify(Object.keys(agents))}`);
            const fixedText = typeof config.text === "string" ? config.text : undefined;
            const textFrom = typeof config.textFrom === "string" ? config.textFrom : undefined;
            if (fixedText === undefined && textFrom === undefined) throw new GraphError(`an "a2a_call" node needs config.text or config.textFrom`);
            const contextFrom = typeof config.contextFrom === "string" ? config.contextFrom : undefined;
            const taskFrom = typeof config.taskFrom === "string" ? config.taskFrom : undefined;
            const to = channelOf(config);
            const textOnly = config.textOnly === true;
            return async (state, ctx) => {
                const s = state as Record<string, unknown>;
                const text = textFrom !== undefined ? String(s[textFrom] ?? "") : fixedText!;
                const contextId = contextFrom !== undefined && typeof s[contextFrom] === "string" ? (s[contextFrom] as string) : undefined;
                const taskId = taskFrom !== undefined && typeof s[taskFrom] === "string" ? (s[taskFrom] as string) : undefined;
                const result = await agent.send(ctx, text, {
                    ...(contextId !== undefined ? { contextId } : {}),
                    ...(taskId !== undefined ? { taskId } : {}),
                });
                return { [to]: textOnly ? result.text : result };
            };
        },
    };
}

function channelOf(config: Record<string, unknown>): string {
    const to = config.to;
    if (to === undefined) return DEFAULT_A2A_CHANNEL;
    if (typeof to !== "string" || to.length === 0) throw new GraphError(`config.to must be a channel name`);
    return to;
}
