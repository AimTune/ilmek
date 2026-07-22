---
id: concepts
title: Concepts — the one idea
sidebar_label: Concepts
sidebar_position: 3
---

# Concepts

## The problem

Every graph engine that supports human-in-the-loop resumes a paused node by
**re-executing it from the top**. So everything above the pause runs again:

```ts
.node("checkout", async (state) => {
  const order = Orders.create(state.cart);   // ← runs AGAIN on resume. Second order.
  const ok = await interrupt({ question: "Charge?" });
  Payments.charge(order, ok);
})
```

LangGraph documents this as a rule for *you* to obey — *a side effect belongs
after the pause gating it; everything before it re-runs.* That works until a node
has two pauses, or a pause inside a loop, or an effect that genuinely must happen
before the question can be asked.

## The fix: the journal

ilmek makes it the engine's problem. Wrap each effect in a `step`, and the
journal replays its recorded result instead of calling it again:

```ts
.node("checkout", async (state, ctx) => {
  // Called once, ever. On the resume pass this returns the journaled order.
  const order = await ctx.step("create_order", () => Orders.create(state.cart));

  // First pass: the task halts here. Resume pass: returns the human's answer.
  const ok = await ctx.interrupt<string>({ question: `Charge ${order.total}?` });

  await ctx.step("charge", () => Payments.charge(order, ok));
  return { log: ["done"] };
})
```

That is what *resume from the line* means here: not a restored call stack —
**an effect that cannot happen twice**.

## The one idea

> **An interrupt is a step whose value comes from a human instead of a function.**

Both live in the same journal under the same replay rules, so the whole HITL
feature set falls out instead of being special-cased:

- **multiple pauses in one node** — each key resolves once, replay fast-forwards
- **pauses inside loops** — give the key a stable name (`` `approve:${item.id}` ``)
- **concurrent pauses across parallel nodes**, each resolving to its own answer
- **effects before a pause never re-run**
- an interrupt is a **first-class event**, never an exception you have to sniff
  out of an error string

## The contract

> A node body must be deterministic **modulo steps**. Every side effect and every
> nondeterministic read — clock, RNG, uuid, network, DB, LLM — goes in a step.

Obey it and replay is invisible. Violate it and **strict mode** (on by default in
dev/test) names the key that diverged, instead of letting a silent double-charge
reach production. See [The journal](/model/journal) for the divergence rules.

## A wrinkle worth knowing up front

A pending interrupt has both a `key` (unique within its task) and an `id`
(`"node:key"`, unique within the thread). **Answer by `id`.** Two nodes pausing in
the same superstep both journal `interrupt#0`, so keying answers by `key`
silently hands them the same one. This is covered in detail in
[Interrupts & resume](/model/interrupts#id-vs-key).

## Where the journal earns its keep

The same replay that powers interrupts also makes **retries safe**. A node that
charges a card in one `ctx.step` and then hits a flaky API in the next retries the
API call *without charging twice* — the completed step replays from the journal
instead of re-running:

```ts
.node("charge_then_call", fn, { retry: { maxAttempts: 3, backoffMs: 200, factor: 2 } })
```

One idea — a journaled step — turned toward a human gives you interrupts; turned
toward failure gives you safe retries. → [Retry & resilience](/control-flow/retry).
