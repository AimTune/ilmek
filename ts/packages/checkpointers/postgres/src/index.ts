// A Postgres-backed Checkpointer for ilmek (MODEL.md §7) — durable threads that
// survive a restart, so a parked interrupt or a resumable run outlives the
// process that created it.
//
// It keeps core's zero-dependency rule by not importing `pg`: it talks to a
// **duck-typed** client — anything with `query(text, params) => { rows }`, which
// node-postgres' Client and Pool both satisfy. Bring your own connection; this
// package never opens or closes it.
//
// Two tables. Checkpoints are the per-thread record (id sorts lexically, so
// "latest" and history are plain ORDER BY id); journals are the per-task replay
// memory (MODEL.md §5), dropped as each superstep commits.

import { Journal, type Checkpoint, type Checkpointer } from "@ilmek/core";

/** The slice of a node-postgres client this checkpointer needs. `pg` satisfies it as-is. */
export interface SqlClient {
    query(text: string, params?: readonly unknown[]): Promise<{ rows: unknown[] }>;
}

export interface PostgresCheckpointerOptions {
    /** Table-name prefix, so several apps can share a database. Default `"ilmek"`. */
    readonly tablePrefix?: string;
}

// Each statement is tagged with a leading comment. It is inert in Postgres but
// lets a test double dispatch on intent without parsing SQL (see the test).
const TAG = {
    createCheckpoints: "ilmek:create_checkpoints",
    createJournals: "ilmek:create_journals",
    putCheckpoint: "ilmek:put_checkpoint",
    getLatest: "ilmek:get_latest",
    getById: "ilmek:get_by_id",
    list: "ilmek:list",
    putJournal: "ilmek:put_journal",
    getJournal: "ilmek:get_journal",
    dropJournal: "ilmek:drop_journal",
    deleteThread: "ilmek:delete_thread",
} as const;

interface CheckpointRow {
    data: string | Record<string, unknown>;
}
interface JournalRow {
    entries: string | unknown[];
}

/**
 * A `Checkpointer` (MODEL.md §7) stored in Postgres.
 *
 *     const cp = new PostgresCheckpointer(pgPool);
 *     await cp.migrate();                 // once, or run the DDL yourself
 *     await run(graph, input, { threadId, checkpointer: cp });
 */
export class PostgresCheckpointer implements Checkpointer {
    private readonly db: SqlClient;
    private readonly checkpoints: string;
    private readonly journals: string;

    constructor(db: SqlClient, opts: PostgresCheckpointerOptions = {}) {
        this.db = db;
        const prefix = opts.tablePrefix ?? "ilmek";
        this.checkpoints = `${prefix}_checkpoints`;
        this.journals = `${prefix}_journals`;
    }

    /** Create the two tables if absent. Idempotent; safe to call on every boot. */
    async migrate(): Promise<void> {
        await this.db.query(
            `/* ${TAG.createCheckpoints} */
             CREATE TABLE IF NOT EXISTS ${this.checkpoints} (
               thread_id text NOT NULL,
               id        text NOT NULL,
               step      integer NOT NULL,
               data      jsonb NOT NULL,
               PRIMARY KEY (thread_id, id)
             )`,
        );
        await this.db.query(
            `/* ${TAG.createJournals} */
             CREATE TABLE IF NOT EXISTS ${this.journals} (
               task_id text PRIMARY KEY,
               entries jsonb NOT NULL
             )`,
        );
    }

    async put(checkpoint: Checkpoint): Promise<void> {
        // The full checkpoint is the source of truth in `data`; thread_id/id/step
        // are columns only for the WHERE and ORDER BY below.
        await this.db.query(
            `/* ${TAG.putCheckpoint} */
             INSERT INTO ${this.checkpoints} (thread_id, id, step, data)
             VALUES ($1, $2, $3, $4)
             ON CONFLICT (thread_id, id) DO UPDATE SET step = EXCLUDED.step, data = EXCLUDED.data`,
            [checkpoint.threadId, checkpoint.id, checkpoint.step, JSON.stringify(checkpoint)],
        );
    }

    async get(threadId: string, checkpointId?: string | null): Promise<Checkpoint | null> {
        const { rows } = checkpointId
            ? await this.db.query(
                  `/* ${TAG.getById} */
                   SELECT data FROM ${this.checkpoints} WHERE thread_id = $1 AND id = $2`,
                  [threadId, checkpointId],
              )
            : await this.db.query(
                  // Ids are lexically monotonic (MODEL.md §7), so max id = latest.
                  `/* ${TAG.getLatest} */
                   SELECT data FROM ${this.checkpoints} WHERE thread_id = $1 ORDER BY id DESC LIMIT 1`,
                  [threadId],
              );
        const row = rows[0] as CheckpointRow | undefined;
        return row ? decode<Checkpoint>(row.data) : null;
    }

    async list(threadId: string, opts: { limit?: number } = {}): Promise<Checkpoint[]> {
        const limit = opts.limit;
        const { rows } = await this.db.query(
            `/* ${TAG.list} */
             SELECT data FROM ${this.checkpoints} WHERE thread_id = $1 ORDER BY id DESC` +
                (limit === undefined ? "" : ` LIMIT $2`),
            limit === undefined ? [threadId] : [threadId, limit],
        );
        return (rows as CheckpointRow[]).map((r) => decode<Checkpoint>(r.data));
    }

    async putJournal(taskId: string, journal: Journal): Promise<void> {
        await this.db.query(
            `/* ${TAG.putJournal} */
             INSERT INTO ${this.journals} (task_id, entries)
             VALUES ($1, $2)
             ON CONFLICT (task_id) DO UPDATE SET entries = EXCLUDED.entries`,
            [taskId, JSON.stringify(journal.dump())],
        );
    }

    async getJournal(taskId: string): Promise<Journal> {
        const { rows } = await this.db.query(
            `/* ${TAG.getJournal} */ SELECT entries FROM ${this.journals} WHERE task_id = $1`,
            [taskId],
        );
        const row = rows[0] as JournalRow | undefined;
        return row ? Journal.load(decode(row.entries)) : new Journal();
    }

    async dropJournal(taskId: string): Promise<void> {
        await this.db.query(
            `/* ${TAG.dropJournal} */ DELETE FROM ${this.journals} WHERE task_id = $1`,
            [taskId],
        );
    }

    async deleteThread(threadId: string): Promise<void> {
        await this.db.query(
            `/* ${TAG.deleteThread} */ DELETE FROM ${this.checkpoints} WHERE thread_id = $1`,
            [threadId],
        );
    }
}

/** The operation tags, exported so a test double can dispatch on them. */
export const SQL_TAGS = TAG;

// A `jsonb` column comes back parsed from node-postgres, but a test double or a
// text column may hand back a string — accept either.
function decode<T>(value: string | unknown): T {
    return (typeof value === "string" ? JSON.parse(value) : value) as T;
}
