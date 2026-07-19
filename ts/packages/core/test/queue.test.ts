/** The unbounded async queue behind `ctx.emit` (MODEL.md §10). */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import { AsyncQueue } from "../src/queue.ts";

describe("AsyncQueue", () => {
    test("a value pushed before the take is available immediately", async () => {
        const q = new AsyncQueue<string>();
        q.push("a");

        assert.equal(await q.take(), "a");
    });

    test("preserves FIFO order across buffered pushes", async () => {
        const q = new AsyncQueue<number>();
        q.push(1);
        q.push(2);
        q.push(3);

        assert.deepEqual([await q.take(), await q.take(), await q.take()], [1, 2, 3]);
    });

    test("a take before any push waits, then resolves", async () => {
        const q = new AsyncQueue<string>();
        const pending = q.take();

        let settled = false;
        void pending.then(() => (settled = true));
        await Promise.resolve();
        assert.equal(settled, false, "take must not resolve on an empty queue");

        q.push("late");
        assert.equal(await pending, "late");
    });

    test("waiters are served in the order they arrived", async () => {
        const q = new AsyncQueue<number>();
        const first = q.take();
        const second = q.take();

        q.push(1);
        q.push(2);

        assert.equal(await first, 1);
        assert.equal(await second, 2);
    });

    test("undefined is a legal value, not an empty queue", async () => {
        // The take() implementation tests items.length rather than the shifted
        // value precisely so this does not hang.
        const q = new AsyncQueue<string | undefined>();
        q.push(undefined);
        q.push("after");

        assert.equal(await q.take(), undefined);
        assert.equal(await q.take(), "after");
    });

    test("a push that lands on a waiter does not disturb the buffer", async () => {
        const q = new AsyncQueue<number>();
        q.push(1); // buffered
        const waiter = q.take(); // drains it

        assert.equal(await waiter, 1);

        const blocked = q.take(); // now the queue is empty: this parks
        q.push(2);
        assert.equal(await blocked, 2);
    });

    test("a producer running ahead of the consumer loses nothing", async () => {
        const q = new AsyncQueue<number>();
        const taken: number[] = [];

        const consumer = (async () => {
            for (let i = 0; i < 100; i++) taken.push(await q.take());
        })();

        for (let i = 0; i < 100; i++) q.push(i);
        await consumer;

        assert.deepEqual(
            taken,
            Array.from({ length: 100 }, (_, i) => i),
        );
    });

    test("interleaved push and take stay in order", async () => {
        const q = new AsyncQueue<number>();
        const taken: number[] = [];

        const consumer = (async () => {
            for (let i = 0; i < 10; i++) taken.push(await q.take());
        })();

        for (let i = 0; i < 10; i++) {
            q.push(i);
            await Promise.resolve(); // let the consumer catch up mid-flight
        }
        await consumer;

        assert.deepEqual(
            taken,
            Array.from({ length: 10 }, (_, i) => i),
        );
    });
});
