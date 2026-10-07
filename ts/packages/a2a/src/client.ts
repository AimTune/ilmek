// The A2A transport port and the wire shapes `@ilmek/a2a` reads.
//
// A2A (Agent2Agent) is JSON-RPC 2.0 over HTTP plus an Agent Card. The port is
// two calls — fetch the card, post one JSON-RPC request — so a real HTTP client
// (`HttpA2aTransport`, on the global `fetch`) and a scripted fake (tests) look
// the same, and this package keeps zero dependencies.

/** The A2A protocol revision this client speaks. */
export const A2A_PROTOCOL_VERSION = "0.3.0";

/** The default Agent Card location, relative to an agent's origin. */
export const AGENT_CARD_PATH = "/.well-known/agent-card.json";

export interface A2aAgentSkill {
    readonly id: string;
    readonly name: string;
    readonly description: string;
    readonly tags?: readonly string[];
}

/** An Agent Card, the fields ilmek reads; the rest passes through. */
export interface A2aAgentCard {
    readonly name: string;
    readonly description?: string;
    readonly url: string;
    readonly version?: string;
    readonly protocolVersion?: string;
    readonly capabilities?: { readonly streaming?: boolean; readonly pushNotifications?: boolean };
    readonly skills?: readonly A2aAgentSkill[];
    readonly [key: string]: unknown;
}

export type A2aPart =
    | { readonly kind: "text"; readonly text: string }
    | { readonly kind: "data"; readonly data: Record<string, unknown> }
    | { readonly kind: "file"; readonly file: Record<string, unknown> };

export interface A2aMessage {
    readonly kind?: "message";
    readonly messageId: string;
    readonly role: "user" | "agent";
    readonly parts: readonly A2aPart[];
    readonly taskId?: string;
    readonly contextId?: string;
}

export interface A2aArtifact {
    readonly artifactId: string;
    readonly name?: string;
    readonly parts: readonly A2aPart[];
}

export type A2aTaskState =
    | "submitted"
    | "working"
    | "input-required"
    | "completed"
    | "canceled"
    | "failed"
    | "rejected"
    | "auth-required"
    | "unknown";

export interface A2aTask {
    readonly kind?: "task";
    readonly id: string;
    readonly contextId: string;
    readonly status: { readonly state: A2aTaskState; readonly message?: A2aMessage; readonly timestamp?: string };
    readonly artifacts?: readonly A2aArtifact[];
    readonly history?: readonly A2aMessage[];
    readonly metadata?: Record<string, unknown>;
}

export interface JsonRpcRequest {
    jsonrpc: "2.0";
    id: string | number;
    method: string;
    params?: unknown;
}

export interface JsonRpcResponse {
    jsonrpc: "2.0";
    id: string | number | null;
    result?: unknown;
    error?: { code: number; message: string; data?: unknown };
}

/** What a connected A2A agent must look like: its card, and a JSON-RPC round-trip. */
export interface A2aTransport {
    getAgentCard(): Promise<A2aAgentCard>;
    post(request: JsonRpcRequest): Promise<JsonRpcResponse>;
}

/** The remote agent answered with a JSON-RPC error. */
export class A2aError extends Error {
    readonly code: number;
    readonly data: unknown;
    constructor(code: number, message: string, data?: unknown) {
        super(message);
        this.name = "A2aError";
        this.code = code;
        this.data = data;
    }
}

/**
 * The HTTP transport: the card from `<origin>/.well-known/agent-card.json` (or
 * the given card URL), JSON-RPC posted to the card's `url` (or the given
 * endpoint). Uses the global `fetch`.
 */
export class HttpA2aTransport implements A2aTransport {
    private readonly cardUrl: string;
    private endpoint: string | undefined;
    private readonly headers: Record<string, string>;
    private readonly fetchImpl: typeof fetch;

    /**
     * @param agentUrl - The agent's origin (`https://bot.example.com`), its card URL, or its JSON-RPC endpoint.
     * @param options.headers - Extra headers on every request (e.g. `Authorization`).
     * @param options.fetch - A `fetch` implementation; default the global one.
     */
    constructor(agentUrl: string, options: { headers?: Record<string, string>; fetch?: typeof fetch } = {}) {
        const url = new URL(agentUrl);
        if (url.pathname.endsWith(".json")) {
            this.cardUrl = url.toString();
        } else {
            this.cardUrl = new URL(AGENT_CARD_PATH, url.origin).toString();
            if (url.pathname !== "/" && url.pathname !== "") this.endpoint = url.toString();
        }
        this.headers = options.headers ?? {};
        this.fetchImpl = options.fetch ?? fetch;
    }

    async getAgentCard(): Promise<A2aAgentCard> {
        const res = await this.fetchImpl(this.cardUrl, { headers: { Accept: "application/json", ...this.headers } });
        if (!res.ok) throw new Error(`agent card ${this.cardUrl}: HTTP ${res.status}`);
        const card = (await res.json()) as A2aAgentCard;
        if (typeof card.url === "string" && this.endpoint === undefined) this.endpoint = card.url;
        return card;
    }

    async post(request: JsonRpcRequest): Promise<JsonRpcResponse> {
        if (this.endpoint === undefined) await this.getAgentCard();
        if (this.endpoint === undefined) {
            throw new Error(`A2A ${request.method}: the agent card at ${this.cardUrl} has no url and no endpoint was given`);
        }
        const res = await this.fetchImpl(this.endpoint!, {
            method: "POST",
            headers: { "Content-Type": "application/json", Accept: "application/json", ...this.headers },
            body: JSON.stringify(request),
        });
        if (!res.ok) throw new Error(`A2A ${request.method}: HTTP ${res.status}`);
        return (await res.json()) as JsonRpcResponse;
    }
}

/** The text parts of a message or artifact, joined by newlines. */
export function textOf(parts: readonly A2aPart[] | undefined): string {
    return (parts ?? [])
        .filter((p): p is Extract<A2aPart, { kind: "text" }> => p.kind === "text" && typeof p.text === "string")
        .map((p) => p.text)
        .join("\n");
}

/** The first data part of a message, if any. */
export function dataOf(parts: readonly A2aPart[] | undefined): Record<string, unknown> | undefined {
    const part = (parts ?? []).find((p): p is Extract<A2aPart, { kind: "data" }> => p.kind === "data");
    return part?.data;
}
