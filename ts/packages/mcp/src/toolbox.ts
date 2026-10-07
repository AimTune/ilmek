// One connected MCP server, as a graph uses it: its tools under a stable name
// prefix, and every call journaled through `ctx.step` so a pause/resume never
// re-invokes a remote tool.

import type { Context } from "@ilmek/core";

import {
    normalizeToolResult,
    promptText,
    type McpClientLike,
    type McpPromptInfo,
    type McpResourceInfo,
    type McpToolInfo,
    type McpToolResult,
} from "./client.ts";

export interface McpToolboxOptions {
    /**
     * The server's name — the journal-key namespace and the default tool prefix.
     * Two toolboxes with different names never collide, even when their servers
     * advertise the same tool.
     */
    name: string;
    /**
     * Prepended to every advertised tool name. Default `"<name>__"`, so a
     * server `github` with a tool `search` is exposed as `github__search` —
     * letters, digits, `_` and `-` only, which is what model tool-name rules
     * allow. Pass `""` to expose the raw names.
     */
    prefix?: string;
    /** Keep only these advertised tool names (before prefixing). Default: all. */
    allow?: readonly string[];
}

/** A tool as the toolbox exposes it: the prefixed name a model calls, plus the server's own name. */
export interface McpTool extends McpToolInfo {
    /** The server's own name for the tool (what `tools/call` receives). */
    readonly remoteName: string;
}

export interface CallOptions {
    /** Journal key; defaults to `mcp:<server>:<tool>`, so one node can call several tools. */
    key?: string;
}

/**
 * A connected MCP server's tools, resources and prompts, as a graph consumes them.
 *
 * ```ts
 * const github = await McpToolbox.connect(client, { name: "github" });
 * const result = await github.call(ctx, "github__search", { q: "ilmek" });   // journaled
 * ```
 *
 * `connect` lists the tools once; `tools()` is that snapshot (level 1 for a
 * model's tool list). `call` runs inside `ctx.step` — on the replay pass after
 * an interrupt the recorded result is returned and the server is **not** called
 * again. `invoke` is the raw, unjournaled call, for a host that journals on its
 * own (mekik's `mekik.tool` does).
 */
export class McpToolbox {
    readonly name: string;
    readonly prefix: string;
    private readonly client: McpClientLike;
    private readonly byName: ReadonlyMap<string, McpTool>;

    private constructor(client: McpClientLike, options: McpToolboxOptions, tools: readonly McpToolInfo[]) {
        this.client = client;
        this.name = options.name;
        this.prefix = options.prefix ?? `${options.name}__`;
        const allow = options.allow ? new Set(options.allow) : null;
        const map = new Map<string, McpTool>();
        // A server that answers tools/list without a list advertises nothing.
        for (const t of tools ?? []) {
            if (allow && !allow.has(t.name)) continue;
            const exposed = `${this.prefix}${t.name}`;
            const tool: { -readonly [K in keyof McpTool]: McpTool[K] } = { name: exposed, remoteName: t.name, inputSchema: t.inputSchema ?? { type: "object" } };
            if (t.description !== undefined) tool.description = t.description;
            map.set(exposed, tool);
        }
        this.byName = map;
    }

    /** List the server's tools once and wrap them. */
    static async connect(client: McpClientLike, options: McpToolboxOptions): Promise<McpToolbox> {
        if (!options.name) throw new Error("an MCP toolbox needs a server name");
        const { tools } = await client.listTools();
        return new McpToolbox(client, options, tools);
    }

    /** The exposed tools, in the server's order — ready for a model's tool list. */
    tools(): McpTool[] {
        return [...this.byName.values()];
    }

    /** One exposed tool by its prefixed name. */
    tool(name: string): McpTool | undefined {
        return this.byName.get(name);
    }

    /** Raw, **unjournaled** call by exposed name. Prefer {@link call} inside a node. */
    async invoke(name: string, args: Record<string, unknown> = {}): Promise<McpToolResult> {
        const tool = this.byName.get(name);
        if (!tool) throw new Error(`MCP toolbox ${JSON.stringify(this.name)} has no tool ${JSON.stringify(name)}`);
        return normalizeToolResult(await this.client.callTool({ name: tool.remoteName, arguments: args }));
    }

    /**
     * Call a tool **once** across any number of resumes: the invocation runs in
     * `ctx.step`, keyed `mcp:<server>:<tool>` unless `opts.key` says otherwise,
     * and the normalized result is what the journal keeps.
     */
    async call(ctx: Context<any>, name: string, args: Record<string, unknown> = {}, opts: CallOptions = {}): Promise<McpToolResult> {
        const tool = this.byName.get(name);
        if (!tool) throw new Error(`MCP toolbox ${JSON.stringify(this.name)} has no tool ${JSON.stringify(name)}`);
        return ctx.step(opts.key ?? `mcp:${this.name}:${tool.remoteName}`, () => this.invoke(name, args));
    }

    /** The server's resources, or `[]` when it advertises none. */
    async resources(): Promise<McpResourceInfo[]> {
        if (!this.client.listResources) return [];
        return [...(await this.client.listResources()).resources];
    }

    /** Read a resource's text **once** across resumes (`ctx.step`, key `mcp:<server>:resource:<uri>`). Non-text contents yield `""`. */
    readResource(ctx: Context<any>, uri: string, opts: CallOptions = {}): Promise<string> {
        return ctx.step(opts.key ?? `mcp:${this.name}:resource:${uri}`, () => this.fetchResource(uri));
    }

    /** Raw, unjournaled resource read. */
    async fetchResource(uri: string): Promise<string> {
        if (!this.client.readResource) throw new Error(`MCP server ${JSON.stringify(this.name)} has no resources`);
        const { contents } = await this.client.readResource({ uri });
        return (contents ?? []).map((c) => (typeof c?.text === "string" ? c.text : "")).filter((t) => t.length > 0).join("\n");
    }

    /** The server's prompts, or `[]` when it advertises none. */
    async prompts(): Promise<McpPromptInfo[]> {
        if (!this.client.listPrompts) return [];
        return [...(await this.client.listPrompts()).prompts];
    }

    /** Fetch a prompt's text **once** across resumes (`ctx.step`, key `mcp:<server>:prompt:<name>`). */
    prompt(ctx: Context<any>, name: string, args: Record<string, string> = {}, opts: CallOptions = {}): Promise<string> {
        return ctx.step(opts.key ?? `mcp:${this.name}:prompt:${name}`, () => this.fetchPrompt(name, args));
    }

    /** Raw, unjournaled prompt fetch, rendered to text (see `promptText`). */
    async fetchPrompt(name: string, args: Record<string, string> = {}): Promise<string> {
        if (!this.client.getPrompt) throw new Error(`MCP server ${JSON.stringify(this.name)} has no prompts`);
        return promptText(await this.client.getPrompt({ name, arguments: args }));
    }
}
