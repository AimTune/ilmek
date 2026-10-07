/**
 * Edge cases for the SQLite checkpointer: the shared port contract against
 * real SQLite (memory and file), plus the knobs only this backend has —
 * migration, WAL, table prefixes and the duck-typed database.
 */

import test, { describe, after } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";

import { Journal, generateCheckpointId, type Checkpoint } from "@ilmek/core";
import { SqliteCheckpointer, type SqliteDatabase } from "../src/index.ts";
import { checkpointerContract } from "./contract.ts";

const workDir = mkdtempSync(join(tmpdir(), "ilmek-sqlite-edge-"));
after(() => rmSync(workDir, { recursive: true, force: true }));
let files = 0;

checkpointerContract("SqliteCheckpointer (:memory:)", {
    make: () => SqliteCheckpointer.open(":memory:"),
    dispose: (cp) => (cp as SqliteCheckpointer).close(),
});

checkpointerContract("SqliteCheckpointer (file, WAL)", {
    make: () => SqliteCheckpointer.open(join(workDir, `contract-${files++}.db`)),
    dispose: (cp) => (cp as SqliteCheckpointer).close(),
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

describe("SqliteCheckpointer — tablePrefix", () => {
    for (const bad of ["", "1app", "app-1", "app 1", "app;DROP TABLE x;--", "a.b", "ilmek\"", "ümlaut"]) {
        test(`the prefix ${JSON.stringify(bad)} is refused before any SQL runs`, () => {
            const db = new DatabaseSync(":memory:");
            assert.throws(
                () => new SqliteCheckpointer(db as unknown as SqliteDatabase, { tablePrefix: bad }),
                /tablePrefix must be a plain SQL identifier/,
            );
            // Nothing was created.
            assert.deepEqual(db.prepare("SELECT name FROM sqlite_master").all(), []);
            db.close();
        });
    }

    test("a hostile prefix passed to open() is refused too", async () => {
        await assert.rejects(() => SqliteCheckpointer.open(":memory:", { tablePrefix: "x; --" }), /plain SQL identifier/);
    });

    for (const good of ["a", "_", "App_2", "ilmek_v2"]) {
        test(`the prefix ${JSON.stringify(good)} names both tables`, async () => {
            const db = new DatabaseSync(":memory:");
            const cp = new SqliteCheckpointer(db as unknown as SqliteDatabase, { tablePrefix: good });
            await cp.migrate();
            const names = (db.prepare("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name").all() as Array<{ name: string }>).map((r) => r.name);
            assert.deepEqual(names, [`${good}_checkpoints`, `${good}_journals`]);
            db.close();
        });
    }

    test("two prefixes on one file see neither each other's checkpoints nor journals", async () => {
        const db = new DatabaseSync(join(workDir, "prefixes.db"));
        const a = new SqliteCheckpointer(db as unknown as SqliteDatabase, { tablePrefix: "app_a" });
        const b = new SqliteCheckpointer(db as unknown as SqliteDatabase, { tablePrefix: "app_b" });
        await a.migrate();
        await b.migrate();

        await a.put(ckpt("shared"));
        const j = new Journal();
        j.putDone("k#0", "a's");
        await a.putJournal("shared:root:n", j);

        assert.equal(await b.get("shared"), null);
        assert.deepEqual((await b.getJournal("shared:root:n")).keys(), []);

        // And b deleting the same thread id leaves a's data alone.
        await b.deleteThread("shared");
        assert.notEqual(await a.get("shared"), null);
        assert.deepEqual((await a.getJournal("shared:root:n")).keys(), ["k#0"]);
        db.close();
    });
});

describe("SqliteCheckpointer — migration and lifecycle", () => {
    test("every operation refuses to run before migrate()", async () => {
        const cp = new SqliteCheckpointer(new DatabaseSync(":memory:") as unknown as SqliteDatabase);
        const calls: Array<[string, () => Promise<unknown>]> = [
            ["put", () => cp.put(ckpt("t"))],
            ["get", () => cp.get("t")],
            ["list", () => cp.list("t")],
            ["putJournal", () => cp.putJournal("t:root:n", new Journal())],
            ["getJournal", () => cp.getJournal("t:root:n")],
            ["dropJournal", () => cp.dropJournal("t:root:n")],
            ["deleteThread", () => cp.deleteThread("t")],
        ];
        for (const [name, call] of calls) {
            await assert.rejects(call, /call await cp\.migrate\(\)/, name);
        }
    });

    test("migrating twice keeps the data written in between", async () => {
        const cp = await SqliteCheckpointer.open(":memory:");
        const c = ckpt("t");
        await cp.put(c);
        await cp.migrate();
        assert.equal((await cp.get("t"))?.id, c.id);
        cp.close();
    });

    test("a file database is switched to WAL by default", async () => {
        const path = join(workDir, "wal.db");
        const cp = await SqliteCheckpointer.open(path);
        cp.close();
        const db = new DatabaseSync(path);
        const mode = (db.prepare("PRAGMA journal_mode").get() as { journal_mode: string }).journal_mode;
        db.close();
        assert.equal(mode, "wal");
    });

    test("wal: false leaves the journal mode alone", async () => {
        const path = join(workDir, "no-wal.db");
        const cp = await SqliteCheckpointer.open(path, { wal: false });
        cp.close();
        const db = new DatabaseSync(path);
        const mode = (db.prepare("PRAGMA journal_mode").get() as { journal_mode: string }).journal_mode;
        db.close();
        assert.equal(mode, "delete");
    });

    test("a database that refuses the WAL pragma still migrates", async () => {
        const raw = new DatabaseSync(":memory:");
        const seen: string[] = [];
        const picky: SqliteDatabase = {
            exec: (sql) => {
                seen.push(sql.trim().split(/\s+/).slice(0, 3).join(" "));
                if (/journal_mode/.test(sql)) throw new Error("no WAL here");
                return raw.exec(sql);
            },
            prepare: (sql) => raw.prepare(sql) as never,
        };
        const cp = new SqliteCheckpointer(picky);
        await cp.migrate();
        await cp.put(ckpt("t"));
        assert.notEqual(await cp.get("t"), null);
        assert.equal(seen[0], "PRAGMA journal_mode =");
    });

    test("close() on a duck-typed database without close() is a no-op", async () => {
        const raw = new DatabaseSync(":memory:");
        const noClose: SqliteDatabase = { exec: (s) => raw.exec(s), prepare: (s) => raw.prepare(s) as never };
        const cp = new SqliteCheckpointer(noClose);
        assert.doesNotThrow(() => cp.close());
        raw.close();
    });

    test("a closed checkpointer fails loudly instead of losing writes", async () => {
        const cp = await SqliteCheckpointer.open(":memory:");
        cp.close();
        await assert.rejects(() => cp.put(ckpt("t")));
    });

    test("a corrupted row surfaces as a parse error, not as an empty thread", async () => {
        const db = new DatabaseSync(":memory:");
        const cp = new SqliteCheckpointer(db as unknown as SqliteDatabase);
        await cp.migrate();
        db.prepare("INSERT INTO ilmek_checkpoints (thread_id, id, step, data) VALUES (?, ?, ?, ?)").run("t", "ckpt-x", 0, "{not json");
        await assert.rejects(() => cp.get("t"), SyntaxError);
        db.close();
    });

    test("a lone surrogate survives because JSON escapes it", async () => {
        const cp = await SqliteCheckpointer.open(":memory:");
        const c = { ...ckpt("t"), channels: { s: "x\uD800y" } };
        await cp.put(c);
        assert.equal((await cp.get("t"))?.channels.s, "x\uD800y");
        cp.close();
    });
});
