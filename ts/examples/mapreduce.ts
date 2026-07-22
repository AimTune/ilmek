// Dynamic fan-out, node-directed routing, and safe retries in one run.
//   node examples/mapreduce.ts
//
// A planner fans one job out to N workers (MODEL.md §14 send), each worker
// retries a flaky call without redoing its committed work (§16 retry + the
// journal), and a router node decides via command whether to finish or loop
// (§15). No LLM, no network — a fake flaky service makes the retry visible.

import {
    channel,
    command,
    END,
    graph,
    send,
    START,
    stream,
    type ChannelMap,
    type RetryPolicy,
    type StateOf,
} from "@ilmek/core";

// A service that fails the first `failsFor` calls per key, then succeeds.
const attempts = new Map<string, number>();
function flakyUppercase(key: string, text: string, failsFor: number): string {
    const n = (attempts.get(key) ?? 0) + 1;
    attempts.set(key, n);
    if (n <= failsFor) throw new Error(`503 on ${key} (attempt ${n})`);
    return text.toUpperCase();
}

const retry: RetryPolicy = { maxAttempts: 4, backoffMs: 5, factor: 2 };

// Schema-first state: words to shout in, results out (accumulated), a round counter.
const ShoutState = {
    words: channel.lastWrite<string[]>([]),
    shouted: channel.append<string>(),
    rounds: channel.lastWrite<number>(0),
} satisfies ChannelMap;

type ShoutState = StateOf<typeof ShoutState>; // { words: string[]; shouted: string[]; rounds: number }

const g = graph("shout", ShoutState)
    // 1) fan out: one worker task per word, each with its own payload (§14)
    .node("plan", () => ({}))
    // 2) worker: reads its send payload, retries the flaky call safely (§16)
    .node(
        "worker",
        (job: { word: string; failsFor: number }, ctx) => {
            const out = flakyUppercase(job.word, job.word, job.failsFor);
            ctx.emit({ worked: job.word });
            return { shouted: out };
        },
        { retry },
    )
    // 3) gate: node-directed routing — loop once, then finish (§15)
    .node("gate", (state) =>
        state.rounds === 0
            ? command({ update: { rounds: 1, words: ["again"] }, goto: "plan" })
            : command({ goto: END }),
    )
    .edge(START, "plan")
    .router("plan", (state) => state.words.map((word, i) => send("worker", { word, failsFor: i })))
    .edge("worker", "gate")
    .compile();

async function main(): Promise<number> {
    console.log("\n── fan-out with retries, then one routed loop ────────────────");

    let retries = 0;
    let workers = 0;
    let shouted: string[] = [];

    // No checkpointer needed here — one run, no pause. Retries live inside a
    // superstep and never checkpoint (MODEL.md §16).
    for await (const ev of stream(g, { words: ["red", "green", "blue"] })) {
        if (ev.type === "node_retry") {
            retries++;
            console.log(`   ↻ retry ${ev.node} attempt ${ev.attempt}: ${(ev.error as Error).message}`);
        } else if (ev.type === "custom" && (ev.payload as { worked?: string }).worked) {
            workers++;
            console.log(`   ✓ worked ${(ev.payload as { worked: string }).worked}`);
        } else if (ev.type === "run_end" && ev.status === "done") {
            shouted = (ev.state.shouted as string[]) ?? [];
        }
    }

    console.log(`\n   worker successes: ${workers}   retries observed: ${retries}`);
    console.log(`   shouted: ${JSON.stringify(shouted)}`);

    const ok = retries > 0 && shouted.length === 4 && shouted.every((s) => s === s.toUpperCase());
    console.log(
        ok
            ? `\n   ✅ 4 words shouted across two rounds; every flaky call retried to success, and no\n` +
                  `      word was processed twice despite the retries (the journal held each worker's step).\n`
            : `\n   ❌ unexpected: ${JSON.stringify({ retries, shouted })}\n`,
    );
    return ok ? 0 : 1;
}

// Deliberately a promise chain, not top-level await — a rejection here dies with
// a real stack instead of an unsettled-top-level-await warning.
main().then(
    (code) => process.exit(code),
    (err) => {
        console.error("\n   ❌ demo crashed:\n", err);
        process.exit(1);
    },
);
