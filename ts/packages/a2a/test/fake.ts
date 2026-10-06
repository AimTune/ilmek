// A fake A2A transport scripted by conformance/a2a/agent.json — the same file
// the .NET suite drives. It records every request so a test can prove the
// journal sent a message once across an interrupt/resume.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import type { A2aAgentCard, A2aTransport, JsonRpcRequest, JsonRpcResponse } from "../src/index.ts";

export const conformanceDir = join(dirname(fileURLToPath(import.meta.url)), "../../../../conformance/a2a");

export interface ScriptedAgent {
    card: A2aAgentCard;
    sends: Record<string, unknown>;
    errors: Record<string, { code: number; message: string }>;
}

export function loadScript(): ScriptedAgent {
    return JSON.parse(readFileSync(join(conformanceDir, "agent.json"), "utf8")) as ScriptedAgent;
}

export class FakeA2aTransport implements A2aTransport {
    readonly requests: JsonRpcRequest[] = [];
    readonly script: ScriptedAgent;
    cardFetches = 0;
    constructor(script: ScriptedAgent = loadScript()) {
        this.script = script;
    }
    async getAgentCard(): Promise<A2aAgentCard> {
        this.cardFetches++;
        return this.script.card;
    }
    async post(request: JsonRpcRequest): Promise<JsonRpcResponse> {
        this.requests.push(request);
        const params = request.params as { message?: { parts: Array<{ kind: string; text?: string }> }; id?: string };
        if (request.method === "message/send") {
            const text = params.message!.parts.filter((p) => p.kind === "text").map((p) => p.text).join("\n");
            const result = this.script.sends[text];
            if (result === undefined) return { jsonrpc: "2.0", id: request.id, error: { code: -32602, message: `unscripted message ${JSON.stringify(text)}` } };
            return { jsonrpc: "2.0", id: request.id, result };
        }
        if (request.method === "tasks/get" || request.method === "tasks/cancel") {
            const task = Object.values(this.script.sends).find((t) => (t as { id?: string }).id === params.id);
            if (!task) return { jsonrpc: "2.0", id: request.id, error: this.script.errors["unknown task"]! };
            return { jsonrpc: "2.0", id: request.id, result: request.method === "tasks/cancel" ? { ...(task as object), status: { state: "canceled" } } : task };
        }
        return { jsonrpc: "2.0", id: request.id, error: { code: -32601, message: `method not found: ${request.method}` } };
    }
}
