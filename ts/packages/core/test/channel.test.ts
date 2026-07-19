/** The reducers of MODEL.md §2, at the unit level. */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    UNSET,
    append,
    channel,
    lastWrite,
    materialize,
    merge,
    reduce,
    reduceChannel,
} from "../src/channel.ts";
import { ReducerError } from "../src/errors.ts";

describe("lastWrite", () => {
    test("incoming wins", () => {
        const ch = lastWrite<number>(0);
        assert.equal(ch.reducer(UNSET, 1), 1);
        assert.equal(ch.reducer(1, 2), 2);
    });

    test("without an argument the default is undefined", () => {
        assert.equal(lastWrite<number>().default, undefined);
    });

    test("writing undefined is a write, not a no-op", () => {
        const ch = lastWrite<number | undefined>(7);
        assert.equal(ch.reducer(7, undefined), undefined);
    });
});

describe("append", () => {
    test("folds one item and many items alike", () => {
        const ch = append<string>();
        assert.deepEqual(ch.reducer(UNSET, "a"), ["a"]);
        assert.deepEqual(ch.reducer(["a"], ["b", "c"]), ["a", "b", "c"]);
    });

    test("does not mutate the current value", () => {
        const ch = append<string>();
        const current = ["a"];
        const next = ch.reducer(current, "b");

        assert.deepEqual(current, ["a"]);
        assert.notEqual(next, current);
    });

    test("the default is frozen so a node cannot push into it", () => {
        const ch = append<string>();
        assert.ok(Object.isFrozen(ch.default));
        assert.throws(() => (ch.default as string[]).push("oops"), TypeError);
    });

    test("appending an empty list is a no-op that still copies", () => {
        const ch = append<string>();
        assert.deepEqual(ch.reducer(["a"], []), ["a"]);
    });
});

describe("merge", () => {
    test("incoming wins per key", () => {
        const ch = merge<{ a?: number; b?: number }>();
        assert.deepEqual(ch.reducer(UNSET, { a: 1 }), { a: 1 });
        assert.deepEqual(ch.reducer({ a: 1, b: 2 }, { b: 3 }), { a: 1, b: 3 });
    });

    test("the merge is shallow", () => {
        const ch = merge<Record<string, unknown>>();
        assert.deepEqual(ch.reducer({ nested: { keep: 1 } }, { nested: { other: 2 } }), {
            nested: { other: 2 },
        });
    });

    test("does not mutate the current value", () => {
        const ch = merge<{ a?: number }>();
        const current = { a: 1 };
        assert.deepEqual(ch.reducer(current, { a: 2 }), { a: 2 });
        assert.deepEqual(current, { a: 1 });
    });

    test("the default is frozen", () => {
        assert.ok(Object.isFrozen(merge<Record<string, unknown>>().default));
    });
});

describe("reduce (custom)", () => {
    test("current is undefined on the first write, not UNSET", () => {
        const seen: Array<number | undefined> = [];
        const ch = reduce<number, number>((current, incoming) => {
            seen.push(current);
            return (current ?? 0) + incoming;
        }, 0);

        assert.equal(ch.reducer(UNSET, 5), 5);
        assert.equal(ch.reducer(5, 3), 8);
        assert.deepEqual(seen, [undefined, 5]);
    });

    test("carries its declared default and kind", () => {
        const ch = reduce<number, number>((c, i) => (c ?? 0) + i, 42);
        assert.equal(ch.default, 42);
        assert.equal(ch.kind, "custom");
    });
});

describe("the channel namespace", () => {
    test("exposes every built-in reducer", () => {
        assert.deepEqual(Object.keys(channel).sort(), ["append", "lastWrite", "merge", "reduce"]);
    });
});

describe("reduceChannel", () => {
    test("returns the reducer's value on the happy path", () => {
        assert.deepEqual(reduceChannel("log", append<string>(), UNSET, "a"), ["a"]);
    });

    test("wraps a throwing reducer in a ReducerError naming the channel", () => {
        const boom = reduce<number, number>(() => {
            throw new Error("nope");
        }, 0);

        assert.throws(
            () => reduceChannel("count", boom, UNSET, 1),
            (err: unknown) => {
                assert.ok(err instanceof ReducerError);
                assert.match(err.message, /"count"/);
                assert.equal((err.cause as Error).message, "nope");
                return true;
            },
        );
    });

    test("a type mismatch surfaces as a ReducerError, not a raw TypeError", () => {
        // `append` calls `.concat` on the current value — a non-array current
        // (a state shape a spec-driven graph can produce) throws inside it.
        assert.throws(
            () => reduceChannel("items", append<string>(), 5 as unknown as string[], "a"),
            ReducerError,
        );
    });
});

describe("materialize", () => {
    const channels = {
        log: append<string>(),
        intent: lastWrite<string>("none"),
    };

    test("substitutes defaults for unwritten channels", () => {
        assert.deepEqual(materialize(channels, {}), { log: [], intent: "none" });
    });

    test("UNSET and undefined both mean unwritten", () => {
        assert.deepEqual(materialize(channels, { log: UNSET, intent: undefined }), {
            log: [],
            intent: "none",
        });
    });

    test("written values pass through", () => {
        assert.deepEqual(materialize(channels, { log: ["a"], intent: "buy" }), {
            log: ["a"],
            intent: "buy",
        });
    });

    test("values for channels the graph does not declare are dropped", () => {
        assert.deepEqual(Object.keys(materialize(channels, { stray: 1 })).sort(), [
            "intent",
            "log",
        ]);
    });
});
