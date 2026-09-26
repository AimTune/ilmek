// A fake MCP client scripted by conformance/mcp/server.json — the same file the
// .NET suite drives. It counts calls so a test can prove the journal ran a tool
// once across an interrupt/resume.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import type { McpCallToolResult, McpClientLike, McpGetPromptResult, McpPromptInfo, McpResourceContents, McpResourceInfo, McpToolInfo } from "../src/index.ts";

export const conformanceDir = join(dirname(fileURLToPath(import.meta.url)), "../../../../conformance/mcp");

export interface ScriptedServer {
    tools: McpToolInfo[];
    calls: Record<string, McpCallToolResult>;
    resources: McpResourceInfo[];
    resourceContents: Record<string, McpResourceContents[]>;
    prompts: McpPromptInfo[];
    promptResults: Record<string, McpGetPromptResult>;
}

export function loadScript(): ScriptedServer {
    return JSON.parse(readFileSync(join(conformanceDir, "server.json"), "utf8")) as ScriptedServer;
}

export class FakeMcpClient implements McpClientLike {
    readonly calls: Array<{ name: string; arguments: Record<string, unknown> | undefined }> = [];
    readonly script: ScriptedServer;
    constructor(script: ScriptedServer = loadScript()) {
        this.script = script;
    }
    async listTools() {
        return { tools: this.script.tools };
    }
    async callTool(params: { name: string; arguments?: Record<string, unknown> }) {
        this.calls.push({ name: params.name, arguments: params.arguments });
        const result = this.script.calls[params.name];
        if (!result) throw new Error(`unknown tool ${params.name}`);
        return result;
    }
    async listResources() {
        return { resources: this.script.resources };
    }
    async readResource(params: { uri: string }) {
        const contents = this.script.resourceContents[params.uri];
        if (!contents) throw new Error(`unknown resource ${params.uri}`);
        return { contents };
    }
    async listPrompts() {
        return { prompts: this.script.prompts };
    }
    async getPrompt(params: { name: string; arguments?: Record<string, string> }) {
        const result = this.script.promptResults[params.name];
        if (!result) throw new Error(`unknown prompt ${params.name}`);
        return result;
    }
}
