---
id: state-and-channels
title: State & channels
sidebar_label: State & channels
sidebar_position: 1
---

# State & channels

*Normative: [MODEL.md §2](/reference/spec).*

State is a map of named **channels**. A node returns a **partial update**; the
engine folds each key into its channel via that channel's **reducer**. A node
never mutates state and never sees another node's update within the same
superstep — see [Supersteps](/model/supersteps).

Each `.channel()` also widens the builder's state type, so both `state` and the
update a node returns are checked against exactly the channels declared so far. A
typo'd channel is a compile error, not a runtime surprise.

```ts
const g = graph("support")
  .channel("messages", channel.append<string>())   // list of strings
  .channel("cart", channel.lastWrite<Cart>())       // last write wins
  .node("agent", async (state, ctx) => ({ messages: ["hi"] }))
  // ...
```

## Reducers

A reducer has the signature `(current, incoming) => next`. `current` is absent on
the first write. Every implementation provides these built-ins:

| Reducer | Behaviour |
|---|---|
| `last_write` | `incoming` wins. **The default.** |
| `append` | List concat: `current ++ wrap(incoming)`. |
| `merge` | Shallow map merge; `incoming` wins per key. |
| custom | Any `(current, incoming) => next` function. |

## The conflict rule

When two tasks in the same superstep write the same channel, the reducer folds
both. For a non-commutative reducer (`last_write`, `append`), the fold order is
**task order** — the order nodes appear in the graph's node list, *not*
completion order. This keeps a superstep deterministic regardless of how tasks
happen to be scheduled.

## Serializability

Channels **must be JSON-serializable** — they are checkpointed. A non-serializable
value (a PID, socket, or stream handle) belongs in a
[step result](/model/journal#keys) only if the serializer round-trips it; return
an id and re-resolve it instead.
