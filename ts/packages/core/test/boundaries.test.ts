/**
 * Malformed and hostile input at every public boundary of the core: the graph
 * builder, the spec loader, run input, node returns, routers and reducers.
 * Each must fail loudly with the library's own error type, naming the bad part
 * — never with a TypeError from deep inside, and never by silently
 * misbehaving.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    channel,
    END,
    fromSpec,
    graph,
    GraphError,
    inTaskOrder,
    InMemoryCheckpointer,
    ReducerError,
    RecursionLimitError,
    run,
    send,
    START,
    stream,
    threadState,
    toSpec,
    type GraphSpec,
    type IlmekEvent,
} from "../src/index.ts";

describe("GraphBuilder — structural errors", () => {
    test("START and END are reserved node ids", () => {
        assert.throws(() => graph().node(START, () => ({})), /reserved/);
        assert.throws(() => graph().node(END, () => ({})), /reserved/);
    });

    test("a duplicate node id is refused", () => {
        assert.throws(() => graph().node("a", () => ({})).node("a", () => ({})), /duplicate node "a"/);
    });

    test("a duplicate channel is refused", () => {
        assert.throws(
            () => graph().channel("x", channel.lastWrite<number>(0)).channel("x", channel.append<number>()),
            /duplicate channel "x"/,
        );
    });

    for (const bad of ["", undefined, null, 42, {}]) {
        test(`node id ${JSON.stringify(bad) ?? "undefined"} is refused`, () => {
            assert.throws(() => graph().node(bad as any, () => ({})), GraphError);
        });

        test(`channel name ${JSON.stringify(bad) ?? "undefined"} is refused`, () => {
            assert.throws(() => graph().channel(bad as any, channel.lastWrite()), GraphError);
        });
    }

    test("a node body that is not a function is refused at declaration", () => {
        assert.throws(() => graph().node("a", "not a function" as any), /expected a function/);
    });

    test("a router that is not a function is refused at declaration", () => {
        assert.throws(() => graph().node("a", () => ({})).router("a", { to: "b" } as any), /expected a function/);
    });

    test("retry.maxAttempts below 1 is refused", () => {
        assert.throws(() => graph().node("a", () => ({}), { retry: { maxAttempts: 0 } }), /maxAttempts must be ≥ 1/);
    });

    test("an edge from an unknown node fails at compile", () => {
        assert.throws(
            () => graph().node("a", () => ({})).edge(START, "a").edge("ghost", "a").compile(),
            /edge from unknown node "ghost"/,
        );
    });

    test("an edge to an unknown node fails at compile", () => {
        assert.throws(
            () => graph().node("a", () => ({})).edge(START, "a").edge("a", "ghost").compile(),
            /"a" -> unknown node "ghost"/,
        );
    });

    test("a graph with no entry edge fails at compile", () => {
        assert.throws(() => graph().node("a", () => ({})).edge("a", END).compile(), /no entry edge/);
    });

    test("an edge into START is not an edge to a node", () => {
        assert.throws(
            () => graph().node("a", () => ({})).edge(START, "a").edge("a", START).compile(),
            /unknown node "__start__"/,
        );
    });

    test("a compiled graph is frozen", () => {
        const g = graph().node("a", () => ({})).edge(START, "a").compile();
        assert.ok(Object.isFrozen(g));
        assert.ok(Object.isFrozen(g.nodeOrder));
        assert.ok(Object.isFrozen(g.edges));
    });

    test("inTaskOrder sorts by declaration order, not by name", () => {
        const g = graph()
            .node("zeta", () => ({}))
            .node("alpha", () => ({}))
            .node("mid", () => ({}))
            .edge(START, "zeta")
            .compile();
        assert.deepEqual(inTaskOrder(g, ["mid", "alpha", "zeta"]), ["zeta", "alpha", "mid"]);
    });
});

describe("channel names that shadow Object.prototype members", () => {
    // Regression: the channel table is a plain object, so `name in map` treated
    // "constructor" as already declared, and a bare `map[key]` read resolved an
    // undeclared "constructor"/"__proto__" write to Object.prototype members —
    // failing as a ReducerError ("reducer undefined") instead of a GraphError.

    for (const name of ["constructor", "toString", "hasOwnProperty", "__proto__"]) {
        test(`a channel named ${JSON.stringify(name)} declares, defaults and folds like any other`, async () => {
            const g = graph()
                .channel(name, channel.append<number>())
                .node("w", () => ({ [name]: [1, 2] }) as any)
                .edge(START, "w")
                .edge("w", END)
                .compile();

            const result = await run(g, JSON.parse(`{${JSON.stringify(name)}: [0]}`));
            assert.equal(result.status, "done");
            assert.ok(Object.hasOwn(result.state!, name));
            assert.deepEqual((result.state as Record<string, unknown>)[name], [0, 1, 2]);
            assert.equal(Object.getPrototypeOf(result.state), Object.prototype);
        });

        test(`writing an undeclared ${JSON.stringify(name)} channel is a GraphError naming it`, async () => {
            const g = graph()
                .channel("log", channel.append<string>())
                .node("w", () => JSON.parse(`{${JSON.stringify(name)}: 1}`))
                .edge(START, "w")
                .edge("w", END)
                .compile();

            const result = await run(g);
            assert.equal(result.status, "error");
            const [node, error] = result.errors[0]!;
            assert.equal(node, "w");
            assert.ok(error instanceof GraphError, `got ${String(error)}`);
            assert.match(error.message, new RegExp(`undeclared channel "${name}"`));
        });
    }

    test("hostile __proto__ input does not pollute Object.prototype", async () => {
        const g = graph().channel("log", channel.append<string>()).node("w", () => ({})).edge(START, "w").compile();

        await assert.rejects(() => run(g, JSON.parse('{"__proto__": {"polluted": true}}')), GraphError);
        assert.equal(({} as Record<string, unknown>).polluted, undefined);
    });

    test("an unwritten channel named constructor reads its default, not Object", async () => {
        const g = graph()
            .channel("constructor", channel.lastWrite<string>("default"))
            .node("w", (s) => ({ constructor: `saw ${typeof s.constructor}:${s.constructor}` }))
            .edge(START, "w")
            .edge("w", END)
            .compile();

        assert.equal((await run(g)).state?.constructor, "saw string:default");
    });
});

describe("run input", () => {
    test("input to an undeclared channel is refused before anything runs", async () => {
        let ran = false;
        const g = graph()
            .channel("a", channel.lastWrite<number>(0))
            .node("w", () => {
                ran = true;
            })
            .edge(START, "w")
            .compile();

        const events: IlmekEvent[] = [];
        await assert.rejects(async () => {
            for await (const ev of stream(g, { nope: 1 })) events.push(ev);
        }, /undeclared channel "nope"/);
        assert.equal(ran, false);
        assert.deepEqual(events, []);
    });

    test("an input key set to undefined counts as not written", async () => {
        const g = graph()
            .channel("a", channel.lastWrite<string>("default"))
            .node("w", () => ({}))
            .edge(START, "w")
            .compile();
        assert.equal((await run(g, { a: undefined })).state?.a, "default");
    });

    test("input whose reducer rejects it surfaces as a ReducerError naming the channel", async () => {
        const strict = channel.reduce<number, number>((cur, inc) => {
            if (typeof inc !== "number") throw new TypeError("numbers only");
            return (cur ?? 0) + inc;
        }, 0);
        const g = graph().channel("sum", strict).node("w", () => ({})).edge(START, "w").compile();

        await assert.rejects(
            () => run(g, { sum: "seven" }),
            (e: unknown) => e instanceof ReducerError && /channel "sum"/.test(e.message) && e.cause instanceof TypeError,
        );
    });
});

describe("node return values", () => {
    for (const [label, value] of [
        ["a string", "done"],
        ["a number", 42],
        ["a boolean", true],
        ["a bigint", 1n],
        ["a symbol", Symbol("x")],
    ] as const) {
        test(`returning ${label} is a node error, not a crash`, async () => {
            const g = graph()
                .channel("log", channel.append<string>())
                .node("w", () => value as any)
                .edge(START, "w")
                .compile();

            const result = await run(g);
            assert.equal(result.status, "error");
            const [node, error] = result.errors[0]!;
            assert.equal(node, "w");
            assert.ok(error instanceof GraphError);
            assert.match(error.message, /must return a channel update object/);
        });
    }

    test("returning an array is read as an update keyed by index, and refused", async () => {
        const g = graph().channel("log", channel.append<string>()).node("w", () => ["x"] as any).edge(START, "w").compile();
        const result = await run(g);
        assert.equal(result.status, "error");
        assert.match((result.errors[0]![1] as Error).message, /undeclared channel "0"/);
    });

    test("a node that throws a non-Error value still settles the run with that value", async () => {
        const g = graph().channel("log", channel.append<string>()).node("w", () => {
            throw "plain string";
        }).edge(START, "w").compile();

        const result = await run(g);
        assert.equal(result.status, "error");
        assert.deepEqual(result.errors, [["w", "plain string"]]);
    });

    test("a failing reduce leaves the checkpoint untouched (superstep atomicity)", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const g = graph()
            .channel("n", channel.lastWrite<number>(0))
            .node("good", () => ({ n: 1 }))
            .node("bad", () => ({ nope: 1 }) as any)
            .edge(START, "good")
            .edge(START, "bad")
            .compile();

        const result = await run(g, { n: 5 }, { threadId: "t-atomic", checkpointer });
        assert.equal(result.status, "error");
        assert.equal(result.errors[0]![0], "bad");
        // Nothing committed: a fresh thread that failed its first superstep has no checkpoint.
        assert.equal(await threadState(g, checkpointer, "t-atomic"), null);
    });

    test("when several tasks fail in one superstep, every failure is reported", async () => {
        const g = graph()
            .channel("log", channel.append<string>())
            .node("a", () => {
                throw new Error("a broke");
            })
            .node("b", () => {
                throw new Error("b broke");
            })
            .edge(START, "a")
            .edge(START, "b")
            .compile();

        const result = await run(g);
        assert.deepEqual(
            result.errors.map(([n, e]) => [n, (e as Error).message]).sort(),
            [
                ["a", "a broke"],
                ["b", "b broke"],
            ],
        );
    });
});

describe("routers", () => {
    test("a router naming an unknown node rejects the run with a GraphError listing known nodes", async () => {
        const g = graph()
            .channel("log", channel.append<string>())
            .node("a", () => ({}))
            .router(START, () => "ghost")
            .compile();

        await assert.rejects(() => run(g), (e: unknown) => e instanceof GraphError && /"ghost"/.test(e.message) && /\["a"\]/.test(e.message));
    });

    test("a router returning undefined is refused as an unknown target", async () => {
        const g = graph().node("a", () => ({})).router(START, () => undefined as any).compile();
        await assert.rejects(() => run(g), /produced undefined/);
    });

    test("a send to an unknown node is refused", async () => {
        const g = graph().node("a", () => ({})).router(START, () => send("ghost", {})).compile();
        await assert.rejects(() => run(g), /"ghost"/);
    });

    test("a router may return an empty list, which ends the run", async () => {
        const g = graph()
            .channel("log", channel.append<string>())
            .node("a", () => ({ log: ["a"] }))
            .edge(START, "a")
            .router("a", () => [])
            .compile();

        const result = await run(g);
        assert.equal(result.status, "done");
        assert.deepEqual(result.state?.log, ["a"]);
    });

    test("duplicate targets from several routers run the node once", async () => {
        let runs = 0;
        const g = graph()
            .node("t", () => {
                runs++;
            })
            .router(START, () => ["t", "t"])
            .router(START, () => "t")
            .edge(START, "t")
            .compile();

        await run(g);
        assert.equal(runs, 1);
    });

    test("a cycle hits the recursion limit with a RecursionLimitError", async () => {
        const g = graph()
            .channel("n", channel.reduce<number, number>((c, i) => (c ?? 0) + i, 0))
            .node("loop", () => ({ n: 1 }))
            .edge(START, "loop")
            .edge("loop", "loop")
            .compile();

        await assert.rejects(() => run(g, {}, { recursionLimit: 3 }), RecursionLimitError);
    });

    test("a recursion limit of zero refuses even the first superstep", async () => {
        let ran = false;
        const g = graph().node("a", () => {
            ran = true;
        }).edge(START, "a").compile();

        await assert.rejects(() => run(g, {}, { recursionLimit: 0 }), /exceeded 0 supersteps/);
        assert.equal(ran, false);
    });

    test("the recursion limit counts supersteps, so exactly N steps fit in a limit of N", async () => {
        const g = graph()
            .channel("n", channel.reduce<number, number>((c, i) => (c ?? 0) + i, 0))
            .node("a", () => ({ n: 1 }))
            .node("b", () => ({ n: 1 }))
            .node("c", () => ({ n: 1 }))
            .edge(START, "a")
            .edge("a", "b")
            .edge("b", "c")
            .compile();

        assert.equal((await run(g, {}, { recursionLimit: 3 })).state?.n, 3);
        await assert.rejects(() => run(g, {}, { recursionLimit: 2 }), RecursionLimitError);
    });
});

describe("fromSpec — malformed documents", () => {
    const registry = { noop: () => () => ({}) };
    const base: GraphSpec = {
        name: "doc",
        channels: { intent: { reducer: "last_write" } },
        nodes: [{ id: "a", type: "noop" }],
        edges: [{ from: START, to: "a" }],
    };

    for (const bad of [null, undefined, "graph", 7, []]) {
        test(`a spec that is ${JSON.stringify(bad) ?? "undefined"} is a GraphError`, () => {
            assert.throws(() => fromSpec(bad as any, registry), GraphError);
        });
    }

    test("a channel config that is not an object is a GraphError naming the channel", () => {
        assert.throws(
            () => fromSpec({ ...base, channels: { intent: null as any } }, registry),
            /channel "intent": config must be an object, got null/,
        );
    });

    test("a node that is not an object is a GraphError", () => {
        assert.throws(() => fromSpec({ ...base, nodes: [null as any] }, registry), /spec node must be an object/);
    });

    test("a node with no id is a GraphError", () => {
        assert.throws(() => fromSpec({ ...base, nodes: [{ type: "noop" } as any] }, registry), /node name must be a non-empty string/);
    });

    test("a node with no type is a GraphError", () => {
        assert.throws(() => fromSpec({ ...base, nodes: [{ id: "a" } as any] }, registry), /has no "type"/);
    });

    test("an edge that is not an object is a GraphError", () => {
        assert.throws(() => fromSpec({ ...base, edges: ["a->b" as any] }, registry), /spec edge must be an object/);
    });

    for (const type of ["constructor", "toString", "valueOf", "__proto__", "hasOwnProperty"]) {
        test(`node type ${JSON.stringify(type)} does not resolve to an Object.prototype member`, () => {
            // Regression: `registry[type]` found inherited members, so "valueOf"
            // was invoked as a builder and threw a raw TypeError.
            assert.throws(
                () => fromSpec({ ...base, nodes: [{ id: "a", type }] }, registry),
                (e: unknown) => e instanceof GraphError && /not in the registry/.test(e.message),
            );
        });
    }

    test("a registry builder that returns a non-function is a GraphError", () => {
        assert.throws(
            () => fromSpec(base, { noop: () => "nope" as any }),
            /registry entry "noop" returned string/,
        );
    });

    test("an edge to a node the document does not define fails at compile", () => {
        assert.throws(
            () => fromSpec({ ...base, edges: [...base.edges, { from: "a", to: "ghost" }] }, registry).compile(),
            /unknown node "ghost"/,
        );
    });

    test("a predicate with no channel key is malformed", () => {
        assert.throws(
            () => fromSpec({ ...base, edges: [{ from: START, to: "a", when: { eq: 1 } as any }] }, registry),
            /malformed/,
        );
    });

    test("a predicate with no known operator is refused", () => {
        assert.throws(
            () => fromSpec({ ...base, edges: [{ from: START, to: "a", when: { channel: "intent", like: "%x" } as any }] }, registry),
            /no known operator/,
        );
    });

    test("an `in` predicate whose operand is not a list is refused rather than matching everything", () => {
        assert.throws(
            () => fromSpec({ ...base, edges: [{ from: START, to: "a", when: { channel: "intent", in: "buy" } as any }] }, registry),
            /no known operator/,
        );
    });

    test("a document missing channels, nodes and edges builds an empty builder that will not compile", () => {
        const builder = fromSpec({ name: null } as any, registry);
        assert.throws(() => builder.compile(), /no entry edge/);
    });

    test("toSpec of a graph with an empty name round-trips the null name", () => {
        const g = fromSpec({ ...base, name: null }, registry).compile();
        assert.equal(toSpec(g).name, null);
    });
});

describe("declarative predicates route as documented", () => {
    const say = (text: string) => () => () => ({ out: [text] });
    const doc = (when: GraphSpec["edges"][number]["when"]): GraphSpec => ({
        name: "p",
        channels: { v: { reducer: "last_write" }, out: { reducer: "append" } },
        nodes: [
            { id: "yes", type: "yes" },
            { id: "no", type: "no" },
        ],
        edges: [
            { from: START, to: "yes", ...(when ? { when } : {}) },
            { from: START, to: "no" },
        ],
    });
    const routes = async (when: GraphSpec["edges"][number]["when"], v: unknown) => {
        const g = fromSpec(doc(when), { yes: say("yes"), no: say("no") }).compile();
        return ((await run(g, { v })).state?.out as string[]).includes("yes");
    };

    const cases: Array<[string, GraphSpec["edges"][number]["when"], unknown, boolean]> = [
        ["eq matches strictly", { channel: "v", eq: 1 }, 1, true],
        ["eq does not coerce", { channel: "v", eq: 1 }, "1", false],
        ["neq", { channel: "v", neq: "x" }, "y", true],
        ["in matches a member", { channel: "v", in: ["a", "b"] }, "b", true],
        ["in misses a non-member", { channel: "v", in: ["a", "b"] }, "c", false],
        ["gt on a number", { channel: "v", gt: 5 }, 6, true],
        ["gt is false at the boundary", { channel: "v", gt: 5 }, 5, false],
        ["gt refuses a numeric string", { channel: "v", gt: 5 }, "6", false],
        ["lt on a number", { channel: "v", lt: 5 }, 4, true],
        ["lt refuses null", { channel: "v", lt: 5 }, null, false],
        ["truthy: an empty list is falsy", { channel: "v", truthy: true }, [], false],
        ["truthy: an empty object is falsy", { channel: "v", truthy: true }, {}, false],
        ["truthy: an empty string is falsy", { channel: "v", truthy: true }, "", false],
        ["truthy: zero is falsy", { channel: "v", truthy: true }, 0, false],
        ["truthy: a non-empty object is truthy", { channel: "v", truthy: true }, { a: 1 }, true],
        ["truthy: false matches an unset channel", { channel: "v", truthy: false }, undefined, true],
    ];

    for (const [label, when, value, expected] of cases) {
        test(label, async () => {
            assert.equal(await routes(when, value), expected);
        });
    }
});

describe("fromSpec — reducers", () => {
    test("a merge channel from a spec merges shallowly and round-trips", async () => {
        const doc: GraphSpec = {
            name: "m",
            channels: { profile: { reducer: "merge" } },
            nodes: [
                { id: "a", type: "set", config: { patch: { name: "ada" } } },
                { id: "b", type: "set", config: { patch: { lang: "tr" } } },
            ],
            edges: [
                { from: START, to: "a" },
                { from: "a", to: "b" },
            ],
        };
        const g = fromSpec(doc, { set: (c) => () => ({ profile: c.patch }) }).compile();
        assert.deepEqual((await run(g, { profile: { id: 1 } })).state?.profile, { id: 1, name: "ada", lang: "tr" });
        assert.deepEqual(toSpec(g), doc);
    });

    test("a channel with no reducer named defaults to last_write", async () => {
        const g = fromSpec(
            { name: null, channels: { v: {} }, nodes: [{ id: "a", type: "t" }], edges: [{ from: START, to: "a" }] },
            { t: () => () => ({ v: 2 }) },
        ).compile();
        assert.equal((await run(g, { v: 1 })).state?.v, 2);
        assert.deepEqual(toSpec(g).channels, { v: { reducer: "last_write" } });
    });
});
