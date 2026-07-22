---
id: overview
title: Checkpointers
sidebar_label: Overview
sidebar_position: 0
---

# Checkpointers

*Normative: [MODEL.md §7](/reference/spec).*

A checkpointer is the **memory port** — one interface, many backends. It is what
makes a [pause](/model/interrupts) or a resumable run outlive the process that
created it. Give a run a `threadId` and a checkpointer and every superstep
persists atomically.

```ts
const opts = { threadId: "conv-42", checkpointer };
await run(g, input, opts);      // persists as it goes
await resume(g, answer, opts);  // picks up from the last checkpoint
```

## The port

```
put(threadId, checkpoint)            -> void
get(threadId, checkpointId | null)   -> checkpoint | null    // null = latest
list(threadId, opts)                 -> checkpoint[]          // newest first
putJournal(taskId, entries)          -> void
getJournal(taskId)                   -> entry[]
deleteThread(threadId)               -> void
```

A **checkpoint** records `{ id, parentId, threadId, channels, next, pending, step,
ts }`. Because every checkpoint names its parent, a thread is a **tree, not a
line**: resuming from a non-latest checkpoint forks a branch (time travel /
what-if). Implementations must not assume a single chain.

:::note ilmek checkpoints are ilmek's own
They are **not** botiva state — botiva keeps its transcript and `conv:*` keyspace
independently.
:::

## Available backends

| Package | Backend | Use when | Status |
|---|---|---|---|
| built into `@ilmek/core` | `InMemoryCheckpointer` | tests, demos, single run | TS ✅ · .NET ✅ |
| [`@ilmek/checkpoint-sqlite`](/checkpointers/sqlite) | one SQLite file | single-process durability | TS ✅ · .NET ✅ |
| [`@ilmek/checkpoint-postgres`](/checkpointers/postgres) | Postgres | threads shared across processes | TS ✅ · .NET ⬜ |

`InMemoryCheckpointer` is enough to feel the model. Swap in SQLite or Postgres
and nothing else in your code changes.
