/**
 * The conformance scenarios of MODEL.md §12 — the same list the Elixir
 * reference passes. These are not incidental unit tests; they are the arbiter of
 * the semantics. If a change makes one fail, either the change is wrong or
 * MODEL.md needs a major bump.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    channel,
    END,
    graph,
    GraphError,
    InMemoryCheckpointer,
    NondeterminismError,
    pendingInterrupts,
    ResumeError,
    resume,
    resumeKeyed,
    run,
    START,
    stream,
    type Context,
    type NodeFn,
} from "../src/index.ts";

// ── helpers ─────────────────────────────────────────────────────────────────

function counter() {
    const counts = new Map<string, number>();
    return {
        bump: (key: string) => counts.set(key, (counts.get(key) ?? 0) + 1),
        count: (key: string) => counts.get(key) ?? 0,
    };
}

function linear(name: string, fn: NodeFn<any>) {
    return graph(name)
        .channel("log", channel.append<string>())
        .channel("items", channel.lastWrite<string[]>([]))
        .node("work", fn)
        .edge(START, "work")
        .edge("work", END)
        .compile();
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

// ── basics ──────────────────────────────────────────────────────────────────

describe("basics", () => {
    test("a graph runs to completion without a checkpointer", async () => {
        const result = await run(linear("plain", () => ({ log: ["hi"] })));

        assert.equal(result.status, "done");
        assert.deepEqual(result.state?.log, ["hi"]);
    });

    test("reducers fold updates: append accumulates across supersteps", async () => {
        const g = graph("chain")
            .channel("log", channel.append<string>())
            .node("a", () => ({ log: ["a"] }))
            .node("b", () => ({ log: ["b"] }))
            .edge(START, "a")
            .edge("a", "b")
            .edge("b", END)
            .compile();

        assert.deepEqual((await run(g)).state?.log, ["a", "b"]);
    });

    test("a node sees the input folded through its channel's reducer", async () => {
        const g = linear("echo", (state) => ({ log: [`saw:${(state.log as string[])[0]}`] }));

        assert.deepEqual((await run(g, { log: ["in"] })).state?.log, ["in", "saw:in"]);
    });

    test("a node may return nothing", async () => {
        const g = linear("silent", () => undefined);

        assert.equal((await run(g)).status, "done");
    });

    test("a router picks the branch", async () => {
        const g = graph("route")
            .channel("log", channel.append<string>())
            .channel("intent", channel.lastWrite<string>())
            .node("classify", () => ({ intent: "buy" }))
            .node("buy", () => ({ log: ["bought"] }))
            .node("browse", () => ({ log: ["browsed"] }))
            .edge(START, "classify")
            .router("classify", (state) => (state.intent === "buy" ? "buy" : "browse"))
            .edge("buy", END)
            .edge("browse", END)
            .compile();

        assert.deepEqual((await run(g)).state?.log, ["bought"]);
    });

    test("writing an undeclared channel fails loudly, attributed to its node", async () => {
        const g = linear("typo", () => ({ lgo: ["oops"] }) as any);
        const result = await run(g);

        assert.equal(result.status, "error");
        const [node, error] = result.errors[0]!;
        assert.equal(node, "work");
        assert.ok(error instanceof GraphError);
        assert.match(error.message, /undeclared channel "lgo"/);
    });

    test("ctx.emit reaches the stream live, before the superstep settles", async () => {
        const g = linear("emitter", (_state, ctx) => {
            ctx.emit({ token: "hel" });
            ctx.emit({ token: "lo" });
            return { log: ["done"] };
        });

        const tokens: unknown[] = [];
        for await (const ev of stream(g)) {
            if (ev.type === "custom") tokens.push(ev.payload);
        }

        assert.deepEqual(tokens, [{ token: "hel" }, { token: "lo" }]);
    });

    test("interrupt without a checkpointer explains itself instead of hanging", async () => {
        const g = linear("no-cp", (_state, ctx) => ctx.interrupt({ q: "?" }));
        const result = await run(g);

        assert.equal(result.status, "error");
        const [, error] = result.errors[0]!;
        assert.ok(error instanceof ResumeError);
        assert.match(error.message, /needs a checkpointer/);
    });

    test("a guard cannot step or interrupt — it has no journal", async () => {
        const g = graph("impure-guard")
            .channel("log", channel.append<string>())
            .node("a", () => ({ log: ["a"] }))
            .node("b", () => ({ log: ["b"] }))
            .edge(START, "a")
            .edge("a", "b", { when: (_state, ctx: Context<any>) => Boolean(ctx.step("nope", () => 1)) })
            .edge("b", END)
            .compile();

        await assert.rejects(() => run(g), /not available in a router or guard/);
    });
});

// ── MODEL.md §12.1 ──────────────────────────────────────────────────────────

test("§12.1 a step executes exactly once across an interrupt/resume cycle", async () => {
    const checkpointer = new InMemoryCheckpointer();
    const calls = counter();

    const g = linear("checkout", async (_state, ctx) => {
        // The effect that must never happen twice.
        const order = await ctx.step("create_order", () => {
            calls.bump("create_order");
            return "order-1";
        });

        const answer = await ctx.interrupt<string>({ question: "Charge order?" });

        const charged = await ctx.step("charge", () => {
            calls.bump("charge");
            return `charged-${order}`;
        });

        return { log: [`${order}/${answer}/${charged}`] };
    });

    const opts = { threadId: "t-checkout", checkpointer };

    const paused = await run(g, {}, opts);
    assert.equal(paused.status, "interrupted");
    assert.equal(paused.pending[0]?.key, "interrupt#0");
    assert.equal(paused.pending[0]?.node, "work");
    assert.deepEqual(paused.pending[0]?.payload, { question: "Charge order?" });
    assert.equal(calls.count("create_order"), 1);
    // The pause gates the charge, so it has not happened yet.
    assert.equal(calls.count("charge"), 0);

    const done = await resume(g, "yes", opts);
    assert.equal(done.status, "done");
    assert.deepEqual(done.state?.log, ["order-1/yes/charged-order-1"]);

    // THE assertion this whole engine exists for: the node body re-ran from the
    // top, but create_order came back from the journal instead of opening a
    // second order. A pure-replay engine scores 2 here.
    assert.equal(calls.count("create_order"), 1);
    assert.equal(calls.count("charge"), 1);
});

// ── MODEL.md §12.2 ──────────────────────────────────────────────────────────

test("§12.2 two interrupts in one node resolve independently, one resume each", async () => {
    const checkpointer = new InMemoryCheckpointer();

    const g = linear("double", async (_state, ctx) => {
        const first = await ctx.interrupt<string>({ q: "first" });
        const second = await ctx.interrupt<string>({ q: "second" });
        return { log: [`${first}+${second}`] };
    });

    const opts = { threadId: "t-double", checkpointer };

    const p1 = await run(g, {}, opts);
    assert.equal(p1.pending[0]?.key, "interrupt#0");
    assert.deepEqual(p1.pending[0]?.payload, { q: "first" });

    // Replays: interrupt#0 returns "A" from the journal, interrupt#1 is new.
    const p2 = await resume(g, "A", opts);
    assert.equal(p2.status, "interrupted");
    assert.equal(p2.pending[0]?.key, "interrupt#1");
    assert.deepEqual(p2.pending[0]?.payload, { q: "second" });

    const done = await resume(g, "B", opts);
    assert.equal(done.status, "done");
    assert.deepEqual(done.state?.log, ["A+B"]);
});

// ── MODEL.md §12.3 ──────────────────────────────────────────────────────────

test("§12.3 an interrupt inside a loop with stable keys resumes at the right iteration", async () => {
    const checkpointer = new InMemoryCheckpointer();
    const calls = counter();

    const g = linear("loop", async (state, ctx) => {
        const approvals: string[] = [];

        for (const item of state.items as string[]) {
            await ctx.step(`prepare:${item}`, () => {
                calls.bump(item);
                return `prep-${item}`;
            });

            approvals.push(await ctx.interrupt<string>({ q: `approve ${item}` }, `approve:${item}`));
        }

        return { log: approvals };
    });

    const opts = { threadId: "t-loop", checkpointer };

    const p1 = await run(g, { items: ["a", "b"] }, opts);
    assert.equal(p1.pending[0]?.key, "approve:a#0");
    assert.equal(calls.count("a"), 1);
    // The loop halted on a's pause — b was never prepared.
    assert.equal(calls.count("b"), 0);

    const p2 = await resume(g, "yes-a", opts);
    assert.equal(p2.pending[0]?.key, "approve:b#0");

    const done = await resume(g, "yes-b", opts);
    assert.equal(done.status, "done");
    assert.deepEqual(done.state?.log, ["yes-a", "yes-b"]);

    // Two replays of the loop, one prepare each. Stable keys did the work.
    assert.equal(calls.count("a"), 1);
    assert.equal(calls.count("b"), 1);
});

// ── MODEL.md §12.4 ──────────────────────────────────────────────────────────

test("§12.4 superstep reduce order is task order, not completion order", async () => {
    const g = graph("race")
        .channel("winner", channel.lastWrite<string>())
        // Declared first, finishes LAST.
        .node("declaredFirst", async () => {
            await sleep(60);
            return { winner: "declaredFirst" };
        })
        // Declared second, finishes FIRST.
        .node("declaredSecond", () => ({ winner: "declaredSecond" }))
        .edge(START, "declaredFirst")
        .edge(START, "declaredSecond")
        .edge("declaredFirst", END)
        .edge("declaredSecond", END)
        .compile();

    // Folding in completion order would let declaredFirst win, since it lands
    // last. Task order makes the result independent of scheduling.
    assert.equal((await run(g)).state?.winner, "declaredSecond");
});

// ── MODEL.md §12.5 ──────────────────────────────────────────────────────────

test("§12.5 a crashed superstep replays with zero re-executed steps", async () => {
    const checkpointer = new InMemoryCheckpointer();
    const calls = counter();
    let shouldFail = true;

    const g = linear("flaky", async (_state, ctx) => {
        await ctx.step("expensive", () => {
            calls.bump("expensive");
            return "computed";
        });

        // Crash after the step, but only on the first attempt.
        if (shouldFail) {
            shouldFail = false;
            throw new Error("transient boom");
        }

        return { log: ["survived"] };
    });

    const opts = { threadId: "t-flaky", checkpointer };

    const crashed = await run(g, {}, opts);
    assert.equal(crashed.status, "error");
    assert.match(String((crashed.errors[0]?.[1] as Error).message), /transient boom/);
    assert.equal(calls.count("expensive"), 1);

    const retried = await run(g, {}, opts);
    assert.equal(retried.status, "done");
    // The retry replayed the node but the journal had already paid for the step.
    assert.equal(calls.count("expensive"), 1);
});

// ── MODEL.md §12.7 ──────────────────────────────────────────────────────────

test("§12.7 strict mode raises when a journaled step vanishes on replay", async () => {
    const checkpointer = new InMemoryCheckpointer();
    let takeBranch = true;

    const g = linear("wobbly", async (_state, ctx) => {
        // Reading this outside a step is exactly the sin strict mode detects:
        // the node's path is not a function of state + journal.
        if (takeBranch) await ctx.step("sometimes", () => "did_it");
        await ctx.interrupt({ q: "ok?" });
        return { log: ["end"] };
    });

    const opts = { threadId: "t-wobbly", checkpointer };
    assert.equal((await run(g, {}, opts)).status, "interrupted");

    // The world changed under the node; the replay now skips a journaled step.
    takeBranch = false;

    const result = await resume(g, "yes", opts);
    assert.equal(result.status, "error");

    const [node, error] = result.errors[0]!;
    assert.equal(node, "work");
    assert.ok(error instanceof NondeterminismError);
    assert.match(error.message, /sometimes#0/);
    assert.match(error.message, /deterministic modulo steps/);
});

test("strict mode can be turned off for a run", async () => {
    const checkpointer = new InMemoryCheckpointer();
    let takeBranch = true;

    const g = linear("wobbly-lax", async (_state, ctx) => {
        if (takeBranch) await ctx.step("sometimes", () => "did_it");
        await ctx.interrupt({ q: "ok?" });
        return { log: ["end"] };
    });

    const opts = { threadId: "t-lax", checkpointer, strict: false };

    assert.equal((await run(g, {}, opts)).status, "interrupted");
    takeBranch = false;
    assert.equal((await resume(g, "yes", opts)).status, "done");
});

// ── resume ergonomics ───────────────────────────────────────────────────────

describe("resume", () => {
    const pausing = (name: string) =>
        linear(name, async (_state, ctx) => {
            await ctx.interrupt({ q: "?" });
            return { log: ["done"] };
        });

    test("an object answer to a single interrupt is the answer, not a key map", async () => {
        // The bug this guards: keying off the answer's *type* would make
        // {approved: true} look like a keyed map and demand "interrupt#0".
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-obj", checkpointer };

        const g = linear("obj", async (_state, ctx) => {
            const answer = await ctx.interrupt<{ approved: boolean }>({ q: "?" });
            return { log: [`approved=${answer.approved}`] };
        });

        assert.equal((await run(g, {}, opts)).status, "interrupted");

        const done = await resume(g, { approved: true }, opts);
        assert.equal(done.status, "done");
        assert.deepEqual(done.state?.log, ["approved=true"]);
    });

    test("resumeKeyed works for the single-pause case too, so generic UIs need no special case", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-keyed", checkpointer };
        const g = pausing("keyed");

        assert.equal((await run(g, {}, opts)).status, "interrupted");
        assert.equal((await resumeKeyed(g, { "work:interrupt#0": "yes" }, opts)).status, "done");
    });

    test("a bare answer to concurrent pauses is refused, and names the alternative", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const g = graph("fanout")
            .channel("log", channel.append<string>())
            .node("a", async (_s, ctx) => ({ log: [await ctx.interrupt<string>({ q: "a?" })] }))
            .node("b", async (_s, ctx) => ({ log: [await ctx.interrupt<string>({ q: "b?" })] }))
            .edge(START, "a")
            .edge(START, "b")
            .edge("a", END)
            .edge("b", END)
            .compile();

        const opts = { threadId: "t-fanout", checkpointer };

        const paused = await run(g, {}, opts);
        assert.equal(paused.status, "interrupted");
        assert.equal(paused.pending.length, 2);

        // Both tasks journaled the same task-scoped key; only `id` separates them.
        assert.deepEqual(
            paused.pending.map((p) => p.key),
            ["interrupt#0", "interrupt#0"],
        );
        assert.deepEqual(
            [...paused.pending.map((p) => p.id)].sort(),
            ["a:interrupt#0", "b:interrupt#0"],
        );

        await assert.rejects(() => resume(g, "yes", opts), /2 interrupts are pending/);

        // Both pauses answered in one resume, and both nodes run to completion.
        const done = await resumeKeyed(
            g,
            Object.fromEntries(paused.pending.map((p) => [p.id, `ans-${p.node}`])),
            opts,
        );
        assert.equal(done.status, "done");
        assert.deepEqual([...(done.state?.log as string[])].sort(), ["ans-a", "ans-b"]);
    });

    test("new input on a parked thread is refused, not silently dropped", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-guard-1", checkpointer };

        assert.equal((await run(pausing("g1"), {}, opts)).status, "interrupted");
        await assert.rejects(() => run(pausing("g1"), { log: ["new"] }, opts), /waiting on interrupt/);
    });

    test("resuming a thread with no checkpoint is refused", async () => {
        const checkpointer = new InMemoryCheckpointer();

        await assert.rejects(
            () => resume(pausing("g2"), "yes", { threadId: "t-nothing", checkpointer }),
            /no checkpoint/,
        );
    });

    test("pendingInterrupts reports what a thread is waiting on", async () => {
        const checkpointer = new InMemoryCheckpointer();
        const opts = { threadId: "t-guard-2", checkpointer };

        assert.equal((await run(pausing("g3"), {}, opts)).status, "interrupted");

        const pending = await pendingInterrupts(checkpointer, "t-guard-2");
        assert.equal(pending[0]?.key, "interrupt#0");
        assert.equal(pending[0]?.node, "work");
        assert.deepEqual(pending[0]?.payload, { q: "?" });
    });
});
