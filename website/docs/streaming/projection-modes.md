---
id: projection-modes
title: Projection modes
sidebar_label: Projection modes
sidebar_position: 1
---

# Projection modes

*Normative: [MODEL.md §10.1](/reference/spec).*

The single typed [event stream](/streaming/overview) is canonical. A consumer may
**project** it into the mode-shaped views popularized by LangGraph's
`stream_mode` — without the engine offering a second stream:

| Mode | Projected from | Yields |
|---|---|---|
| `values` | `state` | full channel state after each superstep |
| `updates` | `node_end` | `{ [node]: update }` per node that ran |
| `custom` | `custom` | each `ctx.emit` payload |
| `messages` | `custom` | just the payloads that are token chunks |
| `debug` | every event | the event itself |

```ts
// token-by-token, plus the committed update, multiplexed through one pass
for await (const part of streamModes(g, { prompt }, ["messages", "updates"])) {
  if (part.mode === "messages") process.stdout.write((part.data as TokenChunk).text);
}
```

## Projection adds no information

Each projected part carries the `seq` and `ns` of the event it came from, so
reconnect and subgraph-grouping still work after projecting. `project(event,
modes)` is a **pure function of one event**, so the same filter runs on a live
stream, a `resumeStream`, or a replay of a reconnect buffer.

Modes at a glance: `values` (full state per superstep) · `updates`
(`{ node: update }`) · `custom` (every `ctx.emit`) · `messages` (token deltas) ·
`debug` (raw events).
