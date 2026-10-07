/**
 * Journal replay determinism (MODEL.md §5) under the cycles that stress it:
 * many interrupt/resume rounds, crashes before and after steps, retries,
 * time-travel forks, cancellation and concurrent threads. The invariant is
 * always the same — a completed step's function runs exactly once.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    channel,
    END,
    graph,
    InMemoryCheckpointer,
    NondeterminismError,
    resume,
    resumeKeyed,
    run,
    send,
    START,
    stream,
    threadState,
    type Checkpointer,
    type Context,
    type IlmekEvent,
    type NodeFn,
} from "../src/index.ts";
import { createContext } from "../src/context.ts";
import { Journal, TaskJournal } from "../src/journal.ts";

function linear(fn: NodeFn<any>) {
    return graph("linear")
        .channel("log", channel.append<unknown>())
        .channel("items", channel.lastWrite<string[]>([]))
        .node("work", fn)
        .edge(START, "work")
        .edge("work", END)
        .compile();
}

function tally() {
    const counts = new Map<string, number>();
    return {
        bump(key: string): number {
            const n = (counts.get(key) ?? 0) + 1;
            counts.set(key, n);
            return n;
        },
        of: (key: string) => counts.get(key) ?? 0,
    };
}

describe("steps across many pause/resume rounds", () => {
    test("three pauses with a step before each: every step runs once in total", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-three", checkpointer: cp };
        const calls = tally();

        const g = linear(async (_s, ctx) => {
            const answers: string[] = [];
            for (const stage of ["one", "two", "three"]) {
                await ctx.step(`prep:${stage}`, () => calls.bump(stage));
                answers.push(await ctx.interrupt<string>({ stage }, `ask:${stage}`));
            }
            await ctx.step("finish", () => calls.bump("finish"));
            return { log: answers };
        });

        let result = await run(g, {}, opts);
        for (const answer of ["A", "B", "C"]) {
            assert.equal(result.status, "interrupted");
            result = await resume(g, answer, opts);
        }

        assert.equal(result.status, "done");
        assert.deepEqual(result.state?.log, ["A", "B", "C"]);
        for (const key of ["one", "two", "three", "finish"]) assert.equal(calls.of(key), 1, key);
    });

    test("each resume is a new run id, the thread id is stable", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-ids", checkpointer: cp };
        const runIds: string[] = [];
        const g = linear(async (_s, ctx) => {
            runIds.push(ctx.runId);
            await ctx.interrupt();
            await ctx.interrupt();
            return {};
        });

        const r1 = await run(g, {}, opts);
        const r2 = await resume(g, 1, opts);
        const r3 = await resume(g, 2, opts);
        assert.equal(new Set([r1.runId, r2.runId, r3.runId]).size, 3);
        assert.deepEqual([r1.threadId, r2.threadId, r3.threadId], ["t-ids", "t-ids", "t-ids"]);
        assert.deepEqual(runIds, [r1.runId, r2.runId, r3.runId]);
    });

    test("a repeated base key auto-suffixes, and replay hands each occurrence its own value", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-suffix", checkpointer: cp };
        let n = 0;
        const g = linear(async (_s, ctx) => {
            const a = await ctx.step("roll", () => ++n);
            const b = await ctx.step("roll", () => ++n);
            const c = await ctx.step("roll", () => ++n);
            await ctx.interrupt();
            return { log: [a, b, c] };
        });

        await run(g, {}, opts);
        const done = await resume(g, "ok", opts);
        assert.deepEqual(done.state?.log, [1, 2, 3]);
        assert.equal(n, 3);
    });

    test("journaled falsy values replay as themselves and are not recomputed", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-falsy-steps", checkpointer: cp };
        const calls = tally();
        const values = [0, "", false, null, undefined, Number.NaN];
        const seen: unknown[][] = [];

        const g = linear(async (_s, ctx) => {
            const pass: unknown[] = [];
            for (const [i, v] of values.entries()) pass.push(await ctx.step(`v${i}`, () => (calls.bump(`v${i}`), v)));
            seen.push(pass);
            await ctx.interrupt();
            return {};
        });

        await run(g, {}, opts);
        await resume(g, "go", opts);
        assert.deepEqual(seen[1], values);
        for (let i = 0; i < values.length; i++) assert.equal(calls.of(`v${i}`), 1);
    });

    test("an async step's resolved value is what gets journaled", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-async", checkpointer: cp };
        const g = linear(async (_s, ctx) => {
            const v = await ctx.step("fetch", async () => {
                await new Promise((r) => setTimeout(r, 5));
                return { id: 7 };
            });
            await ctx.interrupt();
            return { log: [v] };
        });

        await run(g, {}, opts);
        assert.deepEqual((await cp.getJournal("t-async:root:work")).dump(), [["fetch#0", { status: "done", value: { id: 7 } }], ["interrupt#0", { status: "pending", payload: {} }]]);
        assert.deepEqual((await resume(g, "x", opts)).state?.log, [{ id: 7 }]);
    });
});

describe("crash replay", () => {
    test("a step whose function threw is not journaled, so it runs again; earlier steps do not", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-step-throws", checkpointer: cp };
        const calls = tally();

        const g = linear(async (_s, ctx) => {
            await ctx.step("first", () => calls.bump("first"));
            await ctx.step("flaky", () => {
                if (calls.bump("flaky") === 1) throw new Error("flaky failed");
                return "ok";
            });
            return { log: ["done"] };
        });

        assert.equal((await run(g, {}, opts)).status, "error");
        assert.equal((await run(g, {}, opts)).status, "done");
        assert.equal(calls.of("first"), 1);
        assert.equal(calls.of("flaky"), 2);
    });

    test("three consecutive crashes still pay for each completed step once", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-crash-x3", checkpointer: cp };
        const calls = tally();
        let attempt = 0;

        const g = linear(async (_s, ctx) => {
            attempt++;
            for (let i = 1; i <= 3; i++) {
                await ctx.step(`s${i}`, () => calls.bump(`s${i}`));
                if (attempt === i) throw new Error(`crash after s${i}`);
            }
            return { log: ["survived"] };
        });

        for (let i = 0; i < 3; i++) assert.equal((await run(g, {}, opts)).status, "error");
        const done = await run(g, {}, opts);
        assert.deepEqual(done.state?.log, ["survived"]);
        for (const k of ["s1", "s2", "s3"]) assert.equal(calls.of(k), 1, k);
    });

    test("a crash in a later superstep replays only that superstep's journal", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-later", checkpointer: cp };
        const calls = tally();
        let crash = true;

        const g = graph()
            .channel("log", channel.append<string>())
            .node("a", async (_s, ctx) => ({ log: [`a${await ctx.step("a", () => calls.bump("a"))}`] }))
            .node("b", async (_s, ctx) => {
                await ctx.step("b", () => calls.bump("b"));
                if (crash) {
                    crash = false;
                    throw new Error("b crashed");
                }
                return { log: ["b"] };
            })
            .edge(START, "a")
            .edge("a", "b")
            .edge("b", END)
            .compile();

        assert.equal((await run(g, {}, opts)).status, "error");
        const done = await run(g, {}, opts);
        assert.deepEqual(done.state?.log, ["a1", "b"]);
        assert.equal(calls.of("a"), 1);
        assert.equal(calls.of("b"), 1);
    });

    test("committed journals are dropped, so a completed superstep leaves no replay memory", async () => {
        const cp = new InMemoryCheckpointer();
        const g = linear(async (_s, ctx) => ({ log: [await ctx.step("s", () => "v")] }));
        await run(g, {}, { threadId: "t-drop", checkpointer: cp });
        assert.deepEqual((await cp.getJournal("t-drop:root:work")).dump(), []);
    });

    test("a fan-out where one branch crashes replays only the steps that branch had not finished", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-fan-crash", checkpointer: cp };
        const calls = tally();
        let crash = true;

        const g = graph()
            .channel("out", channel.append<string>())
            .node("fan", () => ({}))
            .node("worker", async (input: { item: string }, ctx) => {
                await ctx.step("work", () => calls.bump(input.item));
                if (input.item === "b" && crash) {
                    crash = false;
                    throw new Error("b crashed");
                }
                return { out: [input.item] };
            })
            .edge(START, "fan")
            .router("fan", () => ["a", "b", "c"].map((item) => send("worker", { item })))
            .compile();

        assert.equal((await run(g, {}, opts)).status, "error");
        const done = await run(g, {}, opts);
        assert.deepEqual(done.state?.out, ["a", "b", "c"]);
        for (const k of ["a", "b", "c"]) assert.equal(calls.of(k), 1, k);
    });
});

describe("time travel onto a paused superstep", () => {
    test("forking from the checkpoint before a pause re-raises the pause instead of re-running steps", async () => {
        const cp = new InMemoryCheckpointer();
        const calls = tally();
        const g = graph()
            .channel("log", channel.append<string>())
            .node("first", () => ({ log: ["first"] }))
            .node("ask", async (_s, ctx) => {
                await ctx.step("effect", () => calls.bump("effect"));
                return { log: [await ctx.interrupt<string>({ q: "?" })] };
            })
            .edge(START, "first")
            .edge("first", "ask")
            .edge("ask", END)
            .compile();

        const paused = await run(g, {}, { threadId: "t-fork", checkpointer: cp });
        assert.equal(paused.status, "interrupted");
        const parked = (await cp.get("t-fork"))!;

        const forked = await run(g, {}, { threadId: "t-fork", checkpointer: cp, checkpointId: parked.parentId! });
        assert.equal(forked.status, "interrupted");
        assert.equal(forked.pending[0]?.key, "interrupt#0");
        assert.equal(calls.of("effect"), 1);
    });

    test("a step that reuses the key of a still-pending interrupt is a NondeterminismError", async () => {
        const cp = new InMemoryCheckpointer();
        let asStep = false;
        const g = graph()
            .channel("log", channel.append<string>())
            .node("first", () => ({}))
            .node("ask", async (_s, ctx) => {
                if (asStep) await ctx.step("gate", () => "computed");
                else await ctx.interrupt({}, "gate");
                return {};
            })
            .edge(START, "first")
            .edge("first", "ask")
            .compile();

        await run(g, {}, { threadId: "t-collide", checkpointer: cp });
        const parked = (await cp.get("t-collide"))!;
        asStep = true;

        const forked = await run(g, {}, { threadId: "t-collide", checkpointer: cp, checkpointId: parked.parentId! });
        assert.equal(forked.status, "error");
        const err = forked.errors[0]![1];
        assert.ok(err instanceof NondeterminismError);
        assert.match(err.message, /"gate#0" collides with a pending interrupt/);
    });

    test("resuming from an explicit checkpointId that is not interrupted is refused", async () => {
        const cp = new InMemoryCheckpointer();
        const g = graph()
            .channel("log", channel.append<string>())
            .node("a", () => ({}))
            .node("b", async (_s, ctx) => ({ log: [await ctx.interrupt<string>()] }))
            .edge(START, "a")
            .edge("a", "b")
            .compile();

        await run(g, {}, { threadId: "t-ckid", checkpointer: cp });
        const parked = (await cp.get("t-ckid"))!;
        await assert.rejects(
            () => resume(g, "x", { threadId: "t-ckid", checkpointer: cp, checkpointId: parked.parentId! }),
            /not interrupted/,
        );
    });

    test("an unknown checkpointId resumes nothing", async () => {
        const cp = new InMemoryCheckpointer();
        const g = linear(async (_s, ctx) => ({ log: [await ctx.interrupt()] }));
        await run(g, {}, { threadId: "t-unknown-ck", checkpointer: cp });
        await assert.rejects(
            () => resume(g, "x", { threadId: "t-unknown-ck", checkpointer: cp, checkpointId: "ckpt-nope" }),
            /no checkpoint/,
        );
    });
});

describe("strict mode", () => {
    test("a step requested more times on replay than journaled is not a violation", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-more", checkpointer: cp };
        let extra = false;
        const g = linear(async (_s, ctx) => {
            await ctx.step("s", () => 1);
            if (extra) await ctx.step("s", () => 2);
            await ctx.interrupt();
            return {};
        });

        await run(g, {}, opts);
        extra = true;
        assert.equal((await resume(g, "ok", opts)).status, "done");
    });

    test("a strict violation inside a retried node is reported, not retried into silence", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-strict-retry", checkpointer: cp };
        let pass = 0;
        const g = graph()
            .channel("log", channel.append<string>())
            .node(
                "work",
                async (_s, ctx) => {
                    pass++;
                    if (pass === 1) {
                        await ctx.step("only-first", () => "x");
                        throw new Error("transient");
                    }
                    return {};
                },
                { retry: { maxAttempts: 3 } },
            )
            .edge(START, "work")
            .compile();

        const result = await run(g, {}, opts);
        assert.equal(result.status, "error");
        assert.ok(result.errors[0]![1] instanceof NondeterminismError);
        assert.equal(pass, 3, "a nondeterminism error is just an error to the retry policy");
    });
});

describe("cancellation", () => {
    for (const [label, reason, expected] of [
        ["an Error reason reports its message", new Error("deadline"), "deadline"],
        ["an object reason is reported as JSON", { code: 499 }, '{"code":499}'],
        ["a reason that cannot be serialized falls back to 'aborted'", (() => {
            const o: Record<string, unknown> = {};
            o.self = o;
            return o;
        })(), "aborted"],
        ["a function reason serializes to nothing and falls back to 'aborted'", () => 1, "aborted"],
    ] as const) {
        test(label, async () => {
            const ac = new AbortController();
            ac.abort(reason);
            const result = await run(linear(() => ({})), {}, { signal: ac.signal });
            assert.equal(result.status, "aborted");
            assert.equal(result.abortReason, expected);
        });
    }

    test("a signal whose reason is undefined reports 'aborted'", async () => {
        const fake = { aborted: true, reason: undefined, addEventListener() {} } as unknown as AbortSignal;
        const result = await run(linear(() => ({})), {}, { signal: fake });
        assert.equal(result.abortReason, "aborted");
    });

    test("an abort during a retry backoff stops the retry loop with the last error", async () => {
        const ac = new AbortController();
        let attempts = 0;
        const g = graph()
            .channel("log", channel.append<string>())
            .node(
                "work",
                () => {
                    attempts++;
                    setTimeout(() => ac.abort("stop"), 10);
                    throw new Error(`attempt ${attempts}`);
                },
                { retry: { maxAttempts: 5, backoffMs: 10_000 } },
            )
            .edge(START, "work")
            .compile();

        const started = Date.now();
        const result = await run(g, {}, { signal: ac.signal });
        assert.equal(result.status, "error");
        assert.equal(attempts, 1);
        assert.match((result.errors[0]![1] as Error).message, /attempt 1/);
        assert.ok(Date.now() - started < 5_000, "did not sit out the 10s backoff");
    });

    test("a retry with a backoff waits before the next attempt", async () => {
        const stamps: number[] = [];
        const g = graph()
            .channel("log", channel.append<string>())
            .node(
                "work",
                () => {
                    stamps.push(Date.now());
                    if (stamps.length < 2) throw new Error("again");
                    return { log: ["ok"] };
                },
                { retry: { maxAttempts: 2, backoffMs: 40 } },
            )
            .edge(START, "work")
            .compile();

        assert.equal((await run(g)).status, "done");
        assert.ok(stamps[1]! - stamps[0]! >= 30, `waited ${stamps[1]! - stamps[0]!}ms`);
    });

    test("an already-aborted signal does not retry a failing node", async () => {
        let attempts = 0;
        const ac = new AbortController();
        const g = graph()
            .channel("log", channel.append<string>())
            .node(
                "work",
                () => {
                    attempts++;
                    ac.abort();
                    throw new Error("x");
                },
                { retry: { maxAttempts: 4 } },
            )
            .edge(START, "work")
            .compile();

        await run(g, {}, { signal: ac.signal });
        assert.equal(attempts, 1);
    });
});

describe("concurrent runs", () => {
    test("concurrent runs on different threads of one checkpointer never share journals", async () => {
        const cp = new InMemoryCheckpointer();
        const g = linear(async (s, ctx) => {
            const mine = await ctx.step("own", async () => {
                await new Promise((r) => setTimeout(r, Math.random() * 10));
                return (s.items as string[])[0];
            });
            const answer = await ctx.interrupt<string>({ mine });
            return { log: [`${mine}:${answer}`] };
        });

        const threads = Array.from({ length: 12 }, (_, i) => `conc-${i}`);
        const paused = await Promise.all(threads.map((t) => run(g, { items: [t] }, { threadId: t, checkpointer: cp })));
        paused.forEach((p, i) => assert.deepEqual(p.pending[0]?.payload, { mine: threads[i] }));

        const done = await Promise.all(threads.map((t) => resume(g, `ans-${t}`, { threadId: t, checkpointer: cp })));
        done.forEach((d, i) => assert.deepEqual(d.state?.log, [`${threads[i]}:ans-${threads[i]}`]));
    });

    test("concurrent tasks in one superstep each see the checkpoint state, not a sibling's write", async () => {
        const g = graph()
            .channel("n", channel.lastWrite<number>(0))
            .channel("seen", channel.append<string>())
            .node("a", async (s) => {
                await new Promise((r) => setTimeout(r, 15));
                return { seen: [`a saw ${s.n}`], n: 1 };
            })
            .node("b", (s) => ({ seen: [`b saw ${s.n}`], n: 2 }))
            .edge(START, "a")
            .edge(START, "b")
            .compile();

        const done = await run(g, { n: 0 });
        assert.deepEqual(done.state?.seen, ["a saw 0", "b saw 0"]);
        assert.equal(done.state?.n, 2);
    });
});

describe("context surface", () => {
    test("remainingSteps is the recursion limit minus the superstep index", async () => {
        const seen: Array<[number, number]> = [];
        const g = graph()
            .channel("i", channel.reduce<number, number>((c, x) => (c ?? 0) + x, 0))
            .node("loop", (s, ctx) => {
                seen.push([ctx.stepIndex, ctx.remainingSteps]);
                return { i: 1 };
            })
            .edge(START, "loop")
            .router("loop", (s) => (s.i < 3 ? "loop" : END))
            .compile();

        await run(g, {}, { recursionLimit: 3 });
        assert.deepEqual(seen, [
            [0, 3],
            [1, 2],
            [2, 1],
        ]);
    });

    test("meta and the thread id reach both nodes and routers", async () => {
        const seen: unknown[] = [];
        const g = graph()
            .node("a", (_s, ctx) => {
                seen.push(["node", ctx.meta.tenant, ctx.threadId, ctx.node]);
            })
            .edge(START, "a")
            .router("a", (_s, ctx) => {
                seen.push(["router", ctx.meta.tenant, ctx.threadId, ctx.node]);
                return END;
            })
            .compile();

        await run(g, {}, { threadId: "t-meta", meta: { tenant: "acme" } });
        assert.deepEqual(seen, [
            ["node", "acme", "t-meta", "a"],
            ["router", "acme", "t-meta", ""],
        ]);
    });

    test("ctx.journal is a read-only snapshot of this task's entries so far", async () => {
        const snapshots: unknown[] = [];
        const g = linear(async (_s, ctx) => {
            snapshots.push(ctx.journal);
            await ctx.step("a", () => 1);
            snapshots.push(ctx.journal);
            return {};
        });

        await run(g);
        assert.deepEqual(snapshots, [[], [["a#0", { status: "done", value: 1 }]]]);
    });

    test("a router's emit and emitToken are inert", async () => {
        const g = graph()
            .node("a", () => ({}))
            .edge(START, "a")
            .router("a", (_s, ctx) => {
                ctx.emit("from router");
                ctx.emitToken("tok");
                return END;
            })
            .compile();

        const result = await run(g);
        assert.equal(result.status, "done");
        assert.equal(result.events.filter((e) => e.type === "custom").length, 0);
    });

    test("ctx.interrupt in a router is refused like ctx.step", async () => {
        const g = graph()
            .node("a", () => ({}))
            .edge(START, "a")
            .router("a", (_s, ctx) => {
                void ctx.interrupt();
                return END;
            })
            .compile();

        await assert.rejects(() => run(g), /ctx\.interrupt\(\) is not available in a router/);
    });

    test("without a checkpointer a step still journals in memory and suffixes repeats", async () => {
        const tj = new TaskJournal(new Journal());
        const ctx: Context = createContext({
            graph: linear(() => ({})) as any,
            state: {} as any,
            threadId: "t",
            runId: "r",
            node: "work",
            taskId: "t:root:work",
            stepIndex: 0,
            recursionLimit: 25,
            meta: {},
            checkpointer: null,
            taskJournal: tj,
            emit: () => undefined,
            signal: undefined,
            log: {},
        });

        assert.equal(await ctx.step("x", () => 42), 42);
        assert.deepEqual(tj.journal.dump(), [["x#0", { status: "done", value: 42 }]]);
        assert.equal(await ctx.step("x", () => 43), 43, "a second occurrence is a new key, x#1");
    });

    test("a pending entry the replay reaches again re-raises the pause without persisting", async () => {
        let puts = 0;
        const cp = { putJournal: async () => void puts++ } as unknown as Checkpointer;
        const journal = new Journal();
        journal.putPending("interrupt#0", { q: "old" });
        const ctx: Context = createContext({
            graph: linear(() => ({})) as any,
            state: {} as any,
            threadId: "t",
            runId: "r",
            node: "work",
            taskId: "t:root:work",
            stepIndex: 0,
            recursionLimit: 25,
            meta: {},
            checkpointer: cp,
            taskJournal: new TaskJournal(journal),
            emit: () => undefined,
            signal: undefined,
            log: {},
        });

        await assert.rejects(() => ctx.interrupt({ q: "new" }), (e: unknown) => (e as { key?: string }).key === "interrupt#0");
        assert.equal(puts, 0);
    });
});

describe("threadState", () => {
    test("is null for an unknown thread and the materialized channels otherwise", async () => {
        const cp = new InMemoryCheckpointer();
        const g = graph()
            .channel("a", channel.lastWrite<string>("default-a"))
            .channel("b", channel.append<number>())
            .node("w", () => ({ b: [1] }))
            .edge(START, "w")
            .compile();

        assert.equal(await threadState(g, cp, "nobody"), null);
        await run(g, {}, { threadId: "t-state", checkpointer: cp });
        assert.deepEqual(await threadState(g, cp, "t-state"), { a: "default-a", b: [1] });
    });

    test("a parked thread's state is the state before the paused superstep", async () => {
        const cp = new InMemoryCheckpointer();
        const g = linear(async (_s, ctx) => ({ log: [await ctx.interrupt()] }));
        await run(g, { log: ["input"] }, { threadId: "t-parked-state", checkpointer: cp });
        assert.deepEqual((await threadState(g, cp, "t-parked-state"))?.log, ["input"]);
    });
});

describe("event stream", () => {
    test("an interrupted run emits checkpoint → interrupt → run_end in that order, and no node_end for the paused task", async () => {
        const cp = new InMemoryCheckpointer();
        const g = linear(async (_s, ctx) => ({ log: [await ctx.interrupt({ q: 1 })] }));
        const events: IlmekEvent[] = [];
        for await (const e of stream(g, {}, { threadId: "t-ev", checkpointer: cp })) events.push(e);

        assert.deepEqual(events.map((e) => e.type), ["run_start", "step_start", "node_start", "checkpoint", "interrupt", "run_end"]);
        assert.deepEqual(events.map((e) => e.seq), [1, 2, 3, 4, 5, 6]);
    });

    test("an erroring run ends with node_error then run_end status error, and writes no checkpoint event", async () => {
        const g = linear(() => {
            throw new Error("x");
        });
        const types: string[] = [];
        for await (const e of stream(g)) types.push(e.type);
        assert.deepEqual(types, ["run_start", "step_start", "node_start", "node_error", "run_end"]);
    });

    test("a consumer that stops reading mid-run leaves the last committed checkpoint resumable", async () => {
        const cp = new InMemoryCheckpointer();
        const calls = tally();
        const g = graph()
            .channel("log", channel.append<string>())
            .node("a", async (_s, ctx) => ({ log: [`a${await ctx.step("a", () => calls.bump("a"))}`] }))
            .node("b", () => ({ log: ["b"] }))
            .edge(START, "a")
            .edge("a", "b")
            .compile();

        for await (const e of stream(g, {}, { threadId: "t-walkaway", checkpointer: cp })) {
            if (e.type === "checkpoint") break;
        }
        const done = await run(g, {}, { threadId: "t-walkaway", checkpointer: cp });
        assert.deepEqual(done.state?.log, ["a1", "b"]);
        assert.equal(calls.of("a"), 1);
    });

    test("resumeKeyed over a fan-out: each send branch is answered by its own id", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-fan-pause", checkpointer: cp };
        const g = graph()
            .channel("out", channel.append<string>())
            .node("fan", () => ({}))
            .node("w", async (i: { n: number }, ctx) => ({ out: [`${i.n}=${await ctx.interrupt<string>({ n: i.n })}`] }))
            .edge(START, "fan")
            .router("fan", () => [1, 2, 3].map((n) => send("w", { n })))
            .compile();

        const paused = await run(g, {}, opts);
        assert.deepEqual(paused.pending.map((p) => p.id), ["w#0:interrupt#0", "w#1:interrupt#0", "w#2:interrupt#0"]);
        const done = await resumeKeyed(g, { "w#0:interrupt#0": "x", "w#1:interrupt#0": "y", "w#2:interrupt#0": "z" }, opts);
        assert.deepEqual(done.state?.out, ["1=x", "2=y", "3=z"]);
    });
});

describe("damaged or unusual environments", () => {
    test("resuming a pause whose journal was lost is refused with unknown_key, not silently re-run", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-lost", checkpointer: cp };
        const g = linear(async (_s, ctx) => ({ log: [await ctx.interrupt()] }));
        const paused = await run(g, {}, opts);
        await cp.dropJournal(paused.pending[0]!.taskId);

        await assert.rejects(() => resume(g, "x", opts), /cannot answer "interrupt#0" .*unknown_key/);
    });

    test("ILMEK_DEBUG_BREAK_ON_INTERRUPT=1 without a debugger attached changes nothing", async () => {
        const { spawnSync } = await import("node:child_process");
        const index = new URL("../src/index.ts", import.meta.url).href;
        const script = `
            const m = await import(${JSON.stringify(index)});
            const cp = new m.InMemoryCheckpointer();
            const g = m.graph().channel("log", m.channel.append()).node("w", async (_s, ctx) => ({ log: [await ctx.interrupt({ q: 1 })] })).edge(m.START, "w").compile();
            const o = { threadId: "t", checkpointer: cp };
            const p = await m.run(g, {}, o);
            const d = await m.resume(g, "yes", o);
            console.log(JSON.stringify([p.status, d.status, d.state.log]));`;
        const child = spawnSync(process.execPath, ["--input-type=module", "-e", script], {
            env: { ...process.env, ILMEK_DEBUG_BREAK_ON_INTERRUPT: "1" },
            encoding: "utf8",
        });
        assert.equal(child.status, 0, child.stderr);
        assert.deepEqual(JSON.parse(child.stdout.trim()), ["interrupted", "done", ["yes"]]);
    });
});
