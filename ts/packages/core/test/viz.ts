// The cross-language Mermaid fixture (conformance/viz/README.md): each
// <case>.json is a spec plus code-only extras and render options, and
// <case>.mmd is the exact text every port must produce for it.

import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import {
    fromSpec,
    type ChannelMap,
    type CompiledGraph,
    type GraphSpec,
    type MermaidOptions,
    type NodeRegistry,
} from "../src/index.ts";

export const vizDir = join(dirname(fileURLToPath(import.meta.url)), "../../../../conformance/viz");

export interface VizCase {
    readonly spec: GraphSpec;
    /** Code routers added after the spec's edges; `targets: null` declares none. */
    readonly routers?: ReadonlyArray<{ from: string; targets: string[] | null }>;
    /** Hand-written guards (no declarative form) added after the routers. */
    readonly guards?: ReadonlyArray<{ from: string; to: string }>;
    readonly options?: {
        direction?: MermaidOptions["direction"];
        includeRouters?: boolean;
        highlight?: { next: string[]; pending: string[] };
    };
}

export function vizCases(): string[] {
    return readdirSync(vizDir)
        .filter((f) => f.endsWith(".json"))
        .map((f) => f.slice(0, -".json".length))
        .sort();
}

export function loadCase(name: string): VizCase {
    return JSON.parse(readFileSync(join(vizDir, `${name}.json`), "utf8")) as VizCase;
}

export function expectedMermaid(name: string): string {
    return readFileSync(join(vizDir, `${name}.mmd`), "utf8").replace(/\r\n/g, "\n");
}

/** True when the case needs code (routers, guards), so only the compiled form can render it. */
export function needsCode(c: VizCase): boolean {
    return (c.routers?.length ?? 0) > 0 || (c.guards?.length ?? 0) > 0;
}

export function compileCase(c: VizCase): CompiledGraph<ChannelMap> {
    const registry: NodeRegistry = {};
    for (const n of c.spec.nodes) registry[n.type] = () => () => ({});

    const builder = fromSpec(c.spec, registry);
    for (const r of c.routers ?? []) {
        builder.router(r.from, () => [], r.targets === null ? {} : { targets: r.targets });
    }
    for (const g of c.guards ?? []) builder.edge(g.from, g.to, { when: () => true });
    return builder.compile();
}

export function caseOptions(c: VizCase): MermaidOptions {
    const o = c.options ?? {};
    return {
        ...(o.direction ? { direction: o.direction } : {}),
        ...(o.includeRouters !== undefined ? { includeRouters: o.includeRouters } : {}),
        ...(o.highlight
            ? {
                  highlight: {
                      next: o.highlight.next.map((node) => ({ node })),
                      pending: o.highlight.pending.map((node) => ({ node })),
                  },
              }
            : {}),
    };
}
