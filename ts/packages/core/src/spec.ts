import { append, lastWrite, merge, type Channel, type ChannelMap } from "./channel.ts";
import { GraphError } from "./errors.ts";
import { END, graph, START, type CompiledGraph, type GuardFn, type NodeFn } from "./graph.ts";
import type { GraphBuilder } from "./graph.ts";

/**
 * A declarative predicate — the only kind of condition a **stored** graph may
 * carry. There is no eval path: the engine interprets these, it never executes
 * text from the document.
 */
export interface SpecPredicate {
    readonly channel: string;
    readonly eq?: unknown;
    readonly neq?: unknown;
    readonly in?: readonly unknown[];
    readonly gt?: number;
    readonly lt?: number;
    readonly truthy?: boolean;
}

export interface SpecChannel {
    readonly reducer?: "last_write" | "append" | "merge";
}

export interface SpecNode {
    readonly id: string;
    readonly type: string;
    readonly config?: Record<string, unknown>;
}

export interface SpecEdge {
    readonly from: string;
    readonly to: string;
    readonly when?: SpecPredicate;
}

/** The serializable form of a graph (MODEL.md §9). */
export interface GraphSpec {
    readonly name: string | null;
    readonly channels: Record<string, SpecChannel>;
    readonly nodes: readonly SpecNode[];
    readonly edges: readonly SpecEdge[];
}

/** Maps a node `type` to a builder. Code-defined graphs register anonymous types. */
export type NodeRegistry = Record<string, (config: Record<string, unknown>) => NodeFn<any>>;

/**
 * Build a graph from its spec (MODEL.md §9).
 *
 * This is why the drag-and-drop builder is a CRUD app over a JSON document plus
 * a registry browser, and why nothing in the engine knows the builder exists.
 *
 * Specs must come from a source you control (your own database), not arbitrary
 * end-user input: `registry` resolves behaviour by name, and a spec chooses
 * which names to invoke.
 */
export function fromSpec(spec: GraphSpec, registry: NodeRegistry = {}): GraphBuilder<ChannelMap> {
    let builder = graph(spec.name ?? undefined) as GraphBuilder<ChannelMap>;

    const declared = new Set(Object.keys(spec.channels ?? {}));

    for (const [name, cfg] of Object.entries(spec.channels ?? {})) {
        builder = builder.channel(name, channelFromSpec(cfg.reducer, name)) as GraphBuilder<ChannelMap>;
    }

    for (const node of spec.nodes ?? []) {
        builder = builder.node(node.id, buildNode(node, registry), {
            type: node.type,
            config: node.config ?? {},
        });
    }

    for (const edge of spec.edges ?? []) {
        builder = builder.edge(edge.from, edge.to, {
            ...(edge.when ? { when: predicateFromSpec(edge.when, declared), specWhen: edge.when } : {}),
        });
    }

    return builder;
}

/**
 * Serialize a graph back to its spec.
 *
 * Throws when the graph holds anything a document cannot honestly represent — a
 * router, an anonymous node type, or a hand-written guard. Refusing beats
 * inventing a spec that would not rebuild the same graph.
 */
export function toSpec<C extends ChannelMap>(g: CompiledGraph<C>): GraphSpec {
    return {
        name: g.name,
        channels: Object.fromEntries(
            Object.entries(g.channels).map(([name, ch]) => [name, { reducer: reducerToSpec(ch, name) }]),
        ),
        nodes: g.nodeOrder.map((id) => {
            const node = g.nodes.get(id)!;
            if (node.type === null) {
                throw new GraphError(
                    `node ${JSON.stringify(id)} has no type, so it cannot be serialized — its ` +
                        `behaviour is an anonymous function that no registry could resolve back. ` +
                        `Give it { type: "..." } (and a matching registry entry) to make it storable.`,
                );
            }
            return { id, type: node.type, config: node.config };
        }),
        edges: g.edges.map((edge) => {
            if (edge.router) {
                throw new GraphError(
                    `the router on ${JSON.stringify(edge.from)} cannot be serialized — it is code, ` +
                        `and a stored graph must not carry executable text (MODEL.md §9). Express ` +
                        `the branch as guarded edges with declarative \`when\` predicates instead.`,
                );
            }
            if (edge.when && !edge.specWhen) {
                throw new GraphError(
                    `the guard on the edge from ${JSON.stringify(edge.from)} is a hand-written ` +
                        `function with no declarative equivalent, so it cannot be serialized. Build ` +
                        `the edge from a spec, or pass specWhen alongside it.`,
                );
            }
            return { from: edge.from, to: edge.to!, ...(edge.specWhen ? { when: edge.specWhen } : {}) };
        }),
    };
}

// ── nodes ───────────────────────────────────────────────────────────────────

function buildNode(node: SpecNode, registry: NodeRegistry): NodeFn<any> {
    if (typeof node.type !== "string") {
        throw new GraphError(
            `node ${JSON.stringify(node.id)} has no "type" — a stored graph resolves behaviour ` +
                `through the registry, so every node needs one.`,
        );
    }

    const build = registry[node.type];
    if (!build) {
        throw new GraphError(
            `node ${JSON.stringify(node.id)} has type ${JSON.stringify(node.type)}, which is not ` +
                `in the registry. Known types: ${JSON.stringify(Object.keys(registry))}`,
        );
    }

    const fn = build(node.config ?? {});
    if (typeof fn !== "function") {
        throw new GraphError(
            `registry entry ${JSON.stringify(node.type)} returned ${typeof fn}; expected a node ` +
                `function (state, ctx).`,
        );
    }
    return fn;
}

// ── reducers ────────────────────────────────────────────────────────────────

function channelFromSpec(reducer: SpecChannel["reducer"], name: string): Channel<any, any> {
    switch (reducer ?? "last_write") {
        case "last_write":
            return lastWrite<unknown>();
        case "append":
            return append<unknown>();
        case "merge":
            return merge<Record<string, unknown>>();
        default:
            throw new GraphError(
                `channel ${JSON.stringify(name)}: unknown reducer ${JSON.stringify(reducer)}. A ` +
                    `stored graph may only name a built-in reducer: "last_write", "append", "merge".`,
            );
    }
}

function reducerToSpec(ch: Channel<any, any>, name: string): "last_write" | "append" | "merge" {
    if (ch.kind === "custom") {
        throw new GraphError(
            `channel ${JSON.stringify(name)} uses a custom reducer function, which cannot be ` +
                `serialized. Stored graphs are limited to the built-in reducers.`,
        );
    }
    return ch.kind;
}

// ── declarative predicates ──────────────────────────────────────────────────

function predicateFromSpec(pred: SpecPredicate, declared: ReadonlySet<string>): GuardFn<any> {
    if (!pred || typeof pred.channel !== "string") {
        throw new GraphError(
            `predicate ${JSON.stringify(pred)} is malformed — expected an object with a "channel" ` +
                `key, e.g. { channel: "intent", eq: "buy" }.`,
        );
    }

    if (!declared.has(pred.channel)) {
        throw new GraphError(
            `predicate references channel ${JSON.stringify(pred.channel)}, which this spec does ` +
                `not declare. Declared: ${JSON.stringify([...declared])}`,
        );
    }

    const op = compileOp(pred);
    return (state) => op((state as Record<string, unknown>)[pred.channel]);
}

function compileOp(pred: SpecPredicate): (actual: unknown) => boolean {
    if ("eq" in pred) return (a) => a === pred.eq;
    if ("neq" in pred) return (a) => a !== pred.neq;
    if ("in" in pred && Array.isArray(pred.in)) return (a) => pred.in!.includes(a);
    if ("gt" in pred) return (a) => typeof a === "number" && a > pred.gt!;
    if ("lt" in pred) return (a) => typeof a === "number" && a < pred.lt!;
    if ("truthy" in pred) return (a) => isTruthy(a) === pred.truthy;

    throw new GraphError(
        `predicate on channel ${JSON.stringify(pred.channel)} has no known operator: ` +
            `${JSON.stringify(pred)}. Supported: eq, neq, in, gt, lt, truthy.`,
    );
}

// Mirrors the Elixir port's notion of emptiness so the same document routes the
// same way in both languages — JS `[] == true` would not.
function isTruthy(value: unknown): boolean {
    if (value === null || value === undefined || value === false || value === "") return false;
    if (Array.isArray(value)) return value.length > 0;
    if (typeof value === "object") return Object.keys(value).length > 0;
    return Boolean(value);
}

export { START, END };
