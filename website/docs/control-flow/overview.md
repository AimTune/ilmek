---
id: overview
title: Control flow
sidebar_label: Overview
sidebar_position: 0
---

# Control flow

Beyond static edges and routers ([Graph](/model/graph)), three primitives cover
the dynamic shapes real agents take. All three build on the
[journal](/model/journal), which is why the map-reduce, self-routing, and retry
stories all stay free of double-effects.

| Primitive | Spec | Question it answers |
|---|---|---|
| [`send`](/control-flow/send) | §14 | Run one node many times in parallel, each with its own input. |
| [`command`](/control-flow/command) | §15 | Let a node decide its own next hop *after* seeing its update. |
| [`retry`](/control-flow/retry) | §16 | Re-run a flaky node safely, without repeating committed effects. |

```ts
// §14 send — map-reduce: one worker per item, each with its OWN input state
.router("plan", (state) => state.items.map((item) => send("worker", { item })))

// §15 command — a node decides its own next hop, seeing the update it just wrote
.node("agent", (state) => command({ update: { messages: [reply] }, goto: reply.done ? END : "tools" }))

// §16 retry — and because completed steps are journaled, retries are SAFE
.node("charge_then_call", fn, { retry: { maxAttempts: 3, backoffMs: 200, factor: 2 } })
```

`pnpm demo:mapreduce` runs all three together.
