// A SQLite-backed Checkpointer for ilmek (MODEL.md §7) — durable threads with
// zero dependencies, because Node ships SQLite in the box (`node:sqlite`).
//
// This is the provider to reach for first: a parked interrupt survives a process
// restart, a deploy, or a crash, and the whole thing is a file you can copy. For
// a single-process app or a local agent it is all the durability you need; reach
// for Postgres when several processes must share the same threads.
//
// Like the Postgres provider it talks to a **duck-typed** database, so
// better-sqlite3 (same prepare/run/get/all shape) drops in unchanged. Pass your
// own instance, or let `SqliteCheckpointer.open()` build one from a path.

import { Journal, type Checkpoint, type Checkpointer } from "@ilmek/core";

/** One prepared statement — the slice this checkpointer uses. */
export interface SqliteStatement {
    run(...params: readonly unknown[]): unknown;
    get(...params: readonly unknown[]): unknown;
    all(...params: readonly unknown[]): unknown[];
}

/** The slice of a SQLite database this checkpointer needs. `node:sqlite` and better-sqlite3 both fit. */
export interface SqliteDatabase {
    exec(sql: string): unknown;
    prepare(sql: string): SqliteStatement;
}

export interface SqliteCheckpointerOptions {
    /** Table-name prefix, so several apps can share one database file. Default `"ilmek"`. */
    readonly tablePrefix?: string;
    /**
     * Write-ahead logging. Default true for a file database — it survives a hard
     * kill better and lets a reader run while a run is writing. Ignored for
     * `:memory:`, which has no file to log to.
     */
    readonly wal?: boolean;
}

interface CheckpointRow {
    data: string;
}
interface JournalRow {
    entries: string;
}

/**
 * A `Checkpointer` (MODEL.md §7) stored in SQLite.
 *
 *     const cp = await SqliteCheckpointer.open("./agent.db");
 *     await run(graph, input, { threadId, checkpointer: cp });
 *
 * The `node:sqlite` API is synchronous; the `Checkpointer` port is async. The
 * methods below are `async` but do their work inline — honest for a local file,
 * and it keeps the port identical across providers.
 */
export class SqliteCheckpointer implements Checkpointer {
    private readonly db: SqliteDatabase;
    private readonly checkpoints: string;
    private readonly journals: string;
    private readonly wal: boolean;
    private migrated = false;

    constructor(db: SqliteDatabase, opts: SqliteCheckpointerOptions = {}) {
        this.db = db;
        const prefix = opts.tablePrefix ?? "ilmek";
        this.checkpoints = `${prefix}_checkpoints`;
        this.journals = `${prefix}_journals`;
        this.wal = opts.wal ?? true;
    }

    /**
     * Open a database at `path` and migrate it. `":memory:"` gives an ephemeral
     * store — handy for tests, but it defeats the point of a durable backend.
     *
     * `node:sqlite` is imported lazily so the duck-typed constructor stays usable
     * on runtimes that do not ship it.
     */
    static async open(path = ":memory:", opts: SqliteCheckpointerOptions = {}): Promise<SqliteCheckpointer> {
        const { DatabaseSync } = await import("node:sqlite");
        const cp = new SqliteCheckpointer(new DatabaseSync(path) as unknown as SqliteDatabase, opts);
        await cp.migrate();
        return cp;
    }

    /** Create the two tables if absent. Idempotent; safe to call on every boot. */
    async migrate(): Promise<void> {
        if (this.wal) {
            // A memory database has no WAL; SQLite reports that rather than
            // failing, and either way it must not stop us from migrating.
            try {
                this.db.exec("PRAGMA journal_mode = WAL");
            } catch {
                /* not a file database */
            }
        }

        this.db.exec(`
            CREATE TABLE IF NOT EXISTS ${this.checkpoints} (
              thread_id TEXT NOT NULL,
              id        TEXT NOT NULL,
              step      INTEGER NOT NULL,
              data      TEXT NOT NULL,
              PRIMARY KEY (thread_id, id)
            )`);

        this.db.exec(`
            CREATE TABLE IF NOT EXISTS ${this.journals} (
              task_id TEXT PRIMARY KEY,
              entries TEXT NOT NULL
            )`);

        this.migrated = true;
    }

    private ensureMigrated(): void {
        if (this.migrated) return;
        throw new Error(
            "SqliteCheckpointer: call await cp.migrate() once before use (or build it with " +
                "SqliteCheckpointer.open(), which migrates for you).",
        );
    }

    async put(checkpoint: Checkpoint): Promise<void> {
        this.ensureMigrated();
        // The full checkpoint is the source of truth in `data`; thread_id/id/step
        // are columns only for the WHERE and ORDER BY below.
        this.db
            .prepare(
                `INSERT INTO ${this.checkpoints} (thread_id, id, step, data)
                 VALUES (?, ?, ?, ?)
                 ON CONFLICT(thread_id, id) DO UPDATE SET step = excluded.step, data = excluded.data`,
            )
            .run(checkpoint.threadId, checkpoint.id, checkpoint.step, JSON.stringify(checkpoint));
    }

    async get(threadId: string, checkpointId?: string | null): Promise<Checkpoint | null> {
        this.ensureMigrated();
        const row = (
            checkpointId
                ? this.db
                      .prepare(`SELECT data FROM ${this.checkpoints} WHERE thread_id = ? AND id = ?`)
                      .get(threadId, checkpointId)
                : this.db
                      // Ids are lexically monotonic (MODEL.md §7), so max id = latest.
                      .prepare(
                          `SELECT data FROM ${this.checkpoints} WHERE thread_id = ? ORDER BY id DESC LIMIT 1`,
                      )
                      .get(threadId)
        ) as CheckpointRow | undefined;

        return row ? (JSON.parse(row.data) as Checkpoint) : null;
    }

    async list(threadId: string, opts: { limit?: number } = {}): Promise<Checkpoint[]> {
        this.ensureMigrated();
        const rows = (
            opts.limit === undefined
                ? this.db
                      .prepare(`SELECT data FROM ${this.checkpoints} WHERE thread_id = ? ORDER BY id DESC`)
                      .all(threadId)
                : this.db
                      .prepare(
                          `SELECT data FROM ${this.checkpoints} WHERE thread_id = ? ORDER BY id DESC LIMIT ?`,
                      )
                      .all(threadId, opts.limit)
        ) as CheckpointRow[];

        return rows.map((r) => JSON.parse(r.data) as Checkpoint);
    }

    async putJournal(taskId: string, journal: Journal): Promise<void> {
        this.ensureMigrated();
        this.db
            .prepare(
                `INSERT INTO ${this.journals} (task_id, entries)
                 VALUES (?, ?)
                 ON CONFLICT(task_id) DO UPDATE SET entries = excluded.entries`,
            )
            .run(taskId, JSON.stringify(journal.dump()));
    }

    async getJournal(taskId: string): Promise<Journal> {
        this.ensureMigrated();
        const row = this.db
            .prepare(`SELECT entries FROM ${this.journals} WHERE task_id = ?`)
            .get(taskId) as JournalRow | undefined;

        return row ? Journal.load(JSON.parse(row.entries)) : new Journal();
    }

    async dropJournal(taskId: string): Promise<void> {
        this.ensureMigrated();
        this.db.prepare(`DELETE FROM ${this.journals} WHERE task_id = ?`).run(taskId);
    }

    async deleteThread(threadId: string): Promise<void> {
        this.ensureMigrated();
        this.db.prepare(`DELETE FROM ${this.checkpoints} WHERE thread_id = ?`).run(threadId);
    }

    /** Close the underlying database, when this instance owns it. */
    close(): void {
        (this.db as { close?: () => void }).close?.();
    }
}
