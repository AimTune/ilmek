/** The retry policy arithmetic of MODEL.md §16. */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import { backoffFor, shouldRetry, type RetryPolicy } from "../src/retry.ts";

describe("backoffFor", () => {
    test("is zero when no backoff is configured", () => {
        const policy: RetryPolicy = { maxAttempts: 3 };
        assert.equal(backoffFor(policy, 2), 0);
        assert.equal(backoffFor(policy, 5), 0);
    });

    test("a non-positive backoff short-circuits to zero", () => {
        assert.equal(backoffFor({ maxAttempts: 3, backoffMs: 0, factor: 2 }, 3), 0);
        assert.equal(backoffFor({ maxAttempts: 3, backoffMs: -1, factor: 2 }, 3), 0);
    });

    test("without a factor the delay is constant", () => {
        const policy: RetryPolicy = { maxAttempts: 4, backoffMs: 100 };
        assert.deepEqual([2, 3, 4].map((n) => backoffFor(policy, n)), [100, 100, 100]);
    });

    test("the first retry (attempt 2) waits exactly backoffMs", () => {
        assert.equal(backoffFor({ maxAttempts: 5, backoffMs: 100, factor: 2 }, 2), 100);
    });

    test("the delay grows geometrically from there", () => {
        const policy: RetryPolicy = { maxAttempts: 5, backoffMs: 100, factor: 2 };
        assert.deepEqual([2, 3, 4, 5].map((n) => backoffFor(policy, n)), [100, 200, 400, 800]);
    });

    test("maxBackoffMs caps the computed delay", () => {
        const policy: RetryPolicy = {
            maxAttempts: 6,
            backoffMs: 100,
            factor: 10,
            maxBackoffMs: 500,
        };
        assert.deepEqual([2, 3, 4].map((n) => backoffFor(policy, n)), [100, 500, 500]);
    });

    test("a cap below the base clamps the very first retry", () => {
        assert.equal(backoffFor({ maxAttempts: 3, backoffMs: 100, maxBackoffMs: 10 }, 2), 10);
    });

    test("a fractional factor decays", () => {
        const policy: RetryPolicy = { maxAttempts: 4, backoffMs: 100, factor: 0.5 };
        assert.deepEqual([2, 3, 4].map((n) => backoffFor(policy, n)), [100, 50, 25]);
    });
});

describe("shouldRetry", () => {
    test("the default policy of one attempt never retries", () => {
        assert.equal(shouldRetry({ maxAttempts: 1 }, 1, new Error("boom")), false);
    });

    test("retries while attempts remain", () => {
        const policy: RetryPolicy = { maxAttempts: 3 };
        assert.equal(shouldRetry(policy, 1, new Error("boom")), true);
        assert.equal(shouldRetry(policy, 2, new Error("boom")), true);
        assert.equal(shouldRetry(policy, 3, new Error("boom")), false);
    });

    test("maxAttempts counts the first try, so N attempts means N-1 retries", () => {
        const policy: RetryPolicy = { maxAttempts: 2 };
        assert.equal(shouldRetry(policy, 1, new Error("boom")), true);
        assert.equal(shouldRetry(policy, 2, new Error("boom")), false);
    });

    test("an attempt past the limit still refuses", () => {
        assert.equal(shouldRetry({ maxAttempts: 3 }, 99, new Error("boom")), false);
    });

    test("retryOn selects which errors are worth another attempt", () => {
        const policy: RetryPolicy = {
            maxAttempts: 3,
            retryOn: (e) => e instanceof TypeError,
        };

        assert.equal(shouldRetry(policy, 1, new TypeError("flaky")), true);
        assert.equal(shouldRetry(policy, 1, new RangeError("fatal")), false);
    });

    test("retryOn cannot extend the attempt budget", () => {
        const policy: RetryPolicy = { maxAttempts: 2, retryOn: () => true };
        assert.equal(shouldRetry(policy, 2, new Error("boom")), false);
    });

    test("retryOn is consulted only while attempts remain", () => {
        let calls = 0;
        const policy: RetryPolicy = {
            maxAttempts: 2,
            retryOn: () => {
                calls++;
                return true;
            },
        };

        shouldRetry(policy, 2, new Error("boom"));
        assert.equal(calls, 0);
    });

    test("a non-Error throw is still a candidate", () => {
        assert.equal(shouldRetry({ maxAttempts: 2 }, 1, "just a string"), true);
    });
});
