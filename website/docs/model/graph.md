---
id: graph
title: Graph — nodes, edges, routers
sidebar_label: Graph
sidebar_position: 2
---

# Graph

*Normative: [MODEL.md §3](/reference/spec).*

A graph is a set of **channels** (the state), **nodes** (the work), and **edges**
(the flow). It is always **data**: the compiled form is derived from a
[spec](/graphs-as-data), never the other way round.

```ts
const g = graph("support")
  .channel("messages", channel.append<string>())
  .channel("cart", channel.lastWrite<Cart>())
  .node("agent", Agent.run)
  .node("checkout", Checkout.run)
  .edge(START, "agent")
  .edge("agent", "checkout", (state) => state.intent === "buy")   // conditional
  .edge("checkout", END)
  .compile();
```

## Nodes

A node is an `async (state, ctx) => update` function. It reads `state`, does its
work through [`ctx`](/model/journal), and returns a partial update that the engine
folds into channels. Two node names are reserved:

- **`START`** (`__start__`) — the virtual entry.
- **`END`** (`__end__`) — the virtual exit.

Both are implicit and have no body.

## Edges

A plain edge connects two nodes unconditionally. A **conditional edge** takes a
predicate; when it passes, the edge is taken:

```ts
.edge("agent", "checkout", (state) => state.intent === "buy")
```

## Routers

A **router** returns the next hop dynamically — a node name, a list of names
(fan-out), or `END`:

```ts
.router("plan", (state) => state.needsTool ? "tools" : "respond")
```

A router (and any conditional predicate) **must be pure**. It runs inside
*planning*, not inside a task — so it has **no journal** and must not perform side
effects. **Route on state; compute in nodes.** For data-driven fan-out, see
[`send`](/control-flow/send); for a node that decides its own next hop after
seeing its own update, see [`command`](/control-flow/command).

## Graphs are data

Because a graph compiles from a serializable spec, it round-trips:

```ts
const g = fromSpec(spec, registry).compile();
assert.deepEqual(toSpec(g), spec);   // a conformance test
```

This is the foundation for a drag-and-drop builder — a CRUD app over a JSON
document. Nothing in the engine knows the builder exists. See
[Graphs as data](/graphs-as-data).
