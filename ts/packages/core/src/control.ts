// Control-flow values a router or node can return (MODEL.md §14, §15).
//
// Both are branded with a symbol rather than a string tag: they are transient
// in-memory values that live only between a node/router return and the engine's
// scheduling step — they are never journaled or checkpointed (only the node
// names and input payloads they carry are). A symbol brand cannot collide with a
// user's own `{ type: ... }` data the way a string tag could.

const SEND: unique symbol = Symbol("ilmek.send");
const COMMAND: unique symbol = Symbol("ilmek.command");

/**
 * One dynamic fan-out branch (MODEL.md §14): run `node` with `input` as its
 * entire state, in parallel with the other sends of this superstep. `input` must
 * be JSON-serializable — it is written into the checkpoint so a resumed run
 * re-dispatches the same fan-out.
 */
export interface Send {
    readonly [SEND]: true;
    readonly node: string;
    readonly input: unknown;
}

export function send(node: string, input: unknown): Send {
    return { [SEND]: true, node, input };
}

export function isSend(value: unknown): value is Send {
    return typeof value === "object" && value !== null && (value as { [SEND]?: unknown })[SEND] === true;
}

/** A routing target: a node name, `END`, a `send`, or a list of those. */
export type Goto = string | Send | ReadonlyArray<string | Send>;

/**
 * A node's return that carries a routing decision alongside its update
 * (MODEL.md §15). `goto` replaces the node's static edges for this superstep;
 * omit it to fall back to them. `update` reduces exactly like a bare return.
 */
export interface Command {
    readonly [COMMAND]: true;
    readonly update?: Record<string, unknown>;
    readonly goto?: Goto;
}

export function command(spec: { update?: Record<string, unknown>; goto?: Goto }): Command {
    return {
        [COMMAND]: true,
        ...(spec.update !== undefined ? { update: spec.update } : {}),
        ...(spec.goto !== undefined ? { goto: spec.goto } : {}),
    };
}

export function isCommand(value: unknown): value is Command {
    return (
        typeof value === "object" && value !== null && (value as { [COMMAND]?: unknown })[COMMAND] === true
    );
}
