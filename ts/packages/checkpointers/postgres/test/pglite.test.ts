/**
 * The Postgres checkpointer against a real Postgres engine, without a server.
 *
 * PGlite is Postgres compiled to WASM and run in-process, so the SQL this
 * package sends — DDL, jsonb, ON CONFLICT, the parameter casts, the prefix
 * matching in deleteThread — is executed by the actual Postgres parser and
 * planner rather than a hand-written double. The FakePg suite still covers the
 * client contract; the DATABASE_URL smoke test still covers a real server.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import { PGlite } from "@electric-sql/pglite";
import { generateCheckpointId, Journal, type Checkpoint, type Checkpointer } from "@ilmek/core";
import { PostgresCheckpointer, type SqlClient } from "../src/index.ts";
import { checkpointerContract } from "./contract.ts";

const dbs = new WeakMap<Checkpointer, PGlite>();

async function fresh(tablePrefix?: string): Promise<{ cp: PostgresCheckpointer; db: PGlite }> {
    const db = new PGlite();
    const cp = new PostgresCheckpointer(db as unknown as SqlClient, tablePrefix ? { tablePrefix } : {});
    await cp.migrate();
    return { cp, db };
}

checkpointerContract("PostgresCheckpointer (PGlite)", {
    make: async () => {
        const { cp, db } = await fresh();
        dbs.set(cp, db);
        return cp;
    },
    dispose: (cp) => dbs.get(cp)?.close(),
    // jsonb cannot hold U+0000 — Postgres refuses the escape outright.
    rejectsNul: /unsupported Unicode escape sequence/,
});

const ckpt = (threadId: string): Checkpoint => ({
    id: generateCheckpointId(),
    parentId: null,
    planId: null,
    threadId,
    channels: {},
    next: [],
    pending: [],
    step: 0,
    ts: 0,
});

describe("PostgresCheckpointer (PGlite) — schema and prefixes", () => {
    test("migrate() is idempotent and creates jsonb columns", async () => {
        const { cp, db } = await fresh();
        await cp.migrate();
        const { rows } = await db.query<{ table_name: string; column_name: string; data_type: string }>(
            `SELECT table_name, column_name, data_type FROM information_schema.columns
             WHERE column_name IN ('data', 'entries') AND table_name LIKE 'ilmek_%' ORDER BY table_name`,
        );
        assert.deepEqual(
            rows.map((r) => [r.table_name, r.column_name, r.data_type]),
            [
                ["ilmek_checkpoints", "data", "jsonb"],
                ["ilmek_journals", "entries", "jsonb"],
            ],
        );
        await db.close();
    });

    test("two prefixes in one database see neither each other's checkpoints nor journals", async () => {
        const db = new PGlite();
        const a = new PostgresCheckpointer(db as unknown as SqlClient, { tablePrefix: "app_a" });
        const b = new PostgresCheckpointer(db as unknown as SqlClient, { tablePrefix: "app_b" });
        await a.migrate();
        await b.migrate();

        await a.put(ckpt("shared"));
        const j = new Journal();
        j.putDone("k#0", "a's");
        await a.putJournal("shared:root:n", j);

        assert.equal(await b.get("shared"), null);
        assert.deepEqual((await b.getJournal("shared:root:n")).keys(), []);
        await b.deleteThread("shared");
        assert.notEqual(await a.get("shared"), null);
        assert.deepEqual((await a.getJournal("shared:root:n")).keys(), ["k#0"]);
        await db.close();
    });

    test("a thread id that is SQL text is stored as data, not executed", async () => {
        const { cp, db } = await fresh();
        const evil = "x'); DROP TABLE ilmek_checkpoints; --";
        await cp.put(ckpt(evil));
        await cp.deleteThread(evil);
        // Still there, still queryable.
        assert.deepEqual(await cp.list("anyone"), []);
        assert.equal(await cp.get(evil), null);
        await db.close();
    });
});

describe("PostgresCheckpointer — tablePrefix validation", () => {
    for (const bad of ["", "1app", "app-1", "app 1", "app;DROP TABLE x;--", "a.b", 'ilmek"', "ümlaut"]) {
        test(`the prefix ${JSON.stringify(bad)} is refused before any SQL is sent`, () => {
            let sent = 0;
            const client: SqlClient = {
                query: async () => {
                    sent++;
                    return { rows: [] };
                },
            };
            assert.throws(() => new PostgresCheckpointer(client, { tablePrefix: bad }), /tablePrefix must be a plain SQL identifier/);
            assert.equal(sent, 0);
        });
    }
});
