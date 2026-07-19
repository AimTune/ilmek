/**
 * Control flow & resilience (MODEL.md §14 send, §15 command, §16 retry) —
 * conformance scenarios 9, 10, 11.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    channel,
    command,
    END,
    graph,
    InMemoryCheckpointer,
    resumeKeyed,
    run,
    send,
    START,
    stream,
} from "../src/index.ts";

function counter() {
    const counts = new Map<string, number>();
    return {
        bump: (k: string) => counts.set(k, (counts.get(k) ?? 0) + 1),
        count: (k: string) => counts.get(k) ?? 0,
    };
}

// ── §14 Send: dynamic fan-out / map-reduce ───────────────────────────────────

describe("send — dynamic fan-out (§14)", () => {
    test("§12.9 one target run per payload, each with its own input, reduced independent of scheduling", async () => {
        const seenInputs: number[] = [];

        const g = graph("mapreduce")
            .channel("items", channel.lastWrite<number[]>([]))
            .channel("results", channel.append<number>())
            .node("map", () => ({})) // just a fan-out point
            .node("worker", (state: { n: number }) => {
                seenInputs.push(state.n);
                return { results: state.n * 10 };
            })
            .node("reduce", () => ({}))
            .edge(START, "map")
            // one send per item; each worker sees ONLY its payload as state
            .router("map", (state) => state.items.map((n) => send("worker", { n })))
            .edge("worker", "reduce")
            .edge("reduce", END)
            .compile();

        const result = await run(g, { items: [1, 2, 3] });

        assert.equal(result.status, "done");
        // Each worker saw its own payload, not the shared channel state.
        assert.deepEqual([...seenInputs].sort((a, b) => a - b), [1, 2, 3]);
        // Fan-in: all three results appended, order independent of completion.
        assert.deepEqual([...(result.state!.results as number[])].sort((a, b) => a - b), [10, 20, 30]);
    });

    test("fan-in node runs once, after every worker reduced", async () => {
        const runs = counter();

        const g = graph("fanin")
            .channel("items", channel.lastWrite<number[]>([]))
            .channel("results", channel.append<number>())
            .node("map", () => ({}))
            .node("worker", (state: { n: number }) => ({ results: state.n }))
            .node("join", (state) => {
                runs.bump("join");
                return { results: -state.results.length }; // marker: saw all results
            })
            .edge(START, "map")
            .router("map", (state) => state.items.map((n) => send("worker", { n })))
            .edge("worker", "join")
            .edge("join", END)
            .compile();

        const result = await run(g, { items: [1, 2, 3] });
        assert.equal(runs.count("join"), 1, "join ran exactly once, not once per worker");
        // join saw all 3 worker results before running (its marker is -3).
        assert.ok((result.state!.results as number[]).includes(-3));
    });

    test("two sends to one node with identical payloads both run — not deduped", async () => {
        const g = graph("dup")
            .channel("out", channel.append<string>())
            .node("map", () => ({}))
            .node("worker", (state: { tag: string }) => ({ out: state.tag }))
            .edge(START, "map")
            .router("map", () => [send("worker", { tag: "x" }), send("worker", { tag: "x" })])
            .edge("worker", END)
            .compile();

        const result = await run(g, {});
        assert.deepEqual(result.state!.out, ["x", "x"], "identical sends are two real tasks");
    });

    test("an interrupt inside one fan-out branch does not collide with its siblings", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-fanout-hitl", checkpointer: cp };

        const g = graph("fanout-hitl")
            .channel("out", channel.append<string>())
            .node("map", () => ({}))
            .node("worker", async (state: { id: string }, ctx) => {
                const ok = await ctx.interrupt<string>({ q: `approve ${state.id}?` });
                return { out: `${state.id}:${ok}` };
            })
            .edge(START, "map")
            .router("map", () => [send("worker", { id: "a" }), send("worker", { id: "b" })])
            .edge("worker", END)
            .compile();

        const paused = await run(g, {}, opts);
        assert.equal(paused.status, "interrupted");
        assert.equal(paused.pending.length, 2);
        // Distinct ids despite both journaling interrupt#0 (keyed off taskKey).
        assert.deepEqual([...paused.pending.map((p) => p.id)].sort(), [
            "worker#0:interrupt#0",
            "worker#1:interrupt#0",
        ]);

        const done = await resumeKeyed(
            g,
            Object.fromEntries(paused.pending.map((p) => [p.id, "yes"])),
            opts,
        );
        assert.equal(done.status, "done");
        assert.deepEqual([...(done.state!.out as string[])].sort(), ["a:yes", "b:yes"]);
    });
});

// ── §15 Command: node-directed routing ───────────────────────────────────────

describe("command — node-directed routing (§15)", () => {
    test("§12.10 goto overrides static edges; update reduces before goto is planned", async () => {
        const g = graph("router-node")
            .channel("log", channel.append<string>())
            .channel("intent", channel.lastWrite<string>(""))
            .node("classify", () =>
                // sets intent AND routes on the value it just wrote
                command({ update: { intent: "buy", log: ["classified"] }, goto: "buy" }),
            )
            .node("buy", () => ({ log: ["bought"] }))
            .node("browse", () => ({ log: ["browsed"] }))
            // a misleading static edge that goto must override:
            .edge(START, "classify")
            .edge("classify", "browse")
            .edge("buy", END)
            .edge("browse", END)
            .compile();

        const result = await run(g, {});
        assert.equal(result.status, "done");
        assert.deepEqual(result.state!.log, ["classified", "bought"], "goto won over the static edge");
        assert.equal(result.state!.intent, "buy");
    });

    test("command with no goto falls back to static edges", async () => {
        const g = graph("cmd-fallback")
            .channel("log", channel.append<string>())
            .node("a", () => command({ update: { log: ["a"] } })) // no goto
            .node("b", () => ({ log: ["b"] }))
            .edge(START, "a")
            .edge("a", "b") // used, because no goto
            .edge("b", END)
            .compile();

        assert.deepEqual((await run(g)).state!.log, ["a", "b"]);
    });

    test("a node with no static edges is legal when it always returns a goto", async () => {
        const g = graph("all-goto")
            .channel("log", channel.append<string>())
            .node("start", () => command({ goto: "finish" }))
            .node("finish", () => ({ log: ["done"] }))
            .edge(START, "start")
            // note: no static edge out of "start"
            .edge("finish", END)
            .compile();

        assert.deepEqual((await run(g)).state!.log, ["done"]);
    });

    test("command goto can carry sends for fan-out discovered inside the node", async () => {
        const g = graph("cmd-send")
            .channel("out", channel.append<number>())
            .node("plan", () => command({ goto: [send("worker", { n: 1 }), send("worker", { n: 2 })] }))
            .node("worker", (state: { n: number }) => ({ out: state.n }))
            .edge(START, "plan")
            .edge("worker", END)
            .compile();

        const result = await run(g, {});
        assert.deepEqual([...(result.state!.out as number[])].sort(), [1, 2]);
    });

    test("goto END ends that branch", async () => {
        const g = graph("goto-end")
            .channel("log", channel.append<string>())
            .node("a", () => command({ update: { log: ["a"] }, goto: END }))
            .node("b", () => ({ log: ["b"] }))
            .edge(START, "a")
            .edge("a", "b") // overridden by goto: END
            .edge("b", END)
            .compile();

        assert.deepEqual((await run(g)).state!.log, ["a"], "goto END skipped b");
    });
});

// ── §16 Retry & resilience ───────────────────────────────────────────────────

describe("retry (§16)", () => {
    test("§12.11 a retried node re-runs its body but NOT its completed steps", async () => {
        const cp = new InMemoryCheckpointer();
        const effects = counter();
        let apiAttempts = 0;

        const g = graph("resilient")
            .channel("log", channel.append<string>())
            .node(
                "charge_then_call",
                async (_s, ctx) => {
                    // Journaled: must happen exactly once even across retries.
                    await ctx.step("charge", () => {
                        effects.bump("charge");
                        return "charged";
                    });
                    // Flaky: throws twice, succeeds on the third attempt.
                    await ctx.step("call_api", () => {
                        apiAttempts++;
                        if (apiAttempts < 3) throw new Error(`transient ${apiAttempts}`);
                        return "ok";
                    });
                    return { log: ["done"] };
                },
                { retry: { maxAttempts: 3, backoffMs: 0 } },
            )
            .edge(START, "charge_then_call")
            .edge("charge_then_call", END)
            .compile();

        const opts = { threadId: "t-retry", checkpointer: cp };
        const result = await run(g, {}, opts);

        assert.equal(result.status, "done");
        assert.equal(effects.count("charge"), 1, "charge ran ONCE across 3 attempts — journaled");
        assert.equal(apiAttempts, 3, "the flaky step itself did re-run each attempt");
    });

    test("node_retry events are emitted before each new attempt", async () => {
        let n = 0;
        const g = graph("retry-events")
            .channel("log", channel.append<string>())
            .node(
                "flaky",
                () => {
                    n++;
                    if (n < 3) throw new Error(`boom ${n}`);
                    return { log: ["ok"] };
                },
                { retry: { maxAttempts: 5, backoffMs: 0 } },
            )
            .edge(START, "flaky")
            .edge("flaky", END)
            .compile();

        const retries: number[] = [];
        for await (const ev of stream(g)) {
            if (ev.type === "node_retry") retries.push(ev.attempt);
        }
        assert.deepEqual(retries, [2, 3], "two retries announced, for attempts 2 and 3");
    });

    test("exhausting attempts ends the run in error", async () => {
        const g = graph("always-fails")
            .channel("log", channel.append<string>())
            .node("nope", () => { throw new Error("permanent"); }, { retry: { maxAttempts: 2, backoffMs: 0 } })
            .edge(START, "nope")
            .edge("nope", END)
            .compile();

        const result = await run(g);
        assert.equal(result.status, "error");
        assert.match(String((result.errors[0]?.[1] as Error).message), /permanent/);
    });

    test("retryOn can decline to retry an error", async () => {
        let attempts = 0;
        const g = graph("selective")
            .channel("log", channel.append<string>())
            .node(
                "picky",
                () => {
                    attempts++;
                    throw new Error("fatal");
                },
                { retry: { maxAttempts: 5, backoffMs: 0, retryOn: (e) => !/fatal/.test(String((e as Error).message)) } },
            )
            .edge(START, "picky")
            .edge("picky", END)
            .compile();

        const result = await run(g);
        assert.equal(result.status, "error");
        assert.equal(attempts, 1, "retryOn returned false, so no retry");
    });

    test("an interrupt is never treated as a retryable error", async () => {
        const cp = new InMemoryCheckpointer();
        let attempts = 0;
        const g = graph("interrupt-not-error")
            .channel("log", channel.append<string>())
            .node(
                "asks",
                async (_s, ctx) => {
                    attempts++;
                    await ctx.interrupt({ q: "?" });
                    return { log: ["resumed"] };
                },
                { retry: { maxAttempts: 3, backoffMs: 0 } },
            )
            .edge(START, "asks")
            .edge("asks", END)
            .compile();

        const opts = { threadId: "t-int-retry", checkpointer: cp };
        const paused = await run(g, {}, opts);
        assert.equal(paused.status, "interrupted");
        assert.equal(attempts, 1, "the pause did not trigger the retry loop");
    });
});
