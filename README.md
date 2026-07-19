# ilmek

An agent graph runtime: state, nodes, edges, checkpointed memory, and **durable**
human-in-the-loop.

Ilmek is not a server. It knows nothing about transports, chat protocols or HTTP
— [botiva](https://github.com/AimTune/botiva) adapts it behind its `Runtime`
port, and it works just as well for batch jobs and background workflows.

> **İlmek** (Turkish, *il-MEK*) is a stitch — the single loop pulled through the
> last one. Each stitch holds the one before it, which is why work survives being
> put down and picked up again. A journaled step is an ilmek: made once, and the
> run resumes on top of it.

**[MODEL.md](MODEL.md) is the normative spec.** The implementation reproduces it
exactly; this README is the tour.

## Repository layout

Language-first: each language is a self-contained tree with its own tooling, and
providers (checkpointers, and later stores/adapters) are grouped by category
under it. The spec and conformance list sit at the root, cross-cutting.

```
MODEL.md                          normative spec (language-neutral)
conformance/                      the scenario list every port must pass
ts/                               TypeScript — reference implementation
  packages/
    core/                         @ilmek/core — the engine. zero deps.
    checkpointers/
      sqlite/                     @ilmek/checkpoint-sqlite
      postgres/                   @ilmek/checkpoint-postgres
      …                           (redis → a sibling here)
  examples/                       runnable demos (@ilmek/examples)
dotnet/                           .NET port
  src/Ilmek.Core/                 the engine. no third-party deps.
  src/Ilmek.Checkpointer.Sqlite/  durable threads in a file
  test/                           the conformance suite (xUnit)
  examples/Ilmek.Examples/        the same demos, same output
```

Two implementations today, both green against the same
[conformance](conformance/) list — and the demos print identical output, which is
what having a spec is for. Go/Elixir can follow the same way.

| | check | dev guide |
|---|---|---|
| TypeScript (reference) | `cd ts && pnpm check` | [ts/README.md](ts/README.md) |
| .NET | `cd dotnet && dotnet test Ilmek.sln` | [dotnet/README.md](dotnet/README.md) |

---

## Why this exists

Every graph engine that supports human-in-the-loop resumes a paused node by
**re-executing it from the top**. So everything above the pause runs again:

```ts
.node("checkout", async (state) => {
    const order = Orders.create(state.cart);   // ← runs AGAIN on resume. Second order.
    const ok = await interrupt({ question: "Charge?" });
    Payments.charge(order, ok);
})
```

LangGraph documents this as a rule for *you* to obey — *"a side effect belongs
after the pause gating it; everything before it re-runs."* That works until a
node has two pauses, or a pause inside a loop, or an effect that genuinely must
happen before the question can be asked.

Ilmek makes it the engine's problem. Wrap each effect in a `step` and the journal
replays its recorded result instead of calling it again:

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

That is what *"resume from the line"* means here: not a restored call stack —
**an effect that cannot happen twice**. See it run:

```bash
cd ts && node examples/checkout.ts     # 💳 create_order → ⏸ paused → 💰 charge
```

## The one idea

**An interrupt is a step whose value comes from a human instead of a function.**

Both live in the same journal under the same replay rules, so the whole HITL
feature set falls out instead of being special-cased:

- multiple pauses in one node — each key resolves once, replay fast-forwards
- pauses inside loops — give the key a stable name (`` `approve:${item.id}` ``)
- concurrent pauses across parallel nodes, each resolving to its own answer
- effects before a pause never re-run
- an interrupt is a **first-class event**, never an exception you have to sniff
  out of an error string

One wrinkle worth knowing up front (MODEL.md §6.1): a pending interrupt has both
a `key` (unique within its task) and an `id` (`"node:key"`, unique within the
thread). **Answer by `id`.** Two nodes pausing in the same superstep both journal
`interrupt#0`, so keying answers by `key` silently hands them the same one.

## The contract

> A node body must be deterministic **modulo steps**. Every side effect and every
> nondeterministic read — clock, RNG, uuid, network, DB, LLM — goes in a step.

Obey it and replay is invisible. Violate it and **strict mode** (on by default in
dev/test) names the key that diverged, instead of letting a silent double-charge
reach production.

## Quick start

```ts
const g = graph("support")
    .channel("messages", channel.append<string>())   // widens the state type
    .node("agent", async (state, ctx) => ({ messages: ["hi"] }))
    .edge(START, "agent")
    .edge("agent", END)
    .compile();

const { status, state } = await run(g, { messages: ["hi"] });

for await (const ev of stream(g, { messages: ["hi"] })) console.log(ev);

const opts = { threadId: "conv-42", checkpointer: new InMemoryCheckpointer() };
const paused = await run(g, { messages: ["buy"] }, opts);   // status: "interrupted"
const done = await resume(g, "yes", opts);                  // status: "done"
```

Each `.channel()` widens the builder's state type, so `state` and the update a
node returns are both checked against exactly the channels declared so far — a
typo'd channel is a compile error.

## Graphs are data

A graph is always constructible from a serializable spec — normative from day
one (MODEL.md §9), because retrofitting it is expensive. This is the foundation
for a drag-and-drop builder: a CRUD app over a JSON document plus a registry
browser. Nothing in the engine knows the builder exists.

```ts
const spec = {
    name: "support",
    channels: { messages: { reducer: "append" } },
    nodes: [{ id: "agent", type: "llm", config: { model: "claude-opus-4-8" } }],
    edges: [{ from: "__start__", to: "agent" }, { from: "agent", to: "__end__" }],
};

const g = fromSpec(spec, registry).compile();
assert.deepEqual(toSpec(g), spec);   // round-trip is a conformance test
```

Two rules keep stored graphs safe: a stored spec **never carries executable
text** (`when` is a declarative predicate, no eval path), and `toSpec()`
**refuses** to serialize what a document cannot honestly hold — a code router, an
anonymous node type, a hand-written guard.

## Control flow & resilience

Beyond static edges and routers, three primitives cover dynamic agent shapes
(MODEL.md §14–§16):

```ts
// §14 send — map-reduce: one worker per item, each with its OWN input state
.router("plan", (state) => state.items.map((item) => send("worker", { item })))

// §15 command — a node decides its own next hop, seeing the update it just wrote
.node("agent", (state) => command({ update: { messages: [reply] }, goto: reply.done ? END : "tools" }))

// §16 retry — and because completed steps are journaled, retries are SAFE:
.node("charge_then_call", fn, { retry: { maxAttempts: 3, backoffMs: 200, factor: 2 } })
```

That last one is where the journal earns its keep: a node that charges a card in
one `ctx.step` and then hits a flaky API in the next retries the API call
**without charging twice** — the completed step replays from the journal instead
of re-running. Same guarantee interrupts rely on, turned toward failure.
`pnpm demo:mapreduce` runs all three together.

## Streaming

*(MODEL.md §10.)* A run is one canonical stream of typed events,
each carrying a monotonic `seq` (reconnect after your last-seen one) and an `ns`
namespace path (`[]` today, reserved for subgraphs). Over that one stream you get
the LangGraph-style `stream_mode` views by **projecting** — no second stream:

```ts
// token-by-token, plus the committed update, multiplexed through one pass
for await (const part of streamModes(g, { prompt }, ["messages", "updates"])) {
    if (part.mode === "messages") process.stdout.write((part.data as TokenChunk).text);
}
```

Modes: `values` (full state per superstep) · `updates` (`{node: update}`) ·
`custom` (every `ctx.emit`) · `messages` (token deltas) · `debug` (raw events).
`project(event, modes)` is pure, so the same filter runs on a live stream, a
`resumeStream`, or a reconnect buffer.

**Tokens** ride a transient side channel: `ctx.emitToken(text, meta?)`. They are
*not* journaled — a resumed node re-streams its tokens, while only `ctx.step`
values are memoized. So "show your work again on resume, but never redo the
side effects" is the default.

**Cancellation** is an `AbortSignal`, checked at each superstep boundary; the run
ends `aborted` with the last checkpoint intact (it resumes cleanly, never rolls
back). The same signal reaches nodes as `ctx.signal` to forward into their own
awaits. Run `pnpm demo:stream` to see tokens stream and a mid-stream cancel.

## Status

Green against the [conformance](conformance/) list in both languages:
TypeScript 176 tests (`cd ts && pnpm check`), .NET 28 (`cd dotnet && dotnet test Ilmek.sln`).

| capability | TS | .NET |
|---|---|---|
| channels + reducers, graph/nodes/edges, routers & guards | ✅ | ✅ |
| supersteps: concurrent tasks, task-order reduce, atomic checkpoints | ✅ | ✅ |
| the journal: durable steps, strict-mode divergence detection | ✅ | ✅ |
| interrupts: multi-pause, loops, concurrent, resume guardrails (§6) | ✅ | ✅ |
| streaming: `seq`/`ns` envelope, projection modes, tokens, abort (§10) | ✅ | ✅ |
| fan-out `send` (§14), node-directed `command` (§15), safe `retry` (§16) | ✅ | ✅ |
| graphs-as-data round-trip + declarative predicates (§9) | ✅ | ✅ |
| in-memory checkpointer (in core) | ✅ | ✅ |
| **SQLite** checkpointer — durable, single file | ✅ | ✅ |
| **Postgres** checkpointer | ✅ | ⬜ |

Next, roughly in order:

| | |
|---|---|
| ⬜ | `Ilmek.Checkpointer.Postgres` (Npgsql) — .NET parity for multi-process threads |
| ⬜ | a Redis checkpointer (sibling under `ts/packages/checkpointers/`) |
| ⬜ | botiva `Runtime` adapter — ilmek event stream → `AgentEvent` |
| ⬜ | subgraphs + `ns` streaming (the envelope already carries the namespace) |
| ⬜ | cross-thread `Store` for long-term memory (distinct from the per-thread checkpointer) |

Toolchain and debugging guides live in [ts/README.md](ts/README.md) and
[dotnet/README.md](dotnet/README.md). The fastest way to feel the model:

```bash
cd ts && pnpm build && pnpm --filter @ilmek/examples demo   # interactive
cd dotnet && dotnet run --project examples/Ilmek.Examples   # same thing, C#
```

Pause, answer, and watch `create_order` run exactly once.
