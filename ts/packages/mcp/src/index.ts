/**
 * `@ilmek/mcp` — Model Context Protocol servers as ilmek tools, resources and
 * skills.
 *
 * ```ts
 * import { Client } from "@modelcontextprotocol/sdk/client/index.js";
 * import { McpToolbox, mcpSkills } from "@ilmek/mcp";
 *
 * const github = await McpToolbox.connect(client, { name: "github" });   // any SDK Client fits
 * github.tools();                                                       // [{ name: "github__search", inputSchema, … }]
 * const hits = await github.call(ctx, "github__search", { q: "ilmek" }); // journaled: once across resumes
 * const skills = await mcpSkills(github);                               // its prompts, as skills
 * ```
 *
 * Zero dependencies: the client port is duck-typed to the official SDK's
 * `Client`. ilmek stays LLM-agnostic — this package never calls a model; a host
 * (mekik, your own agent loop) hands `toolbox.tools()` to one and dispatches its
 * calls to `toolbox.call`.
 */

export { normalizeToolResult, promptText } from "./client.ts";
export type {
    McpCallToolResult,
    McpClientLike,
    McpContent,
    McpGetPromptResult,
    McpPromptArgument,
    McpPromptInfo,
    McpPromptMessage,
    McpResourceContents,
    McpResourceInfo,
    McpToolInfo,
    McpToolResult,
} from "./client.ts";

export { McpToolbox } from "./toolbox.ts";
export type { CallOptions, McpTool, McpToolboxOptions } from "./toolbox.ts";

export { mcpSkills, toSkillName } from "./skills.ts";
export type { McpSkill, McpSkillsOptions } from "./skills.ts";

export { DEFAULT_MCP_CHANNEL, mcpNodes } from "./nodes.ts";
