/**
 * Interrupt / resume edge cases (MODEL.md §6): the ways a caller can answer a
 * pause wrongly, twice, partially or after a failure — and the guarantee that
 * none of them corrupts the thread or re-runs a journaled effect.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    channel,
    END,
    graph,
    InMemoryCheckpointer,
    pendingInterrupts,
    resume,
    resumeKeyed,
    resumeKeyedStream,
    ResumeError,
    run,
    START,
    threadState,
    type NodeFn,
} from "../src/index.ts";

function linear(fn: NodeFn<any>) {
    return graph("linear")
        .channel("log", channel.append<unknown>())
        .node("work", fn)
        .edge(START, "work")
        .edge("work", END)
        .compile();
}

/** Two nodes that pause in the same superstep, each logging its own answer. */
function twoPauses() {
    return graph("two")
        .channel("log", channel.append<string>())
        .node("a", async (_s, ctx) => ({ log: [`a=${await ctx.interrupt<string>({ q: "a?" })}`] }))
        .node("b", async (_s, ctx) => ({ log: [`b=${await ctx.interrupt<string>({ q: "b?" })}`] }))
        .edge(START, "a")
        .edge(START, "b")
        .edge("a", END)
        .edge("b", END)
        .compile();
}

function counter() {
    let n = 0;
    return { bump: () => ++n, get count() { return n; } };
}

describe("resumeKeyed — partial and wrong answers", () => {
    test("answering only some of several pauses is refused and names the missing id", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-partial", checkpointer };
        const g = twoPauses();

        assert.equal((await run(g, {}, opts)).status, "interrupted");

        await assert.rejects(
            () => resumeKeyed(g, { "a:interrupt#0": "yes" }, opts),
            (e: unknown) => e instanceof ResumeError && /"b:interrupt#0"/.test(e.message),
        );
    });

    test("a refused partial resume writes nothing, so the full answer still completes the thread", async () => {
        // Regression: answers used to be persisted one at a time, so the partial
        // resume above committed a's answer before failing on b — and the
        // corrected resume was then refused with `already_answered`, forever.
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-partial-retry", checkpointer };
        const g = twoPauses();

        await run(g, {}, opts);
        await assert.rejects(() => resumeKeyed(g, { "a:interrupt#0": "first-try" }, opts), ResumeError);

        const done = await resumeKeyed(g, { "a:interrupt#0": "A", "b:interrupt#0": "B" }, opts);
        assert.equal(done.status, "done");
        assert.deepEqual(done.state?.log, ["a=A", "b=B"]);
    });

    test("an answer keyed by the task-scoped key instead of the id is refused", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-by-key", checkpointer };
        const g = linear(async (_s, ctx) => ({ log: [await ctx.interrupt({ q: "?" })] }));

        await run(g, {}, opts);
        await assert.rejects(() => resumeKeyed(g, { "interrupt#0": "yes" }, opts), /work:interrupt#0/);

        // The refusal left the pause intact and answerable by its real id.
        assert.equal((await pendingInterrupts(checkpointer, "t-by-key")).length, 1);
        const done = await resumeKeyed(g, { "work:interrupt#0": "yes" }, opts);
        assert.deepEqual(done.state?.log, ["yes"]);
    });

    test("an empty answer map is refused while a pause is open", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-empty", checkpointer };
        const g = linear(async (_s, ctx) => ({ log: [await ctx.interrupt()] }));

        await run(g, {}, opts);
        await assert.rejects(() => resumeKeyed(g, {}, opts), /no answer supplied/);
    });

    test("answers for ids that are not pending are ignored alongside the right one", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-extra", checkpointer };
        const g = linear(async (_s, ctx) => ({ log: [await ctx.interrupt()] }));

        await run(g, {}, opts);
        const done = await resumeKeyed(g, { "work:interrupt#0": "real", "ghost:interrupt#0": "noise" }, opts);
        assert.equal(done.status, "done");
        assert.deepEqual(done.state?.log, ["real"]);
    });

    test("a Map of answers is accepted as well as a plain object", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-map", checkpointer };
        const g = twoPauses();

        await run(g, {}, opts);
        const done = await resumeKeyed(
            g,
            new Map([
                ["b:interrupt#0", "2"],
                ["a:interrupt#0", "1"],
            ]),
            opts,
        );
        assert.deepEqual(done.state?.log, ["a=1", "b=2"]);
    });

    test("a refused resume is raised before the stream yields anything", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-stream-refused", checkpointer };
        const g = twoPauses();
        await run(g, {}, opts);

        const it = resumeKeyedStream(g, { "a:interrupt#0": "only-a" }, opts);
        await assert.rejects(() => it.next(), ResumeError);
    });
});

describe("resume — falsy and structured answers", () => {
    for (const answer of [null, 0, false, "", [], { nested: { deep: [1, 2] } }]) {
        test(`the answer ${JSON.stringify(answer)} reaches the node verbatim`, async () => {
            const checkpointer = new InMemoryCheckpointer();
            const opts = { threadId: `t-falsy-${JSON.stringify(answer)}`, checkpointer };
            const g = linear(async (_s, ctx) => ({ log: [await ctx.interrupt()] }));

            await run(g, {}, opts);
            const done = await resume(g, answer, opts);
            assert.equal(done.status, "done");
            assert.deepEqual(done.state?.log, [answer]);
        });
    }

    test("an undefined answer is a real answer, not a missing one", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-undefined", checkpointer };
        const seen: unknown[] = [];
        const g = linear(async (_s, ctx) => {
            seen.push(await ctx.interrupt());
            return {};
        });

        await run(g, {}, opts);
        assert.equal((await resume(g, undefined, opts)).status, "done");
        assert.deepEqual(seen, [undefined]);
    });
});

describe("resume — twice, too early, too late", () => {
    test("a second resume of an already-resumed thread is refused and re-runs nothing", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-double", checkpointer };
        const effects = counter();
        const g = linear(async (_s, ctx) => {
            await ctx.step("effect", () => effects.bump());
            return { log: [await ctx.interrupt()] };
        });

        await run(g, {}, opts);
        assert.equal((await resume(g, "once", opts)).status, "done");
        await assert.rejects(() => resume(g, "twice", opts), /not interrupted/);

        assert.equal(effects.count, 1);
        assert.deepEqual((await threadState(g, checkpointer, "t-double"))?.log, ["once"]);
    });

    test("resuming a thread that finished without ever pausing is refused", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-never-paused", checkpointer };
        const g = linear(() => ({ log: ["done"] }));

        await run(g, {}, opts);
        await assert.rejects(() => resumeKeyed(g, {}, opts), /not interrupted/);
    });

    test("resume without a checkpointer is refused, it does not run the graph fresh", async () => {
        let ran = false;
        const g = linear(() => {
            ran = true;
            return {};
        });

        await assert.rejects(() => resume(g, "yes", { threadId: "t-none" }), /no checkpoint/);
        assert.equal(ran, false);
    });

    test("resuming one thread never answers a pause parked on another", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const g = linear(async (_s, ctx) => ({ log: [await ctx.interrupt()] }));

        await run(g, {}, { threadId: "t-left", checkpointer });
        await run(g, {}, { threadId: "t-right", checkpointer });
        await resume(g, "left-answer", { threadId: "t-left", checkpointer });

        assert.equal((await pendingInterrupts(checkpointer, "t-right")).length, 1);
        const right = await resume(g, "right-answer", { threadId: "t-right", checkpointer });
        assert.deepEqual(right.state?.log, ["right-answer"]);
    });
});

describe("resume — a resumed superstep that does not commit", () => {
    // Regression: the answer is journaled before the superstep runs. If that
    // superstep then failed or was aborted, the checkpoint still showed the
    // pause, but every later resume was refused with `already_answered` and
    // run() was refused because the thread was parked — stuck for good.

    test("after the resumed node throws, resuming again with the same answer completes", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-resume-crash", checkpointer };
        const effects = counter();
        let crash = true;

        const g = linear(async (_s, ctx) => {
            await ctx.step("before", () => effects.bump());
            const answer = await ctx.interrupt<string>({ q: "?" });
            if (crash) {
                crash = false;
                throw new Error("downstream outage");
            }
            return { log: [answer] };
        });

        await run(g, {}, opts);
        const failed = await resume(g, "yes", opts);
        assert.equal(failed.status, "error");
        assert.equal((await pendingInterrupts(checkpointer, "t-resume-crash")).length, 1);

        const done = await resume(g, "yes", opts);
        assert.equal(done.status, "done");
        assert.deepEqual(done.state?.log, ["yes"]);
        assert.equal(effects.count, 1);
    });

    test("a step that ran after the answer is not repeated by the retried resume", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-after-answer", checkpointer };
        const charges = counter();
        let crash = true;

        const g = linear(async (_s, ctx) => {
            const answer = await ctx.interrupt<string>();
            await ctx.step("charge", () => charges.bump());
            if (crash) {
                crash = false;
                throw new Error("crash after charging");
            }
            return { log: [answer] };
        });

        await run(g, {}, opts);
        assert.equal((await resume(g, "go", opts)).status, "error");
        assert.equal((await resume(g, "go", opts)).status, "done");
        assert.equal(charges.count, 1);
    });

    test("after an aborted resume, resuming again with the same answer completes", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-resume-abort", checkpointer };
        const g = linear(async (_s, ctx) => ({ log: [await ctx.interrupt<string>()] }));

        await run(g, {}, opts);
        const ac = new AbortController();
        ac.abort("user went away");
        const aborted = await resume(g, "yes", { ...opts, signal: ac.signal });
        assert.equal(aborted.status, "aborted");
        assert.equal(aborted.abortReason, "user went away");

        const done = await resume(g, "yes", opts);
        assert.equal(done.status, "done");
        assert.deepEqual(done.state?.log, ["yes"]);
    });

    test("a different answer than the one already recorded is refused, the original still works", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-changed-mind", checkpointer };
        let crash = true;
        const g = linear(async (_s, ctx) => {
            const answer = await ctx.interrupt<{ approve: boolean }>();
            if (crash) {
                crash = false;
                throw new Error("boom");
            }
            return { log: [answer] };
        });

        await run(g, {}, opts);
        assert.equal((await resume(g, { approve: true }, opts)).status, "error");

        await assert.rejects(
            () => resume(g, { approve: false }, opts),
            (e: unknown) => e instanceof ResumeError && /already answered/.test(e.message),
        );

        // Structurally equal (not identical) answers count as the same answer.
        const done = await resume(g, { approve: true }, opts);
        assert.deepEqual(done.state?.log, [{ approve: true }]);
    });

    test("a retried keyed resume may re-answer one pause and newly answer another", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-mixed-retry", checkpointer };
        let crashA = true;
        const g = graph("mixed")
            .channel("log", channel.append<string>())
            .node("a", async (_s, ctx) => {
                const ans = await ctx.interrupt<string>();
                if (crashA) {
                    crashA = false;
                    throw new Error("a fails once");
                }
                return { log: [`a=${ans}`] };
            })
            .node("b", async (_s, ctx) => ({ log: [`b=${await ctx.interrupt<string>()}`] }))
            .edge(START, "a")
            .edge(START, "b")
            .edge("a", END)
            .edge("b", END)
            .compile();

        await run(g, {}, opts);
        const answers = { "a:interrupt#0": "1", "b:interrupt#0": "2" };
        assert.equal((await resumeKeyed(g, answers, opts)).status, "error");
        const done = await resumeKeyed(g, answers, opts);
        assert.equal(done.status, "done");
        assert.deepEqual(done.state?.log, ["a=1", "b=2"]);
    });
});

describe("concurrent resumes of one pause", () => {
    test("the effect journaled before the pause still runs exactly once", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-race", checkpointer };
        const effects = counter();
        const g = linear(async (_s, ctx) => {
            await ctx.step("pre", () => effects.bump());
            return { log: [await ctx.interrupt<string>()] };
        });

        await run(g, {}, opts);
        const settled = await Promise.allSettled([resume(g, "same", opts), resume(g, "same", opts)]);

        assert.ok(settled.some((s) => s.status === "fulfilled" && s.value.status === "done"));
        assert.equal(effects.count, 1);
        assert.deepEqual(await pendingInterrupts(checkpointer, "t-race"), []);
    });
});
