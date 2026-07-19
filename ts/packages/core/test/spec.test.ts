/** MODEL.md §9 (graphs as data) and §12.6 (round-trip). */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    channel,
    END,
    fromSpec,
    graph,
    START,
    toSpec,
    run,
    type GraphSpec,
    type NodeRegistry,
} from "../src/index.ts";

// Exactly the kind of document a drag-and-drop builder would store in a row.
const DOC: GraphSpec = {
    name: "support",
    channels: {
        messages: { reducer: "append" },
        intent: { reducer: "last_write" },
    },
    nodes: [
        { id: "classify", type: "set_intent", config: { intent: "buy" } },
        { id: "buy", type: "say", config: { text: "bought" } },
        { id: "browse", type: "say", config: { text: "browsed" } },
    ],
    edges: [
        { from: "__start__", to: "classify" },
        { from: "classify", to: "buy", when: { channel: "intent", eq: "buy" } },
        { from: "classify", to: "browse", when: { channel: "intent", neq: "buy" } },
        { from: "buy", to: "__end__" },
        { from: "browse", to: "__end__" },
    ],
};

const registry: NodeRegistry = {
    set_intent: (config) => () => ({ intent: config.intent }),
    say: (config) => () => ({ messages: [config.text] }),
};

const build = (spec: GraphSpec = DOC) => fromSpec(spec, registry).compile();

test("§12.6 compile(spec) |> toSpec() round-trips", () => {
    assert.deepEqual(toSpec(build()), DOC);
});

test("a spec-built graph runs, and its declarative predicate routes", async () => {
    const result = await run(build());

    assert.equal(result.status, "done");
    assert.deepEqual(result.state?.messages, ["bought"]);
    assert.equal(result.state?.intent, "buy");
});

test("flipping one config value in the document reroutes the graph", async () => {
    const browsing: GraphSpec = {
        ...DOC,
        nodes: DOC.nodes.map((n) =>
            n.id === "classify" ? { ...n, config: { intent: "browse" } } : n,
        ),
    };

    assert.deepEqual((await run(build(browsing))).state?.messages, ["browsed"]);
});

describe("refusals — what a document cannot honestly hold", () => {
    test("a code-defined router will not serialize", () => {
        const g = graph("coded")
            .channel("x", channel.lastWrite<number>())
            .node("a", () => ({}), { type: "noop" })
            .edge(START, "a")
            .router("a", () => END)
            .compile();

        assert.throws(() => toSpec(g), /router on "a" cannot be serialized/);
    });

    test("a node with no type will not serialize", () => {
        const g = graph("anon")
            .channel("x", channel.lastWrite<number>())
            .node("a", () => ({}))
            .edge(START, "a")
            .compile();

        assert.throws(() => toSpec(g), /node "a" has no type/);
    });

    test("a hand-written guard with no declarative equivalent will not serialize", () => {
        const g = graph("guarded")
            .channel("x", channel.lastWrite<number>())
            .node("a", () => ({}), { type: "noop" })
            .edge(START, "a")
            .edge("a", END, { when: (state) => state.x === 1 })
            .compile();

        assert.throws(() => toSpec(g), /hand-written function/);
    });

    test("a custom reducer will not serialize", () => {
        const g = graph("custom")
            .channel("total", channel.reduce<number, number>((cur, inc) => (cur ?? 0) + inc, 0))
            .node("a", () => ({}), { type: "noop" })
            .edge(START, "a")
            .compile();

        assert.throws(() => toSpec(g), /custom reducer function/);
    });

    test("an unknown node type names what the registry does know", () => {
        const spec: GraphSpec = { ...DOC, nodes: [{ id: "x", type: "nope", config: {} }] };

        assert.throws(() => fromSpec(spec, registry), /not in the registry.*set_intent/s);
    });

    test("a predicate may only reference a declared channel", () => {
        const spec: GraphSpec = {
            ...DOC,
            edges: [
                { from: "__start__", to: "classify" },
                { from: "classify", to: "buy", when: { channel: "ghost", eq: 1 } },
            ],
        };

        assert.throws(() => fromSpec(spec, registry), /channel "ghost", which this spec does not declare/);
    });

    test("a stored graph may not name an unknown reducer", () => {
        const spec = { ...DOC, channels: { messages: { reducer: "eval_this" } } } as unknown as GraphSpec;

        assert.throws(() => fromSpec(spec, registry), /unknown reducer "eval_this"/);
    });
});
