/**
 * The SQLite checkpointer.
 *
 * Unlike the Postgres suite — which drives a fake client because a database is
 * not always around — this one runs against **real SQLite**, because Node ships
 * it. So these tests prove the thing that matters most about a durable backend
 * and that a fake can never show: a pause written by one process is answered by
 * another, from a file on disk.
 */

import test, { describe, after } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";

import { channel, END, graph, run, resume, START, type Checkpointer } from "@ilmek/core";
import { SqliteCheckpointer, type SqliteDatabase } from "../src/index.ts";

const workDir = mkdtempSync(join(tmpdir(), "ilmek-sqlite-"));
after(() => rmSync(workDir, { recursive: true, force: true }));

const dbPath = (name: string) => join(workDir, `${name}.db`);

/** The §12.1 shape: an effect that must not happen twice across a pause. */
function checkoutGraph(effects: { create: () => void; charge: () => void }) {
    return graph("checkout")
        .channel("log", channel.append<string>())
        .node("checkout", async (_s, ctx) => {
            await ctx.step("create_order", () => {
                effects.create();
                return "order-1";
            });
            const ok = await ctx.interrupt<string>({ q: "charge?" });
            await ctx.step("charge", () => {
                effects.charge();
                return "charged";
            });
            return { log: [ok] };
        })
        .edge(START, "checkout")
        .edge("checkout", END)
        .compile();
}

describe("SqliteCheckpointer", () => {
    test("open() migrates, and migrate() is idempotent", async () => {
        const cp = await SqliteCheckpointer.open(":memory:");
        await cp.migrate(); // second call must not throw
        assert.equal(await cp.get("nobody"), null);
        cp.close();
    });

    test("using it before migrate() says so instead of failing on missing tables", async () => {
        const cp = new SqliteCheckpointer(new DatabaseSync(":memory:") as unknown as SqliteDatabase);
        await assert.rejects(() => cp.get("t"), /call await cp\.migrate\(\)/);
    });

    test("a step runs once across an interrupt/resume cycle (§12.1) on SQLite", async () => {
        const cp = await SqliteCheckpointer.open(dbPath("step-once"));
        let creates = 0;
        let charges = 0;

        const g = checkoutGraph({ create: () => creates++, charge: () => charges++ });
        const opts = { threadId: "t1", checkpointer: cp };

        const paused = await run(g, {}, opts);
        assert.equal(paused.status, "interrupted");
        assert.equal(creates, 1);
        assert.equal(charges, 0); // the pause gates the charge

        const done = await resume(g, "yes", opts);
        assert.equal(done.status, "done");
        assert.deepEqual(done.state!.log, ["yes"]);
        assert.equal(creates, 1, "create_order stayed at one — the journal round-tripped through SQLite");
        assert.equal(charges, 1);
        cp.close();
    });

    test("a pause survives closing the database — a new instance on the same FILE resumes it", async () => {
        // This is the whole point of a durable checkpointer, and the assertion a
        // fake client cannot make: nothing but the file crosses the boundary.
        const path = dbPath("across-restart");
        const g = checkoutGraph({ create: () => {}, charge: () => {} });

        const first = await SqliteCheckpointer.open(path);
        const paused = await run(g, {}, { threadId: "t2", checkpointer: first });
        assert.equal(paused.status, "interrupted");
        first.close(); // the "process" exits here

        // A different instance, a different connection, the same file on disk.
        const second = await SqliteCheckpointer.open(path);
        const pending = await second.get("t2");
        assert.equal(pending!.pending.length, 1, "the parked interrupt was read back from disk");

        const done = await resume(g, "yes", { threadId: "t2", checkpointer: second });
        assert.equal(done.status, "done");
        assert.deepEqual(done.state!.log, ["yes"]);
        second.close();
    });

    test("an effect before the pause does not re-run after a restart", async () => {
        // Same restart boundary, now watching the journal rather than the pause:
        // create_order ran in the first process and must NOT run in the second.
        const path = dbPath("effects-across-restart");
        let creates = 0;
        let charges = 0;
        const g = checkoutGraph({ create: () => creates++, charge: () => charges++ });

        const first = await SqliteCheckpointer.open(path);
        await run(g, {}, { threadId: "t3", checkpointer: first });
        first.close();
        assert.equal(creates, 1);

        const second = await SqliteCheckpointer.open(path);
        const done = await resume(g, "yes", { threadId: "t3", checkpointer: second });
        second.close();

        assert.equal(done.status, "done");
        assert.equal(creates, 1, "the journal crossed the restart, so the order was not opened twice");
        assert.equal(charges, 1);
    });

    test("get by id, list (newest first, with limit), and deleteThread", async () => {
        const cp = await SqliteCheckpointer.open(dbPath("history"));

        const g = graph("chain")
            .channel("n", channel.lastWrite<number>(0))
            .node("a", () => ({ n: 1 }))
            .node("b", () => ({ n: 2 }))
            .edge(START, "a")
            .edge("a", "b")
            .edge("b", END)
            .compile();

        await run(g, {}, { threadId: "t4", checkpointer: cp });

        const history = await cp.list("t4");
        assert.ok(history.length >= 2);
        assert.ok(history[0]!.id > history[history.length - 1]!.id, "newest first");

        const one = await cp.list("t4", { limit: 1 });
        assert.equal(one.length, 1);
        assert.equal(one[0]!.id, history[0]!.id);

        assert.deepEqual(await cp.get("t4", history[0]!.id), history[0]);

        await cp.deleteThread("t4");
        assert.equal(await cp.get("t4"), null);
        assert.deepEqual(await cp.list("t4"), []);
        cp.close();
    });

    test("a custom tablePrefix isolates two apps in one file", async () => {
        const path = dbPath("shared");
        const { DatabaseSync: DB } = await import("node:sqlite");
        const db = new DB(path) as unknown as SqliteDatabase;

        const app1 = new SqliteCheckpointer(db, { tablePrefix: "app1" });
        const app2 = new SqliteCheckpointer(db, { tablePrefix: "app2" });
        await app1.migrate();
        await app2.migrate();

        const g = graph("g")
            .channel("n", channel.lastWrite<number>(0))
            .node("a", () => ({ n: 1 }))
            .edge(START, "a")
            .edge("a", END)
            .compile();

        await run(g, {}, { threadId: "same-id", checkpointer: app1 });

        assert.ok((await app1.list("same-id")).length >= 1);
        assert.deepEqual(await app2.list("same-id"), [], "app2's tables are untouched");
        app1.close();
    });

    test("satisfies the Checkpointer interface structurally", async () => {
        const cp: Checkpointer = await SqliteCheckpointer.open(":memory:");
        assert.equal(typeof cp.put, "function");
        assert.equal(typeof cp.getJournal, "function");
    });

    test("accepts a duck-typed database, so better-sqlite3 drops in unchanged", async () => {
        // Nothing here imports node:sqlite through the checkpointer — it only
        // ever calls exec/prepare, which better-sqlite3 exposes identically.
        const raw = new DatabaseSync(":memory:");
        const duck: SqliteDatabase = {
            exec: (sql) => raw.exec(sql),
            prepare: (sql) => {
                const stmt = raw.prepare(sql);
                return {
                    run: (...p) => stmt.run(...(p as never[])),
                    get: (...p) => stmt.get(...(p as never[])),
                    all: (...p) => stmt.all(...(p as never[])),
                };
            },
        };

        const cp = new SqliteCheckpointer(duck);
        await cp.migrate();

        const g = checkoutGraph({ create: () => {}, charge: () => {} });
        const opts = { threadId: "duck", checkpointer: cp };
        assert.equal((await run(g, {}, opts)).status, "interrupted");
        assert.equal((await resume(g, "yes", opts)).status, "done");
    });
});
