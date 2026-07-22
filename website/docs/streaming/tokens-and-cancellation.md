---
id: tokens-and-cancellation
title: Tokens & cancellation
sidebar_label: Tokens & cancellation
sidebar_position: 2
---

# Tokens & cancellation

## Tokens

*Normative: [MODEL.md §10.2](/reference/spec).*

ilmek is LLM-agnostic — the core never calls a model. But "stream the answer as
it is generated" is universal, so a **token** has a fixed shape:

```ts
{ type: "token", text, meta? }
```

A node streams one with `ctx.emitToken(text, meta?)` — sugar for
`ctx.emit(token(...))`. Tokens ride the same transient channel as `emit` and are
therefore **not journaled**: on replay a node re-streams its tokens, while only the
values it commits through [`ctx.step`](/model/journal) are memoized.

So the default is exactly *"show your work again on resume, but never redo the side
effects."* The [`messages` projection mode](/streaming/projection-modes) is
precisely "the `custom` payloads that are tokens".

## Cancellation

*Normative: [MODEL.md §10.3](/reference/spec).*

A run may be given an `AbortSignal`. The engine checks it at every **superstep
boundary**: an aborted run stops there and ends with
`run_end { status: "aborted", reason }`.

- The last committed checkpoint **stands** — abort stops the stream, it does not
  roll back — so the thread resumes cleanly later.
- The same signal reaches node code as **`ctx.signal`**. A node must forward it to
  its own long awaits (an LLM call, a `fetch`) for cancellation to interrupt work
  already in flight.
- The engine **never force-kills** a running task; cancellation is only as
  responsive as the node's own signal handling.

```ts
const controller = new AbortController();
const events = stream(g, input, { signal: controller.signal });
// ... later:
controller.abort("user navigated away");
```

Run `pnpm demo:stream` to see tokens stream and a mid-stream cancel land cleanly.
