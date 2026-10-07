/**
 * The Checkpointer port contract (MODEL.md §7), as an executable suite any
 * durable backend runs against itself. Kept byte-identical in
 * checkpointers/sqlite/test and checkpointers/postgres/test — each package's
 * tests only see its own folder, so the file is copied rather than shared.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    channel,
    END,
    generateCheckpointId,
    graph,
    Journal,
    resume,
    run,
    START,
    type Checkpoint,
    type Checkpointer,
} from "@ilmek/core";

export interface ContractOptions {
    /** A fresh, migrated, empty checkpointer. Called once per test. */
    make(): Promise<Checkpointer>;
    /** Release whatever `make` opened. */
    dispose?(cp: Checkpointer): Promise<void> | void;
    /** Strings the backend cannot store, and the error it raises for them. */
    rejectsNul?: RegExp;
}

function checkpoint(threadId: string, fields: Partial<Checkpoint> = {}): Checkpoint {
    return {
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
    };
}

export const UNICODE_SAMPLES: Record<string, string> = {
    emoji: "🧶 ilmek 👩🏽‍💻 🇹🇷",
    cjk: "編み目 — 针脚 — 뜨개",
    rtl: "غرزة · תפר",
    combining: "é å",
    turkish: "İı Ğğ Şş Çç Öö Üü",
    quotes: `'"\`; DROP TABLE x; --`,
    whitespace: "tab\tnewline\ncr\r nbsp ls",
};

export function checkpointerContract(name: string, opts: ContractOptions): void {
    const withCp = (fn: (cp: Checkpointer) => Promise<void>) => async () => {
        const cp = await opts.make();
        try {
            await fn(cp);
        } finally {
            await opts.dispose?.(cp);
        }
    };

    describe(`${name} — Checkpointer contract`, () => {
        test(
            "an empty store answers every read with nothing, and no-op writes do not throw",
            withCp(async (cp) => {
                assert.equal(await cp.get("nobody"), null);
                assert.equal(await cp.get("nobody", "ckpt-missing"), null);
                assert.deepEqual(await cp.list("nobody"), []);
                assert.deepEqual((await cp.getJournal("nobody:root:x")).dump(), []);
                await cp.dropJournal("nobody:root:x");
                await cp.deleteThread("nobody");
            }),
        );

        test(
            "get without an id is the max id, not the last one written",
            withCp(async (cp) => {
                const older = checkpoint("t", { step: 1 });
                const newer = checkpoint("t", { step: 2 });
                await cp.put(newer);
                await cp.put(older);
                assert.equal((await cp.get("t"))?.id, newer.id);
            }),
        );

        test(
            "get with an unknown id is null rather than falling back to latest",
            withCp(async (cp) => {
                await cp.put(checkpoint("t"));
                assert.equal(await cp.get("t", "ckpt-not-here"), null);
            }),
        );

        test(
            "a checkpoint id from another thread is not found through this one",
            withCp(async (cp) => {
                const other = checkpoint("other");
                await cp.put(other);
                assert.equal(await cp.get("t", other.id), null);
            }),
        );

        test(
            "putting the same id twice replaces the checkpoint",
            withCp(async (cp) => {
                const c = checkpoint("t", { step: 1, channels: { v: "first" } });
                await cp.put(c);
                await cp.put({ ...c, step: 2, channels: { v: "second" } });
                const all = await cp.list("t");
                assert.equal(all.length, 1);
                assert.equal(all[0]!.step, 2);
                assert.deepEqual(all[0]!.channels, { v: "second" });
            }),
        );

        test(
            "list is newest first; limit 0, 1 and more-than-stored all behave",
            withCp(async (cp) => {
                const ids: string[] = [];
                for (let i = 0; i < 4; i++) {
                    const c = checkpoint("t", { step: i });
                    ids.push(c.id);
                    await cp.put(c);
                }
                const desc = [...ids].reverse();
                assert.deepEqual((await cp.list("t")).map((c) => c.id), desc);
                assert.deepEqual(await cp.list("t", { limit: 0 }), []);
                assert.deepEqual((await cp.list("t", { limit: 1 })).map((c) => c.id), desc.slice(0, 1));
                assert.deepEqual((await cp.list("t", { limit: 99 })).map((c) => c.id), desc);
            }),
        );

        test(
            "a checkpoint round-trips every field",
            withCp(async (cp) => {
                const c = checkpoint("t", {
                    parentId: "ckpt-parent",
                    planId: "ckpt-plan",
                    channels: { messages: ["a", "b"], cart: { items: [{ sku: 1, qty: 2 }] }, flag: false, none: null },
                    next: [
                        { node: "worker", taskKey: "worker#0", isSend: true, input: { item: 1 } },
                        { node: "agent", taskKey: "agent", isSend: false },
                    ],
                    pending: [{ id: "agent:interrupt#0", taskId: "t:ckpt-plan:agent", node: "agent", key: "interrupt#0", payload: { q: "?" } }],
                    step: 7,
                    ts: 1752570000000,
                });
                await cp.put(c);
                assert.deepEqual(await cp.get("t", c.id), c);
            }),
        );

        test(
            "threads are isolated, even with ids that look like SQL or wildcards",
            withCp(async (cp) => {
                const ids = ["t", "t2", "T", "t%", "t_", "'; DROP TABLE x; --", "🧶", ""];
                for (const id of ids) await cp.put(checkpoint(id, { channels: { owner: id } }));
                for (const id of ids) {
                    const all = await cp.list(id);
                    assert.equal(all.length, 1, `thread ${JSON.stringify(id)}`);
                    assert.equal(all[0]!.channels.owner, id);
                }
            }),
        );

        test(
            "deleteThread drops only that thread's checkpoints",
            withCp(async (cp) => {
                await cp.put(checkpoint("t"));
                await cp.put(checkpoint("t2"));
                await cp.put(checkpoint("t%"));
                await cp.deleteThread("t");
                assert.equal(await cp.get("t"), null);
                assert.notEqual(await cp.get("t2"), null);
                assert.notEqual(await cp.get("t%"), null);
            }),
        );

        test(
            "deleteThread drops the thread's journals and no other thread's",
            withCp(async (cp) => {
                const j = new Journal();
                j.putDone("s#0", "v");
                const owned = ["t:root:work", "t:ckpt-00001-000000-abc:work", "t:ckpt-00002-000000-def:worker#3"];
                const foreign = ["t2:root:work", "tx:ckpt-1:work", "other:root:t", "t%:root:work", "t_:root:work"];
                for (const id of [...owned, ...foreign]) await cp.putJournal(id, j);

                await cp.deleteThread("t");

                for (const id of owned) assert.deepEqual((await cp.getJournal(id)).dump(), [], `${id} dropped`);
                for (const id of foreign) assert.equal((await cp.getJournal(id)).keys().length, 1, `${id} kept`);
            }),
        );

        test(
            "deleting a thread whose id has wildcard characters does not touch look-alikes",
            withCp(async (cp) => {
                const j = new Journal();
                j.putDone("s#0", 1);
                await cp.putJournal("a%:root:n", j);
                await cp.putJournal("ab:root:n", j);
                await cp.putJournal("a_:root:n", j);
                await cp.putJournal("ax:root:n", j);

                await cp.deleteThread("a%");
                await cp.deleteThread("a_");

                assert.deepEqual((await cp.getJournal("a%:root:n")).keys(), []);
                assert.deepEqual((await cp.getJournal("a_:root:n")).keys(), []);
                assert.deepEqual((await cp.getJournal("ab:root:n")).keys(), ["s#0"]);
                assert.deepEqual((await cp.getJournal("ax:root:n")).keys(), ["s#0"]);
            }),
        );

        test(
            "an empty journal round-trips as empty",
            withCp(async (cp) => {
                await cp.putJournal("t:root:n", new Journal());
                assert.deepEqual((await cp.getJournal("t:root:n")).dump(), []);
            }),
        );

        test(
            "a journal keeps entry order, pending status, and falsy values",
            withCp(async (cp) => {
                const j = new Journal();
                j.putDone("z#0", 0);
                j.putDone("a#0", "");
                j.putPending("interrupt#0", { q: "?" });
                j.putDone("m#0", false);
                j.putDone("n#0", null);
                await cp.putJournal("t:root:n", j);

                const back = await cp.getJournal("t:root:n");
                assert.deepEqual(back.keys(), ["z#0", "a#0", "interrupt#0", "m#0", "n#0"]);
                assert.deepEqual(back.fetch("z#0"), { status: "done", value: 0 });
                assert.deepEqual(back.fetch("a#0"), { status: "done", value: "" });
                assert.deepEqual(back.fetch("m#0"), { status: "done", value: false });
                assert.deepEqual(back.fetch("n#0"), { status: "done", value: null });
                assert.deepEqual(back.pending(), [{ key: "interrupt#0", payload: { q: "?" } }]);
            }),
        );

        test(
            "a step that returned undefined is still a completed step after a round-trip",
            withCp(async (cp) => {
                const j = new Journal();
                j.putDone("effect#0", undefined);
                await cp.putJournal("t:root:n", j);
                const entry = (await cp.getJournal("t:root:n")).fetch("effect#0");
                assert.equal(entry?.status, "done");
                assert.equal((entry as { value?: unknown }).value, undefined);
            }),
        );

        test(
            "putJournal replaces, dropJournal forgets",
            withCp(async (cp) => {
                const a = new Journal();
                a.putDone("x#0", 1);
                await cp.putJournal("t:root:n", a);
                const b = new Journal();
                b.putDone("y#0", 2);
                await cp.putJournal("t:root:n", b);
                assert.deepEqual((await cp.getJournal("t:root:n")).keys(), ["y#0"]);
                await cp.dropJournal("t:root:n");
                assert.deepEqual((await cp.getJournal("t:root:n")).keys(), []);
            }),
        );

        test(
            "a multi-megabyte payload round-trips in a checkpoint and in a journal",
            withCp(async (cp) => {
                const big = "ilmek ".repeat(700_000); // ~4.2 MB
                const c = checkpoint("t", { channels: { doc: big, list: Array.from({ length: 10_000 }, (_, i) => i) } });
                await cp.put(c);
                const back = await cp.get("t");
                assert.equal((back!.channels.doc as string).length, big.length);
                assert.equal(back!.channels.doc, big);
                assert.equal((back!.channels.list as number[]).at(-1), 9_999);

                const j = new Journal();
                j.putDone("blob#0", big);
                await cp.putJournal("t:root:n", j);
                assert.equal((await cp.getJournal("t:root:n")).fetch("blob#0")?.status, "done");
                assert.equal(((await cp.getJournal("t:root:n")).fetch("blob#0") as { value: string }).value, big);
            }),
        );

        for (const [label, text] of Object.entries(UNICODE_SAMPLES)) {
            test(
                `unicode (${label}) round-trips in thread ids, channels and journals`,
                withCp(async (cp) => {
                    const c = checkpoint(text, { channels: { [text]: text } });
                    await cp.put(c);
                    assert.deepEqual((await cp.get(text))?.channels, { [text]: text });

                    const j = new Journal();
                    j.putDone(`${text}#0`, text);
                    await cp.putJournal(`${text}:root:${text}`, j);
                    assert.deepEqual((await cp.getJournal(`${text}:root:${text}`)).dump(), [[`${text}#0`, { status: "done", value: text }]]);
                }),
            );
        }

        test(
            "a NUL character in a value " + (opts.rejectsNul ? "is refused by this backend" : "round-trips"),
            withCp(async (cp) => {
                const c = checkpoint("t", { channels: { bin: "a\u0000b" } });
                if (opts.rejectsNul) {
                    await assert.rejects(() => cp.put(c), opts.rejectsNul);
                } else {
                    await cp.put(c);
                    assert.equal((await cp.get("t"))?.channels.bin, "a\u0000b");
                }
            }),
        );

        test(
            "a step runs exactly once across an interrupt/resume cycle (§12.1)",
            withCp(async (cp) => {
                let creates = 0;
                let charges = 0;
                const g = graph("checkout")
                    .channel("log", channel.append<string>())
                    .node("checkout", async (_s, ctx) => {
                        const order = await ctx.step("create_order", () => `order-${++creates}`);
                        const ok = await ctx.interrupt<string>({ q: `charge ${order}?` });
                        await ctx.step("charge", () => ++charges);
                        return { log: [`${order}:${ok}`] };
                    })
                    .edge(START, "checkout")
                    .edge("checkout", END)
                    .compile();
                const o = { threadId: "t-once", checkpointer: cp };

                const paused = await run(g, {}, o);
                assert.deepEqual(paused.pending[0]?.payload, { q: "charge order-1?" });
                const done = await resume(g, "yes", o);
                assert.deepEqual(done.state?.log, ["order-1:yes"]);
                assert.equal(creates, 1);
                assert.equal(charges, 1);
            }),
        );

        test(
            "a fresh run on a deleted thread's id never replays the deleted conversation",
            withCp(async (cp) => {
                // Regression: deleteThread kept the journals, and a new run on the
                // same id plans its first task under the same `root` task id — so
                // bob was greeted with alice's journaled step and alice's pause.
                const g = graph("greet")
                    .channel("who", channel.lastWrite<string>(""))
                    .channel("log", channel.append<string>())
                    .node("greet", async (s, ctx) => {
                        const hello = await ctx.step("hello", () => `hello ${s.who}`);
                        const ok = await ctx.interrupt<string>({ hello });
                        return { log: [`${hello}/${ok}`] };
                    })
                    .edge(START, "greet")
                    .edge("greet", END)
                    .compile();
                const o = { threadId: "reused", checkpointer: cp };

                assert.deepEqual((await run(g, { who: "alice" }, o)).pending[0]?.payload, { hello: "hello alice" });
                await cp.deleteThread("reused");

                const bob = await run(g, { who: "bob" }, o);
                assert.deepEqual(bob.pending[0]?.payload, { hello: "hello bob" });
                assert.deepEqual((await resume(g, "hi", o)).state?.log, ["hello bob/hi"]);
            }),
        );

        test(
            "a non-JSON step value replays as its JSON form (MODEL.md §5.4)",
            withCp(async (cp) => {
                const seen: unknown[] = [];
                const g = graph("dates")
                    .channel("log", channel.append<string>())
                    .node("n", async (_s, ctx) => {
                        seen.push(await ctx.step("when", () => new Date(Date.UTC(2026, 0, 2))));
                        await ctx.interrupt();
                        return {};
                    })
                    .edge(START, "n")
                    .compile();
                const o = { threadId: "t-date", checkpointer: cp };
                await run(g, {}, o);
                await resume(g, "ok", o);
                assert.ok(seen[0] instanceof Date);
                assert.equal(seen[1], "2026-01-02T00:00:00.000Z");
            }),
        );
    });
}
