/**
 * Checkpointer edge cases for the in-memory reference backend, and the task-id
 * helpers every backend's deleteThread relies on.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    channel,
    END,
    generateCheckpointId,
    graph,
    InMemoryCheckpointer,
    Journal,
    resume,
    run,
    START,
    taskIdFor,
    threadJournalPrefixes,
    type Checkpoint,
} from "../src/index.ts";

const ckpt = (threadId: string, fields: Partial<Checkpoint> = {}): Checkpoint => ({
    id: generateCheckpointId(),
    parentId: null,
    planId: null,
    threadId,
    channels: {},
    next: [],
    pending: [],
    step: 0,
    ts: 0,
    ...fields,
});

describe("taskIdFor / threadJournalPrefixes", () => {
    test("a thread's first superstep is planned from root", () => {
        assert.equal(taskIdFor("t", null, "work"), "t:root:work");
    });

    test("later supersteps are keyed by the checkpoint they were planned from", () => {
        assert.equal(taskIdFor("t", "ckpt-1", "worker#2"), "t:ckpt-1:worker#2");
    });

    test("every task id the engine can produce for a thread matches one of its prefixes", () => {
        const ids = [taskIdFor("t", null, "a"), taskIdFor("t", generateCheckpointId(), "b#0")];
        for (const id of ids) assert.ok(threadJournalPrefixes("t").some((p) => id.startsWith(p)), id);
    });

    test("no prefix of one thread matches a task of a thread that merely shares a leading substring", () => {
        const foreign = [taskIdFor("t2", null, "a"), taskIdFor("tt", generateCheckpointId(), "a"), taskIdFor("x", null, "t")];
        for (const id of foreign) assert.ok(!threadJournalPrefixes("t").some((p) => id.startsWith(p)), id);
    });

    test("the engine's task ids, observed live, are the ones taskIdFor computes", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const g = graph()
            .channel("n", channel.lastWrite<number>(0))
            .node("a", () => ({ n: 1 }))
            .node("b", () => ({ n: 2 }))
            .edge(START, "a")
            .edge("a", "b")
            .compile();

        const result = await run(g, {}, { threadId: "t-ids", checkpointer });
        const starts = result.events.filter((e) => e.type === "node_start") as Array<{ taskId: string }>;
        const history = await checkpointer.list("t-ids");
        const firstCommit = history.at(-1)!;

        assert.equal(starts[0]!.taskId, taskIdFor("t-ids", null, "a"));
        assert.equal(starts[1]!.taskId, taskIdFor("t-ids", firstCommit.id, "b"));
    });
});

describe("InMemoryCheckpointer — edge cases", () => {
    test("deleteThread drops that thread's journals and nobody else's", async () => {
        const cp = new InMemoryCheckpointer();
        const j = new Journal();
        j.putDone("s#0", "v");
        const owned = [taskIdFor("t", null, "a"), taskIdFor("t", generateCheckpointId(), "b")];
        const foreign = [taskIdFor("t2", null, "a"), taskIdFor("other", null, "t")];
        for (const id of [...owned, ...foreign]) await cp.putJournal(id, j);

        await cp.deleteThread("t");

        for (const id of owned) assert.deepEqual((await cp.getJournal(id)).keys(), [], id);
        for (const id of foreign) assert.deepEqual((await cp.getJournal(id)).keys(), ["s#0"], id);
    });

    test("a fresh run on a deleted thread's id never replays the deleted conversation", async () => {
        // Regression: deleteThread kept journals, so bob's new run on the reused
        // id replayed alice's journaled step and re-raised alice's pause.
        const cp = new InMemoryCheckpointer();
        const g = graph()
            .channel("who", channel.lastWrite<string>(""))
            .channel("log", channel.append<string>())
            .node("greet", async (s, ctx) => {
                const hello = await ctx.step("hello", () => `hello ${s.who}`);
                return { log: [`${hello}/${await ctx.interrupt<string>({ hello })}`] };
            })
            .edge(START, "greet")
            .edge("greet", END)
            .compile();
        const opts = { threadId: "reused", checkpointer: cp };

        await run(g, { who: "alice" }, opts);
        await cp.deleteThread("reused");

        const bob = await run(g, { who: "bob" }, opts);
        assert.deepEqual(bob.pending[0]?.payload, { hello: "hello bob" });
        assert.deepEqual((await resume(g, "hi", opts)).state?.log, ["hello bob/hi"]);
    });

    test("list with limit 0 is empty; a limit above the count returns everything", async () => {
        const cp = new InMemoryCheckpointer();
        await cp.put(ckpt("t"));
        await cp.put(ckpt("t"));
        assert.deepEqual(await cp.list("t", { limit: 0 }), []);
        assert.equal((await cp.list("t", { limit: 50 })).length, 2);
    });

    test("an empty-string thread id is a thread like any other", async () => {
        const cp = new InMemoryCheckpointer();
        await cp.put(ckpt("", { channels: { v: 1 } }));
        assert.deepEqual((await cp.get(""))?.channels, { v: 1 });
        assert.equal(await cp.get(" "), null);
    });

    test("an empty-string checkpoint id means latest, like null", async () => {
        const cp = new InMemoryCheckpointer();
        const c = ckpt("t");
        await cp.put(c);
        assert.equal((await cp.get("t", ""))?.id, c.id);
        assert.equal((await cp.get("t", null))?.id, c.id);
    });

    test("an empty journal round-trips as empty", async () => {
        const cp = new InMemoryCheckpointer();
        await cp.putJournal("t:root:n", new Journal());
        assert.deepEqual((await cp.getJournal("t:root:n")).dump(), []);
    });

    test("a multi-megabyte value is stored and returned whole", async () => {
        const cp = new InMemoryCheckpointer();
        const big = "x".repeat(5 * 1024 * 1024);
        await cp.put(ckpt("t", { channels: { big } }));
        assert.equal(((await cp.get("t"))?.channels.big as string).length, big.length);
    });

    test("unicode thread ids and journal keys are distinct, exact-match keys", async () => {
        const cp = new InMemoryCheckpointer();
        // Composed vs decomposed é are different strings and different threads.
        await cp.put(ckpt("café", { channels: { form: "nfc" } }));
        await cp.put(ckpt("café", { channels: { form: "nfd" } }));
        assert.equal((await cp.get("café"))?.channels.form, "nfc");
        assert.equal((await cp.get("café"))?.channels.form, "nfd");

        const j = new Journal();
        j.putDone("🧶#0", "stitch");
        await cp.putJournal("🧶:root:n", j);
        assert.deepEqual((await cp.getJournal("🧶:root:n")).fetch("🧶#0"), { status: "done", value: "stitch" });
    });
});
