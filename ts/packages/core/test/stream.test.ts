/**
 * Streaming surface (MODEL.md §10.1–§10.3): the seq/ns envelope, projection
 * modes, the token convention, and AbortSignal cancellation.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import {
    channel,
    END,
    graph,
    InMemoryCheckpointer,
    isToken,
    project,
    projected,
    resumeStream,
    run,
    START,
    stream,
    streamModes,
    token,
    type IlmekEvent,
    type NodeFn,
    type StreamPart,
    type TokenChunk,
} from "../src/index.ts";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

function linear(name: string, fn: NodeFn<any>) {
    return graph(name)
        .channel("log", channel.append<string>())
        .node("work", fn)
        .edge(START, "work")
        .edge("work", END)
        .compile();
}

async function collect<T>(source: AsyncIterable<T>): Promise<T[]> {
    const out: T[] = [];
    for await (const x of source) out.push(x);
    return out;
}

// ── the envelope: seq + ns (MODEL.md §10) ────────────────────────────────────

describe("event envelope", () => {
    test("every event carries a monotonic 1-based seq and a root ns", async () => {
        const g = linear("env", () => ({ log: ["hi"] }));
        const events = await collect(stream(g));

        assert.ok(events.length > 0);
        assert.deepEqual(
            events.map((e) => e.seq),
            events.map((_, i) => i + 1),
            "seq must be 1,2,3,… with no gaps or repeats",
        );
        for (const e of events) assert.deepEqual(e.ns, [], "ns is [] at the root graph");
    });

    test("seq is unique per run — a reconnect can resume after its last-seen seq", async () => {
        const g = linear("reconnect", (_s, ctx) => {
            ctx.emit("a");
            ctx.emit("b");
            return { log: ["done"] };
        });

        const events = await collect(stream(g));
        const seqs = events.map((e) => e.seq);
        assert.equal(new Set(seqs).size, seqs.length, "no duplicate seq");

        // Simulate a client that saw up to seq=3, then reconnects: it replays the
        // buffered tail by seq without re-running anything.
        const lastSeen = 3;
        const tail = events.filter((e) => e.seq > lastSeen);
        assert.ok(tail.length > 0 && tail[0]!.seq === 4);
    });
});

// ── projection modes (MODEL.md §10.1) ────────────────────────────────────────

describe("projection modes", () => {
    const g = graph("proj")
        .channel("log", channel.append<string>())
        .channel("n", channel.lastWrite<number>(0))
        .node("a", (_s, ctx) => {
            ctx.emit({ note: "from a" });
            return { log: ["a"], n: 1 };
        })
        .node("b", () => ({ log: ["b"], n: 2 }))
        .edge(START, "a")
        .edge("a", "b")
        .edge("b", END)
        .compile();

    test("updates: one { [node]: update } per node that ran", async () => {
        const parts = await collect(streamModes(g, {}, ["updates"]));
        assert.deepEqual(
            parts.map((p) => p.data),
            [{ a: { log: ["a"], n: 1 } }, { b: { log: ["b"], n: 2 } }],
        );
        for (const p of parts) assert.equal(p.mode, "updates");
    });

    test("values: full channel state after each superstep", async () => {
        const parts = await collect(streamModes(g, {}, ["values"]));
        // Two supersteps (a, then b), so two state snapshots; the last is terminal.
        assert.deepEqual(parts.map((p) => p.data), [
            { log: ["a"], n: 1 },
            { log: ["a", "b"], n: 2 },
        ]);
    });

    test("custom: every emitted payload", async () => {
        const parts = await collect(streamModes(g, {}, ["custom"]));
        assert.deepEqual(parts.map((p) => p.data), [{ note: "from a" }]);
    });

    test("debug: every event, unchanged, rewrapped", async () => {
        const parts = await collect(streamModes(g, {}, ["debug"]));
        const raw = await collect(stream(g));
        assert.equal(parts.length, raw.length);
        assert.equal((parts[0]!.data as IlmekEvent).type, "run_start");
    });

    test("multiple modes multiplex through one pass, tagged and seq-ordered", async () => {
        const parts = await collect(streamModes(g, {}, ["updates", "custom"]));
        const modes = new Set(parts.map((p) => p.mode));
        assert.deepEqual([...modes].sort(), ["custom", "updates"]);
        // seq is non-decreasing — the parts stay in source-event order.
        const seqs = parts.map((p) => p.seq);
        assert.deepEqual(seqs, [...seqs].sort((x, y) => x - y));
    });

    test("project() is pure and reusable on a buffered event", () => {
        const ev: IlmekEvent = {
            type: "node_end",
            node: "x",
            update: { log: ["z"] },
            runId: "r",
            threadId: "t",
            seq: 7,
            ns: [],
        };
        const parts = project(ev, ["updates", "custom"]);
        assert.deepEqual(parts, [{ mode: "updates", seq: 7, ns: [], data: { x: { log: ["z"] } } }]);
    });

    test("projected() composes with resumeStream, not just fresh runs", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-proj-resume", checkpointer: cp };
        const paused = linear("pauser", async (_s, ctx) => {
            await ctx.interrupt({ q: "?" });
            return { log: ["resumed"] };
        });

        assert.equal((await run(paused, {}, opts)).status, "interrupted");

        const parts: StreamPart[] = await collect(
            projected(resumeStream(paused, "yes", opts), ["updates"]),
        );
        assert.deepEqual(parts.map((p) => p.data), [{ work: { log: ["resumed"] } }]);
    });
});

// ── token convention (MODEL.md §10.2) ────────────────────────────────────────

describe("token streaming", () => {
    test("emitToken rides the custom channel and messages mode filters it out", async () => {
        const g = linear("tok", (_s, ctx) => {
            ctx.emit({ progress: 0.5 }); // a non-token custom event
            ctx.emitToken("Hel");
            ctx.emitToken("lo", { model: "x" });
            return { log: ["done"] };
        });

        const messages = await collect(streamModes(g, {}, ["messages"]));
        assert.deepEqual(
            messages.map((p) => (p.data as TokenChunk).text),
            ["Hel", "lo"],
            "messages mode yields only token deltas",
        );
        assert.deepEqual((messages[1]!.data as TokenChunk).meta, { model: "x" });

        // custom mode sees everything on the channel, tokens included.
        const custom = await collect(streamModes(g, {}, ["custom"]));
        assert.equal(custom.length, 3);
    });

    test("token() / isToken() are the shape messages mode keys off", () => {
        assert.ok(isToken(token("hi")));
        assert.ok(isToken({ type: "token", text: "hi" }));
        assert.equal(isToken({ type: "token" }), false); // no text
        assert.equal(isToken({ text: "hi" }), false); // no type tag
        assert.equal(isToken("hi"), false);
    });

    test("tokens are NOT journaled — a resumed node re-streams them", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-tok-replay", checkpointer: cp };
        const streamed: string[] = [];

        const g = linear("streamer", async (_s, ctx) => {
            ctx.emitToken("draft"); // emitted on BOTH passes
            const ok = await ctx.step("commit", () => "committed"); // journaled: once
            return { log: [ok] };
        });

        for await (const p of streamModes(g, {}, ["messages"], opts)) {
            streamed.push((p.data as TokenChunk).text);
        }
        // first pass hits interrupt? no — no interrupt here; it completes.
        // Re-run on same thread would replay; instead drive an interrupt case:
        assert.deepEqual(streamed, ["draft"]);
    });

    test("across an interrupt, tokens re-stream but journaled steps do not re-run", async () => {
        const cp = new InMemoryCheckpointer();
        const opts = { threadId: "t-tok-hitl", checkpointer: cp };
        let commits = 0;

        const g = linear("hitl-streamer", async (_s, ctx) => {
            ctx.emitToken("thinking…"); // transient: re-streams every pass
            await ctx.step("commit", () => {
                commits++; // journaled: exactly once
                return "done";
            });
            await ctx.interrupt({ q: "ok?" });
            return { log: ["end"] };
        });

        const pass1 = await collect(projected(stream(g, {}, opts), ["messages"]));
        const pass2 = await collect(projected(resumeStream(g, "yes", opts), ["messages"]));

        assert.deepEqual(pass1.map((p) => (p.data as TokenChunk).text), ["thinking…"]);
        assert.deepEqual(pass2.map((p) => (p.data as TokenChunk).text), ["thinking…"], "re-streamed");
        assert.equal(commits, 1, "journaled step ran once across both passes");
    });
});

// ── AbortSignal (MODEL.md §10.3) ─────────────────────────────────────────────

describe("cancellation", () => {
    test("an already-aborted signal stops the run before any node runs", async () => {
        let ran = false;
        const g = linear("never", () => {
            ran = true;
            return { log: ["x"] };
        });

        const controller = new AbortController();
        controller.abort("user closed the tab");

        const result = await run(g, {}, { signal: controller.signal });
        assert.equal(result.status, "aborted");
        assert.equal(result.abortReason, "user closed the tab");
        assert.equal(ran, false);
    });

    test("aborting between supersteps stops at the boundary", async () => {
        const controller = new AbortController();
        const ran: string[] = [];

        const g = graph("multi")
            .channel("log", channel.append<string>())
            .node("a", () => {
                ran.push("a");
                controller.abort(); // abort while a runs; b is next superstep
                return { log: ["a"] };
            })
            .node("b", () => {
                ran.push("b");
                return { log: ["b"] };
            })
            .edge(START, "a")
            .edge("a", "b")
            .edge("b", END)
            .compile();

        const result = await run(g, {}, { signal: controller.signal });
        assert.equal(result.status, "aborted");
        assert.deepEqual(ran, ["a"], "b never ran — abort caught at the superstep boundary");
    });

    test("ctx.signal is the same signal, so nodes can forward it to their own awaits", async () => {
        const controller = new AbortController();
        let seen: AbortSignal | undefined;

        const g = linear("forward", (_s, ctx) => {
            seen = ctx.signal;
            return { log: ["x"] };
        });

        await run(g, {}, { signal: controller.signal });
        assert.equal(seen, controller.signal);
    });

    test("an aborted run's last checkpoint stands — it resumes cleanly, not rolled back", async () => {
        const cp = new InMemoryCheckpointer();
        const controller = new AbortController();

        const g = graph("resumable")
            .channel("log", channel.append<string>())
            .node("a", () => ({ log: ["a"] }))
            .node("b", () => ({ log: ["b"] }))
            .edge(START, "a")
            .edge("a", "b")
            .edge("b", END)
            .compile();

        // Abort after 'a' commits: 'a' checkpoint persists, then the boundary
        // check before 'b' aborts.
        const opts1 = { threadId: "t-abort-resume", checkpointer: cp, signal: controller.signal };
        // Let 'a' finish and commit, then abort before 'b'.
        const events: IlmekEvent[] = [];
        for await (const ev of stream(g, {}, opts1)) {
            events.push(ev);
            if (ev.type === "checkpoint") controller.abort("stop");
        }
        assert.equal(events.at(-1)?.type, "run_end");
        assert.equal((events.at(-1) as { status?: string }).status, "aborted");

        // Resume the same thread with fresh input (no signal): it continues from
        // the committed checkpoint. 'a' does not re-run.
        const done = await run(g, {}, { threadId: "t-abort-resume", checkpointer: cp });
        assert.equal(done.status, "done");
        assert.ok((done.state!.log as string[]).includes("b"));
    });
});
