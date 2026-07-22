// Streaming, end to end.  Run:  node examples/streaming.ts
//
// Shows the three things "streaming support" actually means (MODEL.md §10):
//   1. token streaming   — ctx.emitToken, projected by the "messages" mode
//   2. projection modes  — one canonical stream, viewed as values/updates/messages
//   3. cancellation      — an AbortSignal stops the run at a superstep boundary
//
// No LLM and no network: a fake "model" yields characters on a timer. Swap it
// for a real streaming client and nothing else changes — a token is a token.

import {
    channel,
    END,
    graph,
    START,
    streamModes,
    type ChannelMap,
    type StateOf,
    type TokenChunk,
} from "@ilmek/core";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

// A stand-in streaming model: yields the answer one word at a time, and honors
// the AbortSignal exactly the way a real fetch()-based client would.
async function* fakeModel(prompt: string, signal?: AbortSignal): AsyncGenerator<string> {
    const words = `you asked: "${prompt}" — here is a made-up answer streamed word by word`.split(" ");
    for (const w of words) {
        if (signal?.aborted) return;
        await sleep(70);
        yield w + " ";
    }
}

// Schema-first state: the shape lives in one named place, so node bodies and the
// streamed updates are all typed against it.
const AssistantState = {
    prompt: channel.lastWrite<string>(""),
    answer: channel.lastWrite<string>(""),
} satisfies ChannelMap;

type AssistantState = StateOf<typeof AssistantState>; // { prompt: string; answer: string }

const g = graph("assistant", AssistantState)
    .node("respond", async (state, ctx) => {
        let answer = "";
        // Forward ctx.signal into the model so cancellation reaches the actual
        // blocking work, not just the superstep boundary.
        for await (const chunk of fakeModel(state.prompt, ctx.signal)) {
            answer += chunk;
            ctx.emitToken(chunk, { node: "respond" }); // transient side channel — not journaled
        }
        return { answer: answer.trim() };
    })
    .edge(START, "respond")
    .edge("respond", END)
    .compile();

// ── 1 + 2: stream tokens live, and watch the other modes in the same pass ────

async function demoTokens(): Promise<void> {
    console.log("\n── streaming tokens (messages mode) ──────────────────────────");
    process.stdout.write("   ");

    for await (const part of streamModes(g, { prompt: "what is ilmek?" }, ["messages", "updates"])) {
        if (part.mode === "messages") {
            process.stdout.write((part.data as TokenChunk).text);
        } else {
            // updates arrive once, after the node commits its answer channel.
            console.log(`\n\n   [updates] node committed: ${JSON.stringify(part.data)}`);
        }
    }
}

// ── 3: cancel mid-stream ─────────────────────────────────────────────────────

async function demoCancel(): Promise<void> {
    console.log("── cancelling mid-stream after 300ms ─────────────────────────");
    process.stdout.write("   ");

    const controller = new AbortController();
    setTimeout(() => controller.abort("caller changed their mind"), 300);

    let tokens = 0;
    for await (const part of streamModes(
        g,
        { prompt: "this answer gets cut off" },
        ["messages"],
        { signal: controller.signal },
    )) {
        process.stdout.write((part.data as TokenChunk).text);
        tokens++;
    }

    console.log(`\n\n   stopped after ${tokens} tokens — the node saw ctx.signal and returned early.\n`);
}

async function main(): Promise<number> {
    await demoTokens();
    await demoCancel();
    console.log("✅ tokens streamed, modes multiplexed, cancellation honored.\n");
    return 0;
}

main().then(
    (code) => process.exit(code),
    (err) => {
        console.error("\n   ❌ demo crashed:\n", err);
        process.exit(1);
    },
);
