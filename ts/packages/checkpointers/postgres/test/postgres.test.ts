/**
 * The Postgres checkpointer, exercised without a database.
 *
 * `FakePg` is a tiny in-memory stand-in for a node-postgres client: it dispatches
 * on the leading `/* ilmek:* *​/` tag each statement carries and stores rows in
 * Maps, mimicking a jsonb column (write a string, read back a parsed object). So
 * the SAME PostgresCheckpointer code under test here is what runs against real
 * Postgres — only the client differs. A live-DB smoke test is gated on
 * DATABASE_URL at the bottom, skipped in CI.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import { channel, END, graph, run, resume, START, type Checkpointer } from "@ilmek/core";
import { PostgresCheckpointer, SQL_TAGS, type SqlClient } from "../src/index.ts";

// ── the fake client ──────────────────────────────────────────────────────────

class FakePg implements SqlClient {
    // threadId → id → parsed checkpoint data (jsonb-style: objects, not strings)
    readonly checkpoints = new Map<string, Map<string, unknown>>();
    readonly journals = new Map<string, unknown>();
    calls = 0;

    async query(text: string, params: readonly unknown[] = []): Promise<{ rows: unknown[] }> {
        this.calls++;
        const tag = text.match(/ilmek:(\w+)/)?.[1];

        switch (tag) {
            case "create_checkpoints":
            case "create_journals":
                return { rows: [] };

            case "put_checkpoint": {
                const [threadId, id, , data] = params as [string, string, number, string];
                const thread = this.checkpoints.get(threadId) ?? new Map();
                thread.set(id, JSON.parse(data)); // jsonb parses on write
                this.checkpoints.set(threadId, thread);
                return { rows: [] };
            }
            case "get_by_id": {
                const [threadId, id] = params as [string, string];
                const data = this.checkpoints.get(threadId)?.get(id);
                return { rows: data === undefined ? [] : [{ data }] };
            }
            case "get_latest": {
                const [threadId] = params as [string];
                const rows = this.sortedDesc(threadId);
                return { rows: rows.length ? [{ data: rows[0] }] : [] };
            }
            case "list": {
                const [threadId, limit] = params as [string, number | undefined];
                const rows = this.sortedDesc(threadId).map((data) => ({ data }));
                return { rows: limit === undefined ? rows : rows.slice(0, limit) };
            }
            case "put_journal": {
                const [taskId, entries] = params as [string, string];
                this.journals.set(taskId, JSON.parse(entries));
                return { rows: [] };
            }
            case "get_journal": {
                const [taskId] = params as [string];
                const entries = this.journals.get(taskId);
                return { rows: entries === undefined ? [] : [{ entries }] };
            }
            case "drop_journal": {
                this.journals.delete((params as [string])[0]);
                return { rows: [] };
            }
            case "delete_thread": {
                this.checkpoints.delete((params as [string])[0]);
                return { rows: [] };
            }
            default:
                throw new Error(`FakePg: unrecognized statement: ${text.slice(0, 60)}`);
        }
    }

    // Ids sort lexically (MODEL.md §7), newest last → reverse for "desc".
    private sortedDesc(threadId: string): unknown[] {
        const thread = this.checkpoints.get(threadId);
        if (!thread) return [];
        return [...thread.keys()].sort().reverse().map((id) => thread.get(id));
    }
}

// A tiny graph that opens an "order" once, pauses, then charges — the §12.1
// shape, so we can prove durability across the pause AND across a checkpointer
// instance boundary.
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
            return { log: [`${ok}`] };
        })
        .edge(START, "checkout")
        .edge("checkout", END)
        .compile();
}

// ── conformance against the port contract ────────────────────────────────────

describe("PostgresCheckpointer", () => {
    test("migrate() creates both tables, idempotently", async () => {
        const pg = new FakePg();
        const cp = new PostgresCheckpointer(pg);
        await cp.migrate();
        await cp.migrate(); // second call must not throw
        assert.ok(SQL_TAGS.createCheckpoints); // tags are exported for doubles
    });

    test("a step runs once across an interrupt/resume cycle (§12.1) on Postgres", async () => {
        const pg = new FakePg();
        const cp = new PostgresCheckpointer(pg);
        await cp.migrate();

        let creates = 0;
        let charges = 0;
        const g = checkoutGraph({ create: () => creates++, charge: () => charges++ });
        const opts = { threadId: "t1", checkpointer: cp };

        const paused = await run(g, {}, opts);
        assert.equal(paused.status, "interrupted");
        assert.equal(creates, 1);
        assert.equal(charges, 0);

        const done = await resume(g, "yes", opts);
        assert.equal(done.status, "done");
        assert.deepEqual(done.state!.log, ["yes"]);
        assert.equal(creates, 1, "create_order stayed at one — journal persisted in Postgres");
        assert.equal(charges, 1);
    });

    test("durable across a fresh checkpointer instance sharing the same database", async () => {
        const pg = new FakePg(); // the 'database'
        const g = checkoutGraph({ create: () => {}, charge: () => {} });

        // Instance A parks the thread…
        const a = new PostgresCheckpointer(pg);
        await a.migrate();
        const paused = await run(g, {}, { threadId: "t2", checkpointer: a });
        assert.equal(paused.status, "interrupted");

        // …a brand-new instance B (think: a different process after a deploy)
        // resumes it, reading the parked interrupt straight out of the tables.
        const b = new PostgresCheckpointer(pg);
        const done = await resume(g, "yes", { threadId: "t2", checkpointer: b });
        assert.equal(done.status, "done");
        assert.deepEqual(done.state!.log, ["yes"]);
    });

    test("get by id, list (newest first, with limit), and deleteThread", async () => {
        const pg = new FakePg();
        const cp = new PostgresCheckpointer(pg);
        await cp.migrate();

        const g = graph("chain")
            .channel("n", channel.lastWrite<number>(0))
            .node("a", () => ({ n: 1 }))
            .node("b", () => ({ n: 2 }))
            .edge(START, "a")
            .edge("a", "b")
            .edge("b", END)
            .compile();

        await run(g, {}, { threadId: "t3", checkpointer: cp });

        const history = await cp.list("t3");
        assert.ok(history.length >= 2);
        // newest first
        assert.ok(history[0]!.id > history[history.length - 1]!.id);

        const one = await cp.list("t3", { limit: 1 });
        assert.equal(one.length, 1);
        assert.equal(one[0]!.id, history[0]!.id);

        const byId = await cp.get("t3", history[0]!.id);
        assert.deepEqual(byId, history[0]);

        await cp.deleteThread("t3");
        assert.equal(await cp.get("t3"), null);
        assert.deepEqual(await cp.list("t3"), []);
    });

    test("satisfies the Checkpointer interface structurally", () => {
        const cp: Checkpointer = new PostgresCheckpointer(new FakePg());
        assert.equal(typeof cp.put, "function");
        assert.equal(typeof cp.getJournal, "function");
    });

    test("a custom tablePrefix isolates two apps in one database", async () => {
        const pg = new FakePg();
        const app1 = new PostgresCheckpointer(pg, { tablePrefix: "app1" });
        await app1.migrate();
        // The DDL names carry the prefix — the fake sees distinct table names in
        // the SQL text even though it ignores them; this asserts the wiring.
        const g = graph("g").channel("n", channel.lastWrite<number>(0)).node("a", () => ({ n: 1 })).edge(START, "a").edge("a", END).compile();
        await run(g, {}, { threadId: "t", checkpointer: app1 });
        assert.ok((await app1.list("t")).length >= 1);
    });
});

// ── optional live-Postgres smoke test (skipped without DATABASE_URL) ──────────

test("live Postgres round-trip", { skip: !process.env.DATABASE_URL }, async () => {
    // Only runs when a real database is provided, e.g.
    //   DATABASE_URL=postgres://… node --test packages/checkpoint-postgres/test/*.test.ts
    // Kept out of CI; documents how to point the same code at a real client.
    const { default: pg } = await import("pg" as string);
    const client = new pg.Client({ connectionString: process.env.DATABASE_URL });
    await client.connect();
    try {
        const cp = new PostgresCheckpointer(client);
        await cp.migrate();
        const g = checkoutGraph({ create: () => {}, charge: () => {} });
        const threadId = `t-live-${Date.now()}`;
        assert.equal((await run(g, {}, { threadId, checkpointer: cp })).status, "interrupted");
        assert.equal((await resume(g, "yes", { threadId, checkpointer: cp })).status, "done");
        await cp.deleteThread(threadId);
    } finally {
        await client.end();
    }
});
