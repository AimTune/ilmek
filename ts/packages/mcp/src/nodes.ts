// MCP as graph data (MODEL.md §9): node types a stored spec can name. The spec
// carries the server and tool names — data — and the registry resolves them
// against connected toolboxes at build time.

import { GraphError, type NodeRegistry } from "@ilmek/core";

import type { McpToolbox } from "./toolbox.ts";

/** The default channel the MCP node types write to. */
export const DEFAULT_MCP_CHANNEL = "result";

/**
 * A node registry with two types, over toolboxes keyed by server name:
 *
 * - `mcp_tool` — config `{ server, tool, arguments?, argumentsFrom?, to?, text? }`.
 *   Calls `tool` (the **exposed** name) on `server` with `arguments` merged under
 *   the object found in channel `argumentsFrom` (when set), journaled, and writes
 *   the result to channel `to` (default `"result"`): the whole `McpToolResult`,
 *   or just its `text` when `text: true`. A tool the server did not advertise
 *   fails at build time, not mid-run.
 * - `mcp_resource` — config `{ server, uri, to? }`. Reads the resource's text,
 *   journaled, into channel `to`.
 *
 * ```json
 * { "id": "search", "type": "mcp_tool",
 *   "config": { "server": "github", "tool": "github__search", "argumentsFrom": "query", "to": "hits", "text": true } }
 * ```
 */
export function mcpNodes(toolboxes: Readonly<Record<string, McpToolbox>>): NodeRegistry {
    const toolbox = (config: Record<string, unknown>, type: string): McpToolbox => {
        const server = config.server;
        if (typeof server !== "string" || server.length === 0) throw new GraphError(`an "${type}" node needs config.server`);
        const tb = toolboxes[server];
        if (!tb) {
            throw new GraphError(`"${type}" node references MCP server ${JSON.stringify(server)}; known: ${JSON.stringify(Object.keys(toolboxes))}`);
        }
        return tb;
    };

    return {
        mcp_tool: (config) => {
            const tb = toolbox(config, "mcp_tool");
            const name = config.tool;
            if (typeof name !== "string" || !tb.tool(name)) {
                throw new GraphError(
                    `"mcp_tool" node references tool ${JSON.stringify(name)} on ${JSON.stringify(tb.name)}; ` +
                        `known: ${JSON.stringify(tb.tools().map((t) => t.name))}`,
                );
            }
            const to = channelOf(config);
            const fixed = isRecord(config.arguments) ? config.arguments : {};
            const from = typeof config.argumentsFrom === "string" ? config.argumentsFrom : undefined;
            const textOnly = config.text === true;
            return async (state, ctx) => {
                const dynamic = from !== undefined ? (state as Record<string, unknown>)[from] : undefined;
                const args = { ...fixed, ...(isRecord(dynamic) ? dynamic : {}) };
                const result = await tb.call(ctx, name, args);
                return { [to]: textOnly ? result.text : result };
            };
        },
        mcp_resource: (config) => {
            const tb = toolbox(config, "mcp_resource");
            const uri = config.uri;
            if (typeof uri !== "string" || uri.length === 0) throw new GraphError(`an "mcp_resource" node needs config.uri`);
            const to = channelOf(config);
            return async (_state, ctx) => ({ [to]: await tb.readResource(ctx, uri) });
        },
    };
}

function channelOf(config: Record<string, unknown>): string {
    const to = config.to;
    if (to === undefined) return DEFAULT_MCP_CHANNEL;
    if (typeof to !== "string" || to.length === 0) throw new GraphError(`config.to must be a channel name`);
    return to;
}

function isRecord(v: unknown): v is Record<string, unknown> {
    return typeof v === "object" && v !== null && !Array.isArray(v);
}
