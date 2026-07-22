---
id: getting-started
title: Getting started
sidebar_label: Getting started
sidebar_position: 2
---

# Getting started

Build a graph that pauses for a human and resumes without redoing its work — in
about five minutes.

## Install

```bash
npm install @ilmek/core
```

`@ilmek/core` has **zero runtime dependencies**. Durable threads that outlive the
process are opt-in via a checkpointer package:

```bash
npm install @ilmek/checkpoint-sqlite     # single-process, one file
# or
npm install @ilmek/checkpoint-postgres   # threads shared across processes
```

Requirements: Node ≥ 22.5 for the SQLite checkpointer (it uses the built-in
`node:sqlite`); core itself runs anywhere with modern ESM.

## Your first graph

A graph is a set of **channels** (the state), **nodes** (the work), and **edges**
(the flow). Each `.channel()` widens the builder's state type, so a node update
is checked against exactly the channels declared so far — a typo'd channel is a
compile error.

```ts
import { graph, channel, START, END, run, stream } from "@ilmek/core";

const g = graph("support")
  .channel("messages", channel.append<string>())   // widens the state type
  .node("agent", async (state, ctx) => ({ messages: ["hi"] }))
  .edge(START, "agent")
  .edge("agent", END)
  .compile();

const { status, state } = await run(g, { messages: ["hi"] });
console.log(status, state.messages);   // "done" [ "hi", "hi" ]
```

Prefer events? The same run is one canonical stream:

```ts
for await (const ev of stream(g, { messages: ["hi"] })) console.log(ev);
```

## Add a pause

Give a node a durable thread (a `threadId` + a checkpointer) and it can
**interrupt** — halt, persist, and resume later with a human's answer.

```ts
import { run, resume, InMemoryCheckpointer } from "@ilmek/core";

const opts = { threadId: "conv-42", checkpointer: new InMemoryCheckpointer() };

const paused = await run(g, { messages: ["buy"] }, opts);  // status: "interrupted"
const done = await resume(g, "yes", opts);                 // status: "done"
```

`InMemoryCheckpointer` is enough to feel the model. For a pause that survives a
process restart, swap it for [SQLite](/checkpointers/sqlite) or
[Postgres](/checkpointers/postgres) — nothing else in your code changes.

## The move that matters

Wrap side effects in `ctx.step`. On the resume pass, a completed step returns its
**journaled** value instead of running again:

```ts
const g = graph("checkout")
  .channel("log", channel.append<string>())
  .node("checkout", async (state, ctx) => {
    // Called once, ever. On resume this returns the journaled order.
    const order = await ctx.step("create_order", () => Orders.create(state.cart));

    // First pass halts here; resume pass returns the human's answer.
    const ok = await ctx.interrupt<string>({ question: `Charge ${order.total}?` });

    await ctx.step("charge", () => Payments.charge(order, ok));
    return { log: ["done"] };
  })
  .edge(START, "checkout").edge("checkout", END)
  .compile();
```

That is what *resume from the line* means here: not a restored call stack, but
**an effect that cannot happen twice**.

## Run the demos

Clone the repo and watch it happen — pause, answer, and see `create_order` run
exactly once:

```bash
cd ts && pnpm build
pnpm --filter @ilmek/examples demo          # interactive checkout
pnpm --filter @ilmek/examples demo:stream   # tokens streaming + a mid-stream cancel
pnpm --filter @ilmek/examples demo:mapreduce # fan-out + routing + safe retry
```

The same demos exist in C# with identical output:

```bash
cd dotnet && dotnet run --project examples/Ilmek.Examples
```

## Next

- [**Concepts**](/concepts) — why replay is invisible, and the contract that
  keeps it that way.
- [**Interrupts & resume**](/model/interrupts) — multiple pauses, loops, and the
  `id` vs `key` rule you should know up front.
