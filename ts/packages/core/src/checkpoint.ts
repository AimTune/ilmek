import { randomBytes } from "node:crypto";
import { Journal, type JournalDump } from "./journal.ts";

/**
 * One task scheduled for the next superstep (MODEL.md §4, §14).
 *
 * A plain task (`isSend: false`) reads the checkpoint's channel state and its
 * `taskKey` is just the node name — so its task id, and therefore its journal,
 * is unchanged from a graph with no fan-out. A send task (`isSend: true`) reads
 * `input` as its whole state and gets a disambiguated `taskKey` (`node#n`) so N
 * sends to one node never share a journal.
 */
export interface ScheduledTask {
    readonly node: string;
    /** Stable id fragment: the node name, or `node#n` for the nth send to it. */
    readonly taskKey: string;
    readonly isSend: boolean;
    /** The node's input state — meaningful only when `isSend` is true. */
    readonly input?: unknown;
}

/** An interrupt a thread is parked on (MODEL.md §7). */
export interface Pending {
    /**
     * Thread-scoped handle — `"<node>:<key>"`. This is what `resumeKeyed()`
     * answers by.
     *
     * `key` alone is NOT enough: it is unique only within its own task, so two
     * nodes pausing in the same superstep both produce `interrupt#0` and a map
     * keyed by `key` would silently drop one answer.
     */
    readonly id: string;
    readonly taskId: string;
    readonly node: string;
    /** The journal key, unique within `taskId`. */
    readonly key: string;
    readonly payload: unknown;
}

/**
 * The per-thread record of channel values plus what runs next (MODEL.md §7).
 *
 * Every checkpoint names its parent, so a thread is a **tree**, not a line:
 * resuming from a non-latest checkpoint forks a branch. Nothing here may assume
 * a single chain.
 */
export interface Checkpoint {
    readonly id: string;
    readonly parentId: string | null;
    /**
     * The checkpoint `next` was planned from. Task ids derive from it, so it
     * MUST survive an interrupt/resume cycle unchanged — otherwise a replayed
     * task looks up a journal that does not exist and re-runs every side effect
     * it already performed.
     */
    readonly planId: string | null;
    readonly threadId: string;
    readonly channels: Readonly<Record<string, unknown>>;
    readonly next: readonly ScheduledTask[];
    readonly pending: readonly Pending[];
    readonly step: number;
    readonly ts: number;
}

// Checkpoint ids sort lexically, so a backend can range-scan a thread's history
// without a side index. The counter breaks ties: Date.now() has millisecond
// resolution and a superstep can easily finish inside one.
let seq = 0;

export function generateCheckpointId(): string {
    const micros = String(Date.now() * 1000).padStart(20, "0");
    const tiebreak = String(seq++ % 1_000_000).padStart(6, "0");
    return `ckpt-${micros}-${tiebreak}-${randomBytes(5).toString("base64url")}`;
}

export function isInterrupted(checkpoint: Checkpoint): boolean {
    return checkpoint.pending.length > 0;
}

/**
 * The memory port (MODEL.md §7) — one interface, many backends.
 *
 * Ilmek checkpoints are ilmek's own. They are **not** botiva state: botiva keeps
 * its transcript and `conv:*` keyspace independently.
 */
export interface Checkpointer {
    put(checkpoint: Checkpoint): Promise<void>;
    /** `checkpointId` omitted or null means "latest". */
    get(threadId: string, checkpointId?: string | null): Promise<Checkpoint | null>;
    list(threadId: string, opts?: { limit?: number }): Promise<Checkpoint[]>;
    putJournal(taskId: string, journal: Journal): Promise<void>;
    getJournal(taskId: string): Promise<Journal>;
    dropJournal(taskId: string): Promise<void>;
    deleteThread(threadId: string): Promise<void>;
}

/**
 * Reference checkpointer backed by plain Maps (MODEL.md §7).
 *
 * Fine for dev, tests and single-process work; use a durable backend when a
 * restart must not lose parked interrupts.
 */
export class InMemoryCheckpointer implements Checkpointer {
    private readonly checkpoints = new Map<string, Map<string, Checkpoint>>();
    private readonly journals = new Map<string, JournalDump>();

    async put(checkpoint: Checkpoint): Promise<void> {
        let thread = this.checkpoints.get(checkpoint.threadId);
        if (!thread) {
            thread = new Map();
            this.checkpoints.set(checkpoint.threadId, thread);
        }
        thread.set(checkpoint.id, checkpoint);
    }

    async get(threadId: string, checkpointId?: string | null): Promise<Checkpoint | null> {
        const thread = this.checkpoints.get(threadId);
        if (!thread) return null;
        if (checkpointId) return thread.get(checkpointId) ?? null;

        // Ids are lexically monotonic, so the max id IS the latest.
        let latest: Checkpoint | null = null;
        for (const ckpt of thread.values()) {
            if (!latest || ckpt.id > latest.id) latest = ckpt;
        }
        return latest;
    }

    async list(threadId: string, opts: { limit?: number } = {}): Promise<Checkpoint[]> {
        const all = [...(this.checkpoints.get(threadId)?.values() ?? [])].sort((a, b) =>
            a.id < b.id ? 1 : a.id > b.id ? -1 : 0,
        );
        return opts.limit === undefined ? all : all.slice(0, opts.limit);
    }

    async putJournal(taskId: string, journal: Journal): Promise<void> {
        this.journals.set(taskId, journal.dump());
    }

    async getJournal(taskId: string): Promise<Journal> {
        const dump = this.journals.get(taskId);
        return dump ? Journal.load(dump) : new Journal();
    }

    async dropJournal(taskId: string): Promise<void> {
        this.journals.delete(taskId);
    }

    async deleteThread(threadId: string): Promise<void> {
        this.checkpoints.delete(threadId);
    }
}
