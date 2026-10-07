# @ilmek/core

An agent graph runtime: state, nodes, edges, checkpointed memory, and **durable**
human-in-the-loop. Zero runtime dependencies.

The one idea: a node that pauses for a human re-runs from the top on resume, so a
pure-replay engine repeats every side effect before the pause. ilmek wraps each
effect in `ctx.step(...)` — the journal replays its recorded result instead of
calling it again. An interrupt is just a step whose value comes from a human.

```sh
npm install @ilmek/core
```

```ts
import { graph, channel, START, END, run, resume, InMemoryCheckpointer } from "@ilmek/core";

// Stand-in services — swap for your real order + payment code.
const Orders = { create: () => ({ id: "ord-9001", total: 249.9 }) };
const Payments = { charge: (order: { id: string }, ok: string) => "charged" };

const g = graph("checkout")
    .channel("log", channel.append<string>())
    .node("checkout", async (state, ctx) => {
        const order = await ctx.step("create_order", () => Orders.create());   // once, ever
        const ok = await ctx.interrupt<string>({ question: "Charge?" });
        await ctx.step("charge", () => Payments.charge(order, ok));
        return { log: ["done"] };
    })
    .edge(START, "checkout").edge("checkout", END)
    .compile();

const opts = { threadId: "conv-42", checkpointer: new InMemoryCheckpointer() };
await run(g, {}, opts);        // { status: "interrupted" }
await resume(g, "yes", opts);  // { status: "done" }
```

Docs: <https://ilmek.aimtune.dev> · normative spec: [MODEL.md](https://github.com/AimTune/ilmek/blob/main/MODEL.md).
Durable checkpointers: `@ilmek/checkpoint-sqlite`, `@ilmek/checkpoint-postgres`.
Integrations: `@ilmek/skills`, `@ilmek/mcp`, `@ilmek/a2a`.
