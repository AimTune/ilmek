/** The memory port of MODEL.md §7 — ids, the interrupt predicate, and the reference backend. */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    InMemoryCheckpointer,
    generateCheckpointId,
    isInterrupted,
    type Checkpoint,
    type Pending,
} from "../src/checkpoint.ts";
import { Journal } from "../src/journal.ts";

function ckpt(overrides: Partial<Checkpoint> = {}): Checkpoint {
    return {
        id: generateCheckpointId(),
        parentId: null,
        planId: null,
        threadId: "t1",
        channels: {},
        next: [],
        pending: [],
        step: 0,
        ts: 0,
        ...overrides,
    };
}

const pending: Pending = {
    id: "approve:interrupt#0",
    taskId: "task-1",
    node: "approve",
    key: "interrupt#0",
    payload: { amount: 10 },
};

describe("generateCheckpointId", () => {
    test("ids sort lexically in generation order", () => {
        const ids = Array.from({ length: 50 }, () => generateCheckpointId());
        assert.deepEqual(ids, [...ids].sort());
    });

    test("ids are unique even inside one millisecond", () => {
        const ids = Array.from({ length: 1000 }, () => generateCheckpointId());
        assert.equal(new Set(ids).size, 1000);
    });

    test("ids share a fixed-width prefix so lexical order is numeric order", () => {
        const [a, b] = [generateCheckpointId(), generateCheckpointId()];
        const width = (id: string) => id.split("-").slice(0, 3).join("-").length;

        assert.ok(a.startsWith("ckpt-"));
        assert.equal(width(a), width(b));
    });
});

describe("isInterrupted", () => {
    test("false with no pending interrupts", () => {
        assert.equal(isInterrupted(ckpt()), false);
    });

    test("true with at least one", () => {
        assert.equal(isInterrupted(ckpt({ pending: [pending] })), true);
    });
});

describe("InMemoryCheckpointer — checkpoints", () => {
    test("get on an unknown thread returns null", async () => {
        const cp = new InMemoryCheckpointer();
        assert.equal(await cp.get("nope"), null);
        assert.equal(await cp.get("nope", "ckpt-x"), null);
        assert.deepEqual(await cp.list("nope"), []);
    });

    test("get without an id returns the latest checkpoint", async () => {
        const cp = new InMemoryCheckpointer();
        const first = ckpt({ step: 0 });
        const second = ckpt({ step: 1, parentId: first.id });

        await cp.put(first);
        await cp.put(second);

        assert.equal((await cp.get("t1"))?.id, second.id);
    });

    test("latest is the max id, not the last put", async () => {
        // A branch replayed out of order must not shadow the newest checkpoint.
        const cp = new InMemoryCheckpointer();
        const older = ckpt();
        const newer = ckpt();

        await cp.put(newer);
        await cp.put(older);

        assert.equal((await cp.get("t1"))?.id, newer.id);
    });

    test("get with an id returns that exact checkpoint", async () => {
        const cp = new InMemoryCheckpointer();
        const first = ckpt({ step: 0 });
        await cp.put(first);
        await cp.put(ckpt({ step: 1 }));

        assert.equal((await cp.get("t1", first.id))?.step, 0);
    });

    test("get with an unknown id returns null rather than falling back to latest", async () => {
        const cp = new InMemoryCheckpointer();
        await cp.put(ckpt());

        assert.equal(await cp.get("t1", "ckpt-does-not-exist"), null);
    });

    test("putting the same id twice replaces it", async () => {
        const cp = new InMemoryCheckpointer();
        const id = generateCheckpointId();
        await cp.put(ckpt({ id, step: 0 }));
        await cp.put(ckpt({ id, step: 9 }));

        assert.deepEqual((await cp.list("t1")).length, 1);
        assert.equal((await cp.get("t1", id))?.step, 9);
    });

    test("threads are isolated", async () => {
        const cp = new InMemoryCheckpointer();
        await cp.put(ckpt({ threadId: "a" }));
        await cp.put(ckpt({ threadId: "b" }));

        assert.equal((await cp.list("a")).length, 1);
        assert.equal((await cp.get("a"))?.threadId, "a");
    });

    test("list is newest-first and honours limit", async () => {
        const cp = new InMemoryCheckpointer();
        const ids = [];
        for (let i = 0; i < 5; i++) {
            const c = ckpt({ step: i });
            ids.push(c.id);
            await cp.put(c);
        }

        assert.deepEqual(
            (await cp.list("t1")).map((c) => c.id),
            [...ids].reverse(),
        );
        assert.deepEqual(
            (await cp.list("t1", { limit: 2 })).map((c) => c.id),
            [...ids].reverse().slice(0, 2),
        );
        assert.deepEqual(await cp.list("t1", { limit: 0 }), []);
    });

    test("a thread is a tree: two children may share one parent", async () => {
        const cp = new InMemoryCheckpointer();
        const root = ckpt();
        const branchA = ckpt({ parentId: root.id, step: 1 });
        const branchB = ckpt({ parentId: root.id, step: 1 });

        await cp.put(root);
        await cp.put(branchA);
        await cp.put(branchB);

        const all = await cp.list("t1");
        assert.equal(all.length, 3);
        assert.equal(all.filter((c) => c.parentId === root.id).length, 2);
    });

    test("deleteThread drops every checkpoint of that thread only", async () => {
        const cp = new InMemoryCheckpointer();
        await cp.put(ckpt({ threadId: "a" }));
        await cp.put(ckpt({ threadId: "b" }));

        await cp.deleteThread("a");

        assert.equal(await cp.get("a"), null);
        assert.equal((await cp.list("b")).length, 1);
    });

    test("deleteThread on an unknown thread is a no-op", async () => {
        await assert.doesNotReject(() => new InMemoryCheckpointer().deleteThread("nope"));
    });
});

describe("InMemoryCheckpointer — journals", () => {
    test("getJournal on an unknown task returns a fresh empty journal", async () => {
        const cp = new InMemoryCheckpointer();
        const j = await cp.getJournal("task-unknown");

        assert.ok(j instanceof Journal);
        assert.deepEqual(j.keys(), []);
    });

    test("round-trips entries, order and pending status", async () => {
        const cp = new InMemoryCheckpointer();
        const j = new Journal();
        j.putDone("charge#0", { id: "ch_1" });
        j.putPending("approve#0", { amount: 10 });

        await cp.putJournal("task-1", j);
        const back = await cp.getJournal("task-1");

        assert.deepEqual(back.keys(), ["charge#0", "approve#0"]);
        assert.deepEqual(back.fetch("charge#0"), { status: "done", value: { id: "ch_1" } });
        assert.deepEqual(back.pending(), [{ key: "approve#0", payload: { amount: 10 } }]);
    });

    test("stores a snapshot: mutating after the put does not leak in", async () => {
        const cp = new InMemoryCheckpointer();
        const j = new Journal();
        j.putDone("a#0", 1);
        await cp.putJournal("task-1", j);

        j.putDone("b#0", 2);

        assert.deepEqual((await cp.getJournal("task-1")).keys(), ["a#0"]);
    });

    test("each get hands out an independent journal", async () => {
        const cp = new InMemoryCheckpointer();
        await cp.putJournal("task-1", new Journal());

        const one = await cp.getJournal("task-1");
        one.putDone("a#0", 1);

        assert.deepEqual((await cp.getJournal("task-1")).keys(), []);
    });

    test("dropJournal makes the task look unstarted again", async () => {
        const cp = new InMemoryCheckpointer();
        const j = new Journal();
        j.putDone("charge#0", "ok");
        await cp.putJournal("task-1", j);

        await cp.dropJournal("task-1");

        assert.deepEqual((await cp.getJournal("task-1")).keys(), []);
    });

    test("dropJournal on an unknown task is a no-op", async () => {
        await assert.doesNotReject(() => new InMemoryCheckpointer().dropJournal("nope"));
    });

    test("journals are keyed by task id", async () => {
        const cp = new InMemoryCheckpointer();
        const a = new Journal();
        a.putDone("a#0", 1);
        const b = new Journal();
        b.putDone("b#0", 2);

        await cp.putJournal("task-a", a);
        await cp.putJournal("task-b", b);

        assert.deepEqual((await cp.getJournal("task-a")).keys(), ["a#0"]);
        assert.deepEqual((await cp.getJournal("task-b")).keys(), ["b#0"]);
    });
});
