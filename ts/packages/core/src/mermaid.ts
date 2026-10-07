import type { ChannelMap } from "./channel.ts";
import { GraphError } from "./errors.ts";
import { END, START, type CompiledGraph } from "./graph.ts";
import type { GraphSpec, SpecPredicate } from "./spec.ts";

export type MermaidDirection = "TD" | "TB" | "BT" | "LR" | "RL";

/** Where a thread stands — a `Checkpoint` fits, as does any `{ next, pending }`. */
export interface MermaidHighlight {
    readonly next: readonly { readonly node: string }[];
    readonly pending: readonly { readonly node: string }[];
}

export interface MermaidOptions {
    /** Flowchart direction. Default `"TD"`. */
    direction?: MermaidDirection;
    /**
     * Mark a thread's position: nodes in `next` get the `next` class (bold),
     * nodes in `pending` the `pending` class and a ⏸ in their label.
     */
    highlight?: MermaidHighlight | null;
    /** Draw router edges. Default `true`. */
    includeRouters?: boolean;
}

const DIRECTIONS: ReadonlySet<string> = new Set(["TD", "TB", "BT", "LR", "RL"]);

// Words Mermaid's flowchart grammar claims for itself; a node id spelled like
// one (in any case) breaks the parse, so such a node gets a generated id.
const KEYWORDS: ReadonlySet<string> = new Set([
    "end", "graph", "flowchart", "subgraph", "style", "class", "classdef",
    "click", "linkstyle", "direction", "default", "call", "href", "interpolate",
]);

const SAFE_ID = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** One edge, reduced to what a picture can show. */
type VizEdge =
    | { readonly kind: "static"; readonly from: string; readonly to: string }
    | { readonly kind: "spec"; readonly from: string; readonly to: string; readonly when: SpecPredicate }
    | { readonly kind: "guard"; readonly from: string; readonly to: string }
    | { readonly kind: "router"; readonly from: string; readonly targets: readonly string[] | null };

/**
 * Render a compiled graph or a stored spec as a Mermaid flowchart.
 *
 * Pure and deterministic: the same graph renders byte-identically in every
 * port (`conformance/viz`). Solid edges are static or declarative; dashed
 * edges are decided by code — a hand-written guard, or a router (to each of
 * its declared `targets`, or to a `?` when it declared none). A node's
 * `command({ goto })` is invisible here: it is decided inside the node.
 */
export function toMermaid<C extends ChannelMap>(
    source: CompiledGraph<C> | GraphSpec,
    opts: MermaidOptions = {},
): string {
    const direction = opts.direction ?? "TD";
    if (!DIRECTIONS.has(direction)) {
        throw new GraphError(`unknown Mermaid direction ${JSON.stringify(direction)}; use TD, TB, BT, LR or RL`);
    }

    const { nodes, edges: allEdges } = isCompiled(source) ? fromCompiled(source) : fromSpec(source);
    const edges = opts.includeRouters === false ? allEdges.filter((e) => e.kind !== "router") : allEdges;

    // Every node referenced by an edge is drawn, even one a spec forgot to declare.
    const order = [...nodes];
    const known = new Set(order);
    for (const e of edges) {
        for (const name of e.kind === "router" ? [e.from, ...(e.targets ?? [])] : [e.from, e.to]) {
            if (name !== START && name !== END && !known.has(name)) {
                known.add(name);
                order.push(name);
            }
        }
    }

    const used = new Set<string>([START, END]);
    for (const name of order) if (isSafeId(name)) used.add(name);
    const fresh = (base: string): string => {
        let id = base;
        while (used.has(id)) id += "_";
        used.add(id);
        return id;
    };

    const ids = new Map<string, string>([[START, START], [END, END]]);
    order.forEach((name, i) => ids.set(name, isSafeId(name) ? name : fresh(`n${i}`)));

    const unknownTargets = new Map<VizEdge, string>();
    let routerCount = 0;
    for (const e of edges) {
        if (e.kind === "router" && e.targets === null) unknownTargets.set(e, fresh(`r${routerCount++}`));
    }

    const pending = new Set((opts.highlight?.pending ?? []).map((p) => p.node));
    const next = new Set((opts.highlight?.next ?? []).map((t) => t.node).filter((n) => !pending.has(n)));

    const usesEnd = edges.some((e) => (e.kind === "router" ? (e.targets ?? []).includes(END) : e.to === END));

    const lines = [`flowchart ${direction}`, `  ${START}([START])`];
    for (const name of order) {
        lines.push(`  ${ids.get(name)}["${label(pending.has(name) ? `⏸ ${name}` : name)}"]`);
    }
    for (const id of unknownTargets.values()) lines.push(`  ${id}{"?"}`);
    if (usesEnd) lines.push(`  ${END}([END])`);

    for (const e of edges) {
        const from = ids.get(e.from)!;
        switch (e.kind) {
            case "static":
                lines.push(`  ${from} --> ${ids.get(e.to)}`);
                break;
            case "spec":
                lines.push(`  ${from} -->|"${label(predicateLabel(e.when))}"| ${ids.get(e.to)}`);
                break;
            case "guard":
                lines.push(`  ${from} -.->|"guard"| ${ids.get(e.to)}`);
                break;
            case "router":
                if (e.targets === null) lines.push(`  ${from} -.-> ${unknownTargets.get(e)}`);
                else for (const t of e.targets) lines.push(`  ${from} -.-> ${ids.get(t)}`);
                break;
        }
    }

    const marked = (set: ReadonlySet<string>) => order.filter((n) => set.has(n)).map((n) => ids.get(n)!);
    const nextIds = marked(next);
    const pendingIds = marked(pending);
    if (nextIds.length > 0) {
        lines.push("  classDef next stroke-width:3px");
        lines.push(`  class ${nextIds.join(",")} next`);
    }
    if (pendingIds.length > 0) {
        lines.push("  classDef pending stroke-width:3px,stroke-dasharray:5 3");
        lines.push(`  class ${pendingIds.join(",")} pending`);
    }

    return lines.join("\n") + "\n";
}

function isCompiled<C extends ChannelMap>(source: CompiledGraph<C> | GraphSpec): source is CompiledGraph<C> {
    return source.nodes instanceof Map;
}

function fromCompiled<C extends ChannelMap>(g: CompiledGraph<C>): { nodes: string[]; edges: VizEdge[] } {
    return {
        nodes: [...g.nodeOrder],
        edges: g.edges.map((e): VizEdge => {
            if (e.router) return { kind: "router", from: e.from, targets: e.targets };
            if (e.specWhen) return { kind: "spec", from: e.from, to: e.to!, when: e.specWhen };
            if (e.when) return { kind: "guard", from: e.from, to: e.to! };
            return { kind: "static", from: e.from, to: e.to! };
        }),
    };
}

function fromSpec(spec: GraphSpec): { nodes: string[]; edges: VizEdge[] } {
    return {
        nodes: (spec.nodes ?? []).map((n) => n.id),
        edges: (spec.edges ?? []).map((e): VizEdge =>
            e.when ? { kind: "spec", from: e.from, to: e.to, when: e.when } : { kind: "static", from: e.from, to: e.to },
        ),
    };
}

function isSafeId(name: string): boolean {
    return SAFE_ID.test(name) && !KEYWORDS.has(name.toLowerCase());
}

/**
 * A predicate as a reader would write it. The operator chosen is the one the
 * engine evaluates — the first present of eq, neq, in, gt, lt, truthy.
 */
function predicateLabel(p: SpecPredicate): string {
    if ("eq" in p) return `${p.channel} == ${value(p.eq)}`;
    if ("neq" in p) return `${p.channel} != ${value(p.neq)}`;
    if ("in" in p && Array.isArray(p.in)) return `${p.channel} in [${p.in.map(value).join(", ")}]`;
    if ("gt" in p) return `${p.channel} > ${value(p.gt)}`;
    if ("lt" in p) return `${p.channel} < ${value(p.lt)}`;
    if ("truthy" in p) return p.truthy ? `truthy(${p.channel})` : `not truthy(${p.channel})`;
    return p.channel;
}

function value(v: unknown): string {
    return v === undefined ? "null" : JSON.stringify(v);
}

/**
 * Escape text for a quoted Mermaid label. `#` opens an entity code, so it is
 * escaped first; quotes, angle brackets and backticks would otherwise end the
 * string, open HTML, or turn it into a markdown string.
 */
function label(text: string): string {
    return text
        .replace(/#/g, "#35;")
        .replace(/"/g, "#quot;")
        .replace(/</g, "#lt;")
        .replace(/>/g, "#gt;")
        .replace(/`/g, "#96;")
        .replace(/\r\n|\r|\n/g, " ");
}
