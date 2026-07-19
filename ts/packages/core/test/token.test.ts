/** The token envelope of MODEL.md §10.2. */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import { isToken, token } from "../src/token.ts";

describe("token", () => {
    test("builds a chunk without a meta key when none is given", () => {
        const chunk = token("hel");

        assert.deepEqual(chunk, { type: "token", text: "hel" });
        // Absent, not undefined: the envelope goes over the wire as JSON.
        assert.equal("meta" in chunk, false);
    });

    test("carries meta when given", () => {
        assert.deepEqual(token("lo", { node: "answer", model: "opus" }), {
            type: "token",
            text: "lo",
            meta: { node: "answer", model: "opus" },
        });
    });

    test("an empty string is a legal delta", () => {
        assert.deepEqual(token(""), { type: "token", text: "" });
    });
});

describe("isToken", () => {
    test("accepts what token() builds, with and without meta", () => {
        assert.equal(isToken(token("hi")), true);
        assert.equal(isToken(token("hi", { node: "answer" })), true);
    });

    test("accepts a hand-built envelope of the right shape", () => {
        assert.equal(isToken({ type: "token", text: "hi", extra: 1 }), true);
    });

    test("rejects other custom events on the same channel", () => {
        assert.equal(isToken({ progress: 0.5 }), false);
        assert.equal(isToken({ type: "progress", text: "hi" }), false);
    });

    test("rejects a token-typed value with a non-string text", () => {
        assert.equal(isToken({ type: "token" }), false);
        assert.equal(isToken({ type: "token", text: 42 }), false);
        assert.equal(isToken({ type: "token", text: null }), false);
    });

    test("rejects null and primitives without throwing", () => {
        for (const value of [null, undefined, "token", 42, true, Symbol("token")]) {
            assert.equal(isToken(value), false);
        }
    });

    test("rejects arrays and functions", () => {
        assert.equal(isToken([]), false);
        assert.equal(isToken(() => {}), false);
    });

    test("narrows the type for a consumer", () => {
        const value: unknown = token("hi", { node: "answer" });
        assert.ok(isToken(value));
        // Compiles only because isToken narrowed `value` to TokenChunk.
        assert.equal(value.text, "hi");
        assert.equal(value.meta?.node, "answer");
    });
});
