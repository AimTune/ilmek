---
id: retry
title: Retry & resilience
sidebar_label: retry
sidebar_position: 3
---

# `retry` — safe by default

*Normative: [MODEL.md §16](/reference/spec).*

A node may declare a retry policy:

```ts
.node("call_api", fn, {
  retry: { maxAttempts: 3, backoffMs: 200, factor: 2, retryOn: isTransient },
})
```

When the node throws a non-interrupt error, the engine re-invokes it up to
`maxAttempts` times — waiting `backoffMs * factor^(n-1)` between attempts,
optionally gated by `retryOn(error)`. Each retry emits a
`node_retry { node, attempt, error }` event before the next attempt.

## Why ilmek retries are safe

The retry re-runs the **node body**, but every `ctx.step` it already completed
returns from the [journal](/model/journal) instead of re-executing. So a node that
charged a card in one step and then hit a flaky API in the next retries the API
call **without charging twice**:

```ts
.node("charge_then_call", async (state, ctx) => {
  await ctx.step("charge", () => Payments.charge(state.order));   // journaled — runs once
  return ctx.step("notify", () => Api.callFlaky(state.order));    // retried on failure
}, { retry: { maxAttempts: 3, backoffMs: 200, factor: 2 } })
```

This is the same guarantee [interrupts](/model/interrupts) rely on, turned toward
failure instead of a human — and it is why retries here are safe by default where
a pure-replay engine's are not.

## Scope

- Retries are **within a single superstep**; they do not create checkpoints.
- If all attempts are exhausted, the node fails normally and the run ends
  `error`.
- An [`AbortSignal`](/streaming/tokens-and-cancellation) that fires between
  attempts stops the retry loop.

Run `pnpm demo:mapreduce` to watch flaky workers retry to success while no item
is ever processed twice.
