import { NondeterminismError } from "./errors.ts";

/**
 * A journal entry (MODEL.md §5).
 *
 * An interrupt is a step whose value comes from a human (MODEL.md §6), so both
 * live in this one table: `done` covers a completed step *and* an answered
 * interrupt; `pending` is an interrupt still waiting on a person.
 */
export type JournalEntry =
    | { readonly status: "done"; readonly value: unknown }
    | { readonly status: "pending"; readonly payload: unknown };

export type JournalDump = ReadonlyArray<readonly [string, JournalEntry]>;

export interface PendingEntry {
    readonly key: string;
    readonly payload: unknown;
}

/**
 * The per-task record of completed steps and interrupt answers (MODEL.md §5).
 *
 * This is the reason ilmek is not a LangGraph clone. A pure-replay engine
 * re-executes a node from the top on resume, so every side effect before the
 * pause happens twice. Here each effect is wrapped in a `ctx.step()` whose
 * result is journaled — on replay the step returns the recorded value and the
 * function is never called again.
 *
 * A journal is scoped to a task (`{thread, checkpoint, node}`) and is dropped
 * once that task's update is reduced. It is replay memory, not history.
 */
export class Journal {
    // A Map preserves insertion order, so journaled order comes for free.
    private readonly entries: Map<string, JournalEntry>;

    constructor(entries: Map<string, JournalEntry> = new Map()) {
        this.entries = entries;
    }

    static load(dump: JournalDump): Journal {
        return new Journal(new Map(dump.map(([k, e]) => [k, e])));
    }

    dump(): JournalDump {
        return [...this.entries.entries()];
    }

    keys(): string[] {
        return [...this.entries.keys()];
    }

    fetch(key: string): JournalEntry | undefined {
        return this.entries.get(key);
    }

    putDone(key: string, value: unknown): void {
        this.entries.set(key, { status: "done", value });
    }

    putPending(key: string, payload: unknown): void {
        this.entries.set(key, { status: "pending", payload });
    }

    /** Resolve a pending interrupt with the human's answer. */
    answer(key: string, value: unknown): { ok: true } | { ok: false; reason: string } {
        const entry = this.entries.get(key);
        if (!entry) return { ok: false, reason: "unknown_key" };
        if (entry.status === "done") return { ok: false, reason: "already_answered" };
        this.entries.set(key, { status: "done", value });
        return { ok: true };
    }

    /** Every interrupt still waiting on a human, in journaled order. */
    pending(): PendingEntry[] {
        const out: PendingEntry[] = [];
        for (const [key, entry] of this.entries) {
            if (entry.status === "pending") out.push({ key, payload: entry.payload });
        }
        return out;
    }
}

/**
 * Per-task bookkeeping around a journal: occurrence counters for key
 * suffixing and the trace strict mode checks (MODEL.md §5.4, §5.5).
 *
 * In Elixir this state lives in the task process's dictionary; in TypeScript a
 * task is just an async call tree, so it lives on this object — which the
 * task's `ctx` closes over. Same lifetime, no ambient storage required.
 */
export class TaskJournal {
    readonly journal: Journal;
    private readonly counters = new Map<string, number>();
    private readonly observed: string[] = [];

    constructor(journal: Journal) {
        this.journal = journal;
    }

    /**
     * Turn a caller-supplied base key into the full journal key by appending
     * this pass's occurrence count for that base: `"charge"` → `"charge#0"`,
     * `"charge#1"`, …
     *
     * Suffixing is uniform (even for keys used once) so the mapping depends only
     * on how many times *this* base has been requested in *this* pass. Since a
     * node sees identical state on every pass the sequence is stable, and a node
     * that branches around a step only shifts the ordinals of that same base key.
     */
    resolveKey(base: string): string {
        const n = this.counters.get(base) ?? 0;
        this.counters.set(base, n + 1);
        const full = `${base}#${n}`;
        this.observed.push(full);
        return full;
    }

    /**
     * Strict-mode check (MODEL.md §5.5): every key the journal holds must have
     * been requested again on this pass. A key that vanishes means the node took
     * a different path than it did before — a side effect escaped a step.
     */
    checkDeterminism(node: string): void {
        const seen = new Set(this.observed);
        const missing = this.journal.keys().filter((k) => !seen.has(k));
        if (missing.length === 0) return;

        throw new NondeterminismError(
            `node ${JSON.stringify(node)} replayed without requesting journaled step(s) ` +
                `${JSON.stringify(missing)}.\n\n` +
                `The node body is not deterministic modulo steps: given the same state and ` +
                `journal it took a different path. Wrap every side effect and every ` +
                `nondeterministic read (clock, RNG, uuid, network) in ctx.step() — see ` +
                `MODEL.md §5.2.`,
        );
    }
}
