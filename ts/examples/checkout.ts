// The pitch, executable.
//
//   node examples/checkout.ts              → interactive: you answer at the prompt
//   node examples/checkout.ts --selftest   → scripted "yes", deterministic, exit 0/1
//   echo yes | node examples/checkout.ts   → the prompt reads a pipe just as happily
//
// A node opens an order, pauses for a human, then charges. The interesting part
// is what happens on resume: the node body re-runs from the top, but the order
// is NOT opened a second time.
//
// Debugging (see ../.vscode/launch.json): break on the ctx.step("create_order")
// line and on the ctx.step("charge") line, then walk both runs. On the second
// run the node re-enters from the top and the debugger steps straight over
// create_order's callback — that is the journal, with a stack trace attached.

import { createInterface } from "node:readline/promises";
import { stdin, stdout } from "node:process";

import {
    channel,
    END,
    graph,
    InMemoryCheckpointer,
    resumeKeyed,
    run,
    START,
    type ChannelMap,
    type Pending,
    type Result,
    type StateOf,
} from "@ilmek/core";

const SELFTEST = process.argv.includes("--selftest");

interface Order {
    id: string;
    total: number;
}

interface Question {
    question: string;
    options: string[];
}

// ── stand-ins that shout when called, so double effects are visible ──────────

const calls: string[] = [];

const Fake = {
    createOrder(cart: readonly string[]): Order {
        calls.push("create_order");
        console.log(`   💳 SIDE EFFECT: opening an order for ${JSON.stringify(cart)}`);
        return { id: "ord-9001", total: 249.9 };
    },
    charge(order: Order): string {
        calls.push("charge");
        console.log(`   💰 SIDE EFFECT: charging ${order.total} on ${order.id}`);
        return "charged";
    },
};

// ── the state, in one named place ────────────────────────────────────────────

// Schema-first: the shape lives here, so a node body, the run input, and the
// Result are all checked against the same nameable type instead of re-deriving it.
const CheckoutState = {
    cart: channel.lastWrite<string[]>([]),
    log: channel.append<string>(),
} satisfies ChannelMap;

type CheckoutState = StateOf<typeof CheckoutState>; // { cart: string[]; log: string[] }

// ── the graph ───────────────────────────────────────────────────────────────

const g = graph("checkout", CheckoutState)
    .node("checkout", async (state, ctx) => {
        const order = await ctx.step("create_order", () => Fake.createOrder(state.cart));

        const answer = await ctx.interrupt<string>({
            question: `Charge ${order.total.toFixed(2)}?`,
            options: ["yes", "no"],
        } satisfies Question);

        if (answer !== "yes") return { log: [`order ${order.id} cancelled`] };

        await ctx.step("charge", () => Fake.charge(order));
        return { log: [`order ${order.id} charged`] };
    })
    .edge(START, "checkout")
    .edge("checkout", END)
    .compile();

// ── asking the human ────────────────────────────────────────────────────────

const rl = SELFTEST ? null : createInterface({ input: stdin, output: stdout });

/**
 * Read lines through the async iterator rather than `rl.question()`.
 *
 * `question()` only captures the *next* `line` event after it is called. When
 * several lines arrive in one chunk — a paste, a `printf "a\nb\n"` pipe, a fast
 * typist — readline emits them all while only the first has a listener, and the
 * rest are dropped on the floor. The next `question()` then waits forever for
 * input that already came and went. The iterator buffers instead.
 */
const lines = rl?.[Symbol.asyncIterator]();

async function prompt(text: string): Promise<string> {
    stdout.write(text);
    const next = await lines!.next();

    if (next.done) {
        // stdin closed with a pause still open. Hanging here is what the
        // unsettled top-level await would do; say so and leave.
        console.log("\n\n   ⚠️  stdin ended while a question was still open.");
        console.log("      The pause is durable — rerun to be asked again, or use --selftest.\n");
        rl?.close();
        process.exit(1);
    }

    return next.value.trim().toLowerCase();
}

async function ask(pending: Pending): Promise<string> {
    const { question, options } = pending.payload as Question;

    if (SELFTEST) {
        console.log(`   ❓ ${question}`);
        console.log(`   > yes   (scripted, --selftest)`);
        return "yes";
    }

    for (;;) {
        const raw = await prompt(`   ❓ ${question} [${options.join("/")}] > `);
        if (options.includes(raw)) return raw;

        // Re-asking is the driver's business, not the graph's: the pause is
        // still open and the node has not re-run.
        console.log(`      (type ${options.join(" or ")})`);
    }
}

// ── the driver ──────────────────────────────────────────────────────────────

// Everything below lives in main() rather than at module top level. Top-level
// await makes an ES module itself async, and a rejection there dies as a bare
// "unsettled top-level await" warning with no stack. It also gives the debugger
// nothing but the module frame to point at.
async function main(): Promise<number> {
    const opts = { threadId: "conv-42", checkpointer: new InMemoryCheckpointer() };

    console.log("\n── run 1: user asks to check out ─────────────────────────────");
    let result: Result<typeof CheckoutState> = await run(g, { cart: ["coffee", "mug"] }, opts);
    let round = 1;

    // The generic HITL loop: keep answering until the thread stops asking. It
    // reads the same for one pause or five, because resumeKeyed answers by id —
    // which is exactly why `id` exists (MODEL.md §6.1).
    while (result.status === "interrupted") {
        console.log(`\n── paused: ${result.pending.length} question(s) ──────────────────────────`);
        console.log("   the process could die here. a deploy could happen here.");
        console.log("   the pause lives in the checkpointer, not in memory.\n");

        const answers: Record<string, unknown> = {};
        for (const pending of result.pending) answers[pending.id] = await ask(pending);

        console.log(`\n── run ${++round}: resuming with the human's answer ───────────`);
        result = await resumeKeyed(g, answers, opts);
    }

    rl?.close();

    console.log(`\n   status: ${result.status}`);
    console.log(`   log:    ${JSON.stringify(result.state?.log)}`);

    // ── the point ───────────────────────────────────────────────────────────

    console.log("\n── what actually got called ──────────────────────────────────");
    console.log(`   ${JSON.stringify(calls)}`);

    const createOrderCalls = calls.filter((c) => c === "create_order").length;

    if (createOrderCalls === 1) {
        console.log(`
   ✅ create_order ran ONCE across ${round} runs.

      The node re-ran from the top on every resume — but the journal had
      already paid for create_order, so it returned the recorded order
      instead of opening another one. A pure-replay engine (LangGraph's
      model) would print create_order ${round} times here, and the fix would be
      your problem: "put the side effect below the pause that gates it."
`);
    } else {
        console.log(`\n   ❌ create_order ran ${createOrderCalls} times across ${round} runs — expected 1\n`);
        return 1;
    }

    // Only the scripted run asserts the exact call list: an interactive user may
    // legitimately answer "no", and cancelling is not a failure.
    if (!SELFTEST) return 0;

    const expected = ["create_order", "charge"];
    if (JSON.stringify(calls) === JSON.stringify(expected)) return 0;

    console.log(`   ❌ expected ${JSON.stringify(expected)}, got ${JSON.stringify(calls)}\n`);
    return 1;
}

// Deliberately a promise chain, not top-level await — see main()'s comment.
main().then(
    (code) => process.exit(code),
    (err) => {
        // A real stack, instead of the silent death top-level await gives you.
        console.error("\n   ❌ demo crashed:\n", err);
        rl?.close();
        process.exit(1);
    },
);
