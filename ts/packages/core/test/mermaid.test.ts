/** Mermaid export (MODEL.md §9.1) and the shared conformance/viz fixture. */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    channel,
    END,
    graph,
    GraphError,
    InMemoryCheckpointer,
    run,
    send,
    START,
    toMermaid,
    toSpec,
} from "../src/index.ts";
import { caseOptions, compileCase, expectedMermaid, loadCase, needsCode, vizCases } from "./viz.ts";

describe("conformance/viz (shared with .NET)", () => {
    const cases = vizCases();

    test("the fixture is not empty", () => {
        assert.ok(cases.length >= 5, `found ${cases.length} cases`);
    });

    for (const name of cases) {
        test(`${name}: the compiled graph renders to ${name}.mmd`, () => {
            const c = loadCase(name);
            assert.equal(toMermaid(compileCase(c), caseOptions(c)), expectedMermaid(name));
        });

        const c = loadCase(name);
        if (!needsCode(c)) {
            test(`${name}: the stored spec renders the same text`, () => {
                assert.equal(toMermaid(c.spec, caseOptions(c)), expectedMermaid(name));
            });
        }
    }
});

describe("toMermaid", () => {
    test("a spec and the graph built from it render identically", () => {
        const g = graph("loop")
            .channel("n", channel.lastWrite<number>(0))
            .node("tick", () => ({}), { type: "tick" })
            .edge(START, "tick")
            .edge("tick", "tick", { when: (s) => s.n < 3, specWhen: { channel: "n", lt: 3 } })
            .edge("tick", END, { when: (s) => s.n >= 3, specWhen: { channel: "n", gt: 2 } })
            .compile();

        assert.equal(toMermaid(toSpec(g)), toMermaid(g));
        assert.match(toMermaid(g), /tick -->\|"n #lt; 3"\| tick/);
    });

    test("a router's declared targets include its send targets", () => {
        const g = graph("fan")
            .channel("items", channel.lastWrite<string[]>([]))
            .channel("done", channel.append<string>())
            .node("split", () => ({}))
            .node("work", () => ({}))
            .edge(START, "split")
            .router("split", (s) => s.items.map((item) => send("work", { item })), { targets: ["work"] })
            .edge("work", END)
            .compile();

        assert.match(toMermaid(g), /^ {2}split -\.-> work$/m);
    });

    test("an undeclared router draws one ? per router; includeRouters: false hides them", () => {
        const g = graph()
            .node("a", () => ({}))
            .edge(START, "a")
            .router("a", () => END)
            .compile();

        assert.match(toMermaid(g), /^ {2}a -\.-> r0$/m);
        assert.match(toMermaid(g), /^ {2}r0\{"\?"\}$/m);
        assert.doesNotMatch(toMermaid(g, { includeRouters: false }), /r0/);
    });

    test("a router target must be a node or END", () => {
        const b = graph()
            .node("a", () => ({}))
            .edge(START, "a")
            .router("a", () => END, { targets: ["nowhere"] });
        assert.throws(() => b.compile(), GraphError);
    });

    test("an unknown direction is refused", () => {
        const g = graph().node("a", () => ({})).edge(START, "a").compile();
        assert.throws(() => toMermaid(g, { direction: "XY" as never }), GraphError);
    });

    test("highlights a parked thread from its checkpoint", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const g = graph("approve")
            .channel("log", channel.append<string>())
            .node("draft", () => ({ log: ["drafted"] }))
            .node("approve", async (_s, ctx) => {
                await ctx.interrupt({ q: "ship it?" });
                return {};
            })
            .node("notify", () => ({ log: ["notified"] }))
            .edge(START, "draft")
            .edge("draft", "approve")
            .edge("draft", "notify")
            .edge("approve", END)
            .edge("notify", END)
            .compile();

        const paused = await run(g, {}, { threadId: "t1", checkpointer });
        assert.equal(paused.status, "interrupted");

        const ckpt = (await checkpointer.get("t1"))!;
        const text = toMermaid(g, { highlight: ckpt });

        assert.match(text, /^ {2}approve\["⏸ approve"\]$/m);
        assert.match(text, /^ {2}class approve pending$/m);
        // The superstep did not commit, so notify re-runs (from its journal) and is
        // still next; approve is marked pending instead, never both.
        assert.match(text, /^ {2}class notify next$/m);
    });

    test("highlights the next tasks of a checkpoint between supersteps", () => {
        const g = graph()
            .node("a", () => ({}))
            .node("b", () => ({}))
            .node("c", () => ({}))
            .edge(START, "a")
            .edge("a", "b")
            .edge("a", "c")
            .compile();

        const text = toMermaid(g, {
            highlight: { next: [{ node: "c" }, { node: "b" }, { node: "b" }], pending: [] },
        });
        // Declaration order, each node once.
        assert.match(text, /^ {2}class b,c next$/m);
        assert.doesNotMatch(text, /pending/);
    });
});
