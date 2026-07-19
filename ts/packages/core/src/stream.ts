// Stream projection modes (MODEL.md §10.1).
//
// ilmek's native stream is a single sequence of typed events — the shape
// LangGraph itself converged on with its v2/v3 stream protocols. This module is
// the thin view layer over it: LangGraph asks callers to pick a `stream_mode` up
// front and hands back mode-shaped chunks; ilmek keeps the one canonical stream
// and lets you PROJECT it into those same shapes after the fact, so a reconnect
// buffer, a test, and a live run all filter through the identical function.
//
// A projection never adds information — every StreamPart carries the `seq` and
// `ns` of the event it came from, so a consumer can still reconnect or group by
// subgraph after projecting.

import { isToken } from "./token.ts";
import type { IlmekEvent } from "./engine.ts";

/**
 * The classic LangGraph stream shapes, over ilmek's event stream:
 *
 * - `values`   — the full channel state after each superstep (from `state` events).
 * - `updates`  — one `{ [node]: update }` per node that ran (from `node_end`).
 * - `custom`   — every payload pushed through `ctx.emit`.
 * - `messages` — just the token deltas (custom payloads that are `TokenChunk`s).
 * - `debug`    — every event, unchanged, rewrapped as a part.
 */
export type StreamMode = "values" | "updates" | "custom" | "messages" | "debug";

/** One projected chunk. `data` shape depends on `mode`; `seq`/`ns` echo the source event. */
export interface StreamPart {
    readonly mode: StreamMode;
    readonly seq: number;
    readonly ns: readonly string[];
    readonly data: unknown;
}

/**
 * Project one event into zero or more parts, one per requested mode it matches.
 * Pure — the unit the streaming helpers are built from, and usable on its own to
 * re-project a buffered event (e.g. on reconnect).
 */
export function project(event: IlmekEvent, modes: readonly StreamMode[]): StreamPart[] {
    const parts: StreamPart[] = [];
    const emit = (mode: StreamMode, data: unknown): void => {
        parts.push({ mode, seq: event.seq, ns: event.ns, data });
    };

    for (const mode of modes) {
        switch (mode) {
            case "values":
                // The final superstep emits a `state` event before run_end, so
                // this covers the terminal state too — no need to also read
                // run_end. The one gap vs LangGraph: no initial-input snapshot.
                if (event.type === "state") emit("values", event.channels);
                break;
            case "updates":
                if (event.type === "node_end") emit("updates", { [event.node]: event.update });
                break;
            case "custom":
                if (event.type === "custom") emit("custom", event.payload);
                break;
            case "messages":
                if (event.type === "custom" && isToken(event.payload)) emit("messages", event.payload);
                break;
            case "debug":
                emit("debug", event);
                break;
        }
    }

    return parts;
}

/**
 * Wrap any `IlmekEvent` source as a stream of `StreamPart`s in the requested
 * modes. Composes with every producer — `stream`, `resumeStream`,
 * `resumeKeyedStream`, or a replay of buffered events:
 *
 *     for await (const part of projected(resumeStream(g, "yes", opts), ["updates"])) { ... }
 */
export async function* projected(
    source: AsyncIterable<IlmekEvent>,
    modes: readonly StreamMode[],
): AsyncGenerator<StreamPart> {
    for await (const event of source) {
        for (const part of project(event, modes)) yield part;
    }
}
