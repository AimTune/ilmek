import type { Channel, ChannelMap, StateOf, UpdateOf } from "./channel.ts";
import type { Context } from "./context.ts";
import type { Command, Goto, Send } from "./control.ts";
import { isSend } from "./control.ts";
import { GraphError } from "./errors.ts";
import type { RetryPolicy } from "./retry.ts";
import type { SpecPredicate } from "./spec.ts";

/** The virtual entry node. Implicit; has no body. */
export const START = "__start__";
/** The virtual exit node. Implicit; has no body. */
export const END = "__end__";

const RESERVED: ReadonlySet<string> = new Set([START, END]);

/**
 * One named unit of work (MODEL.md §3).
 *
 * Returns a channel update, `void` (same as `{}` — effects only), or a `command`
 * (MODEL.md §15) that carries an update alongside a routing decision.
 */
export type NodeFn<C extends ChannelMap> = NodeFnIn<StateOf<C>, C>;

/**
 * A node whose input state is `In` rather than the graph's channel state.
 *
 * `In` defaults to `StateOf<C>` — an ordinary node reads the channels. A node
 * reached by a `send` (MODEL.md §14) instead receives the send payload, so its
 * author annotates that: `.node("worker", (p: { item: T }) => …)` infers
 * `In = { item: T }`. The `ctx` and the return type stay channel-typed either way.
 */
export type NodeFnIn<In, C extends ChannelMap> = (
    state: In,
    ctx: Context<C>,
) => UpdateOf<C> | Command | void | Promise<UpdateOf<C> | Command | void>;

/**
 * A guard or router runs at plan time, outside any task — it has no journal and
 * MUST NOT perform side effects (MODEL.md §3). Route on state; compute in nodes.
 */
export type GuardFn<C extends ChannelMap> = (state: StateOf<C>, ctx: Context<C>) => boolean;

/**
 * Returns the next target(s): a node name, `END`, a `send` for dynamic fan-out
 * (MODEL.md §14), or a list mixing names and sends.
 */
export type RouterFn<C extends ChannelMap> = (state: StateOf<C>, ctx: Context<C>) => Goto;

export interface GraphNode<C extends ChannelMap> {
    readonly id: string;
    readonly fn: NodeFn<C>;
    /** How the node retries on a non-interrupt error (MODEL.md §16). Absent ⇒ no retry. */
    readonly retry: RetryPolicy | null;
    /** Carried only so a code-defined graph can round-trip through `toSpec()` (MODEL.md §9). */
    readonly type: string | null;
    readonly config: Record<string, unknown>;
}

/** Exactly one of `to` (static, optionally guarded by `when`) or `router` is set. */
export interface GraphEdge<C extends ChannelMap> {
    readonly from: string;
    readonly to: string | null;
    readonly when: GuardFn<C> | null;
    readonly router: RouterFn<C> | null;
    /** The declarative equivalent of `when`, when there is one. */
    readonly specWhen: SpecPredicate | null;
}

export interface CompiledGraph<C extends ChannelMap> {
    readonly name: string | null;
    readonly channels: C;
    readonly nodes: ReadonlyMap<string, GraphNode<C>>;
    readonly nodeOrder: readonly string[];
    readonly edges: readonly GraphEdge<C>[];
}

export interface NodeOptions {
    type?: string;
    config?: Record<string, unknown>;
    /** Retry the node on a non-interrupt error (MODEL.md §16). */
    retry?: RetryPolicy;
}

export interface EdgeOptions<C extends ChannelMap> {
    when?: GuardFn<C>;
    specWhen?: SpecPredicate;
}

/**
 * The static, serializable description of a workflow (MODEL.md §3).
 *
 * Declare channels before nodes: each `.channel()` widens the builder's state
 * type, and node bodies are typed against whatever has been declared so far.
 *
 *     const g = graph("support")
 *         .channel("messages", channel.append<string>())
 *         .node("agent", async (state, ctx) => ({ messages: ["hi"] }))
 *         .edge(START, "agent")
 *         .edge("agent", END)
 *         .compile();
 */
export class GraphBuilder<C extends ChannelMap> {
    private readonly name: string | null;
    private readonly channelMap: Record<string, Channel<any, any>>;
    private readonly nodes = new Map<string, GraphNode<C>>();
    private readonly nodeOrder: string[] = [];
    private readonly edges: GraphEdge<C>[] = [];

    constructor(name: string | null = null, channels?: ChannelMap) {
        this.name = name;
        this.channelMap = { ...channels };
    }

    /** Declare a channel and its reducer (MODEL.md §2). Undeclared channels are a runtime error. */
    channel<K extends string, V, U>(
        name: K,
        ch: Channel<V, U>,
    ): GraphBuilder<C & { [P in K]: Channel<V, U> }> {
        if (name in this.channelMap) throw new GraphError(`duplicate channel ${JSON.stringify(name)}`);
        this.channelMap[name] = ch;
        // The object is the same; only its static type widens.
        return this as unknown as GraphBuilder<C & { [P in K]: Channel<V, U> }>;
    }

    node<In = StateOf<C>>(id: string, fn: NodeFnIn<In, C>, opts: NodeOptions = {}): this {
        if (RESERVED.has(id)) throw new GraphError(`${JSON.stringify(id)} is reserved and implicit`);
        if (this.nodes.has(id)) throw new GraphError(`duplicate node ${JSON.stringify(id)}`);
        if (opts.retry && opts.retry.maxAttempts < 1) {
            throw new GraphError(`node ${JSON.stringify(id)}: retry.maxAttempts must be ≥ 1`);
        }

        this.nodes.set(id, {
            id,
            // Stored channel-typed; a send-worker's narrower `In` is honored at
            // runtime, where the engine feeds it the send payload as its state.
            fn: fn as NodeFn<C>,
            retry: opts.retry ?? null,
            type: opts.type ?? null,
            config: opts.config ?? {},
        });
        this.nodeOrder.push(id);
        return this;
    }

    /** A static edge, optionally guarded: `.edge("agent", "checkout", { when: s => s.intent === "buy" })`. */
    edge(from: string, to: string, opts: EdgeOptions<C> = {}): this {
        this.edges.push({
            from,
            to,
            when: opts.when ?? null,
            router: null,
            specWhen: opts.specWhen ?? null,
        });
        return this;
    }

    /** A conditional edge returning the target name(s) at plan time. */
    router(from: string, fn: RouterFn<C>): this {
        this.edges.push({ from, to: null, when: null, router: fn, specWhen: null });
        return this;
    }

    /** Validate the graph and freeze it. Throws `GraphError`. */
    compile(): CompiledGraph<C> {
        for (const edge of this.edges) {
            if (edge.from !== START && !this.nodes.has(edge.from)) {
                throw new GraphError(`edge from unknown node ${JSON.stringify(edge.from)}`);
            }
            if (edge.to !== null && edge.to !== END && !this.nodes.has(edge.to)) {
                throw new GraphError(
                    `edge ${JSON.stringify(edge.from)} -> unknown node ${JSON.stringify(edge.to)}`,
                );
            }
        }

        if (!this.edges.some((e) => e.from === START)) {
            throw new GraphError(`graph has no entry edge — add .edge(START, "someNode")`);
        }

        return Object.freeze({
            name: this.name,
            channels: this.channelMap as C,
            nodes: this.nodes,
            nodeOrder: Object.freeze([...this.nodeOrder]),
            edges: Object.freeze([...this.edges]),
        });
    }
}

/**
 * Start a graph.
 *
 * Two ways to declare the state, both fully typed:
 *
 * ```ts
 * // 1. chained — the builder's type widens with each channel
 * const g = graph("checkout")
 *     .channel("cart", channel.lastWrite<string[]>([]))
 *     .node("checkout", (state) => …);   // state.cart: string[]
 *
 * // 2. schema-first — the shape lives in one named place you can export
 * const CheckoutState = {
 *     cart: channel.lastWrite<string[]>([]),
 *     log: channel.append<string>(),
 * } satisfies ChannelMap;
 *
 * type CheckoutState = StateOf<typeof CheckoutState>;   // a nameable state type
 *
 * const g = graph("checkout", CheckoutState)
 *     .node("checkout", (state) => …);   // same inference, declared up front
 * ```
 *
 * Prefer the schema form when the state is shared — a node in another file, a
 * test, or a botiva adapter can then import the type instead of re-deriving it.
 */
export function graph(name?: string): GraphBuilder<Record<never, never>>;
export function graph<C extends ChannelMap>(name: string | undefined, channels: C): GraphBuilder<C>;
export function graph(name?: string, channels?: ChannelMap): GraphBuilder<ChannelMap> {
    return new GraphBuilder(name ?? null, channels);
}

/**
 * Plan the targets leaving `from` (MODEL.md §4 step 1) — node names and/or
 * `send`s (MODEL.md §14).
 *
 * Router results are validated here rather than at compile time, since a router
 * only names its targets at runtime.
 */
export function targets<C extends ChannelMap>(
    g: CompiledGraph<C>,
    from: string,
    state: StateOf<C>,
    ctx: Context<C>,
): Array<string | Send> {
    const out: Array<string | Send> = [];

    for (const edge of g.edges) {
        if (edge.from !== from) continue;

        if (edge.router) out.push(...[edge.router(state, ctx)].flat());
        else if (edge.to !== null && (!edge.when || edge.when(state, ctx))) out.push(edge.to);
    }

    return resolveTargets(g, from, out);
}

/**
 * Validate raw targets and dedup the plain names (MODEL.md §14). `END` drops out;
 * sends are kept as-is — two sends with identical payloads are two real tasks, so
 * they must never be collapsed the way duplicate node names are.
 */
export function resolveTargets<C extends ChannelMap>(
    g: CompiledGraph<C>,
    from: string,
    raw: ReadonlyArray<string | Send>,
): Array<string | Send> {
    const seenNames = new Set<string>();
    const out: Array<string | Send> = [];

    for (const target of raw) {
        if (isSend(target)) {
            validateNode(g, from, target.node);
            out.push(target);
            continue;
        }
        if (target === END || seenNames.has(target)) continue;
        validateNode(g, from, target);
        seenNames.add(target);
        out.push(target);
    }

    return out;
}

function validateNode<C extends ChannelMap>(g: CompiledGraph<C>, from: string, node: string): void {
    if (!g.nodes.has(node)) {
        throw new GraphError(
            `routing from ${JSON.stringify(from)} produced ${JSON.stringify(node)}, which is ` +
                `not a node in this graph. Known nodes: ${JSON.stringify([...g.nodes.keys()])}`,
        );
    }
}

/** Order a node set by graph declaration order — the deterministic reduce order (MODEL.md §2). */
export function inTaskOrder<C extends ChannelMap>(g: CompiledGraph<C>, nodes: string[]): string[] {
    return [...nodes].sort((a, b) => g.nodeOrder.indexOf(a) - g.nodeOrder.indexOf(b));
}
