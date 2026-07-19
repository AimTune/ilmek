/** The replay memory of MODEL.md §5 — Journal and TaskJournal, at the unit level. */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import { Journal, TaskJournal } from "../src/journal.ts";
import { NondeterminismError } from "../src/errors.ts";

describe("Journal", () => {
    test("a fresh journal is empty", () => {
        const j = new Journal();
        assert.deepEqual(j.keys(), []);
        assert.equal(j.fetch("charge#0"), undefined);
        assert.deepEqual(j.pending(), []);
    });

    test("putDone records a value, including undefined", () => {
        const j = new Journal();
        j.putDone("charge#0", { id: "ch_1" });
        j.putDone("log#0", undefined);

        assert.deepEqual(j.fetch("charge#0"), { status: "done", value: { id: "ch_1" } });
        // A step that returns nothing is still journaled — otherwise replay
        // would re-run it.
        assert.deepEqual(j.fetch("log#0"), { status: "done", value: undefined });
    });

    test("putDone overwrites an earlier entry for the same key", () => {
        const j = new Journal();
        j.putDone("k#0", 1);
        j.putDone("k#0", 2);

        assert.deepEqual(j.keys(), ["k#0"]);
        assert.deepEqual(j.fetch("k#0"), { status: "done", value: 2 });
    });

    test("pending() lists unanswered interrupts in journaled order", () => {
        const j = new Journal();
        j.putPending("approve#0", { amount: 10 });
        j.putDone("charge#0", "ok");
        j.putPending("approve#1", { amount: 20 });

        assert.deepEqual(j.pending(), [
            { key: "approve#0", payload: { amount: 10 } },
            { key: "approve#1", payload: { amount: 20 } },
        ]);
    });

    test("keys() preserves insertion order", () => {
        const j = new Journal();
        j.putDone("b#0", 1);
        j.putDone("a#0", 2);
        j.putPending("c#0", 3);

        assert.deepEqual(j.keys(), ["b#0", "a#0", "c#0"]);
    });
});

describe("Journal.answer", () => {
    test("resolves a pending interrupt into a done entry", () => {
        const j = new Journal();
        j.putPending("approve#0", { amount: 10 });

        assert.deepEqual(j.answer("approve#0", "yes"), { ok: true });
        assert.deepEqual(j.fetch("approve#0"), { status: "done", value: "yes" });
        assert.deepEqual(j.pending(), []);
    });

    test("refuses an unknown key", () => {
        assert.deepEqual(new Journal().answer("nope#0", "yes"), {
            ok: false,
            reason: "unknown_key",
        });
    });

    test("refuses to answer twice", () => {
        const j = new Journal();
        j.putPending("approve#0", null);
        j.answer("approve#0", "yes");

        assert.deepEqual(j.answer("approve#0", "no"), { ok: false, reason: "already_answered" });
        // The first answer stands.
        assert.deepEqual(j.fetch("approve#0"), { status: "done", value: "yes" });
    });

    test("answering does not reorder the entry", () => {
        const j = new Journal();
        j.putPending("a#0", null);
        j.putDone("b#0", 1);
        j.answer("a#0", "yes");

        assert.deepEqual(j.keys(), ["a#0", "b#0"]);
    });
});

describe("Journal dump/load", () => {
    test("round-trips entries, order and status", () => {
        const j = new Journal();
        j.putDone("charge#0", { id: "ch_1" });
        j.putPending("approve#0", { amount: 10 });

        const back = Journal.load(j.dump());

        assert.deepEqual(back.dump(), j.dump());
        assert.deepEqual(back.keys(), ["charge#0", "approve#0"]);
        assert.deepEqual(back.pending(), [{ key: "approve#0", payload: { amount: 10 } }]);
    });

    test("a loaded journal is independent of the original", () => {
        const j = new Journal();
        j.putDone("a#0", 1);

        const back = Journal.load(j.dump());
        back.putDone("b#0", 2);

        assert.deepEqual(j.keys(), ["a#0"]);
        assert.deepEqual(back.keys(), ["a#0", "b#0"]);
    });

    test("an empty dump loads to an empty journal", () => {
        assert.deepEqual(Journal.load([]).keys(), []);
    });
});

describe("TaskJournal.resolveKey", () => {
    test("suffixes uniformly from zero, even for a key used once", () => {
        const tj = new TaskJournal(new Journal());
        assert.equal(tj.resolveKey("charge"), "charge#0");
    });

    test("counts occurrences per base key", () => {
        const tj = new TaskJournal(new Journal());

        assert.equal(tj.resolveKey("charge"), "charge#0");
        assert.equal(tj.resolveKey("email"), "email#0");
        assert.equal(tj.resolveKey("charge"), "charge#1");
        assert.equal(tj.resolveKey("charge"), "charge#2");
        assert.equal(tj.resolveKey("email"), "email#1");
    });

    test("counters are per task, so a fresh pass restarts the sequence", () => {
        const journal = new Journal();
        const first = new TaskJournal(journal);
        first.resolveKey("charge");
        first.resolveKey("charge");

        // A replay of the same task builds a new TaskJournal over the same journal.
        const replay = new TaskJournal(journal);
        assert.equal(replay.resolveKey("charge"), "charge#0");
    });
});

describe("TaskJournal.checkDeterminism", () => {
    test("passes when the replay requested every journaled key", () => {
        const journal = new Journal();
        journal.putDone("charge#0", "ok");
        journal.putDone("email#0", "ok");

        const tj = new TaskJournal(journal);
        tj.resolveKey("charge");
        tj.resolveKey("email");

        assert.doesNotThrow(() => tj.checkDeterminism("pay"));
    });

    test("passes on an empty journal", () => {
        assert.doesNotThrow(() => new TaskJournal(new Journal()).checkDeterminism("pay"));
    });

    test("passes when the replay requests MORE keys than are journaled", () => {
        // Progress: the node got past where it paused and is doing new work.
        const journal = new Journal();
        journal.putDone("charge#0", "ok");

        const tj = new TaskJournal(journal);
        tj.resolveKey("charge");
        tj.resolveKey("ship");

        assert.doesNotThrow(() => tj.checkDeterminism("pay"));
    });

    test("throws naming the node and every missing key", () => {
        const journal = new Journal();
        journal.putDone("charge#0", "ok");
        journal.putDone("email#0", "ok");

        const tj = new TaskJournal(journal);
        tj.resolveKey("charge"); // took a different branch: email never requested

        assert.throws(
            () => tj.checkDeterminism("pay"),
            (err: unknown) => {
                assert.ok(err instanceof NondeterminismError);
                assert.equal(err.name, "NondeterminismError");
                assert.match(err.message, /"pay"/);
                assert.match(err.message, /email#0/);
                assert.doesNotMatch(err.message, /charge#0/);
                return true;
            },
        );
    });

    test("a shifted ordinal counts as missing", () => {
        // Journaled charge#0 and charge#1; this pass only asked once.
        const journal = new Journal();
        journal.putDone("charge#0", "ok");
        journal.putDone("charge#1", "ok");

        const tj = new TaskJournal(journal);
        tj.resolveKey("charge");

        assert.throws(() => tj.checkDeterminism("pay"), /charge#1/);
    });
});
