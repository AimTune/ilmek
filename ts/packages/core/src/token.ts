// The token-streaming convention (MODEL.md §10.2).
//
// ilmek is LLM-agnostic — the core never calls a model. But "stream the answer
// as it is generated" is the one streaming need every agent has, so ilmek fixes
// a shape for it instead of leaving each caller to invent one. A token is just a
// text delta pushed through `ctx.emit`; giving it a stable envelope lets the
// `messages` stream mode (stream.ts) pick tokens out of the generic custom
// channel without guessing.
//
// Tokens ride the SAME transient side channel as `ctx.emit` — they are NOT
// journaled. On replay a node re-streams its tokens; only the value it commits
// through `ctx.step` is memoized. That is deliberate: a resumed run should show
// its work again, not silently skip to the answer.

/** One streamed text delta. `meta` carries model/node/tags a consumer may filter on. */
export interface TokenChunk {
    readonly type: "token";
    readonly text: string;
    readonly meta?: Record<string, unknown>;
}

/** Build a `TokenChunk`. Usually reached through `ctx.emitToken(text, meta)`. */
export function token(text: string, meta?: Record<string, unknown>): TokenChunk {
    return meta === undefined ? { type: "token", text } : { type: "token", text, meta };
}

/** True when an emitted payload is a `TokenChunk` — the `messages` mode's filter. */
export function isToken(value: unknown): value is TokenChunk {
    return (
        typeof value === "object" &&
        value !== null &&
        (value as { type?: unknown }).type === "token" &&
        typeof (value as { text?: unknown }).text === "string"
    );
}
