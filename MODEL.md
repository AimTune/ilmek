# Ilmek Execution Model — `ilmek/1`

The normative, language-neutral contract for the ilmek graph engine. The
**TypeScript implementation in `ts/packages/core` is the reference**; the
**.NET port in `dotnet/src/Ilmek.Core`** reproduces it, verified against the same
[`conformance/`](conformance/) list. The document stays language-neutral so
further ports (Go, Elixir) can follow. Where the spec shows a signature it is
TypeScript unless noted; §11 maps every name to .NET.

Ilmek is an **agent graph runtime**: state, nodes, edges, checkpointed memory,
and durable human-in-the-loop. It is *not* a server and knows nothing about
transports, chat protocols, or HTTP. Botiva (the `botiva/1` wire protocol)
adapts ilmek behind its `Runtime` port; ilmek itself is equally usable for batch
jobs and background workflows.

> Naming: **ilmek** is Turkish for a stitch — the single loop pulled through the
> last one. Each stitch holds the one before it, which is why work survives being
> put down and picked up again. A journaled step is an ilmek: made once, and the
> run resumes on top of it.

```
   ┌──────────────────────── Graph (static, serializable) ────────────────────┐
   │  nodes · edges · channels                                               │
   └────────────────────────────────┬───────────────────────────────────────-┘
                                    │  compile
   ┌────────────────────────────────▼───────────────────────────────────────-┐
   │  Engine — supersteps: plan → run tasks → reduce → checkpoint            │
   │           ├─ Journal      (per task: memoized steps + interrupt answers)│
   │           └─ Checkpointer (per thread: state + pending tasks)           │
   └────────────────────────────────┬───────────────────────────────────────-┘
                                    │  events
                              consumer (botiva adapter, CLI, test, …)
```

---

## 1. Vocabulary

| Term | Meaning |
|------|---------|
| **Graph** | Static, serializable description: channels, nodes, edges. No behaviour. |
| **Node** | A named function `(state, ctx) -> update`. The unit of work. |
| **Edge** | Static (`a → b`) or conditional (`a → f(state, ctx)`). |
| **Channel** | One named slot of state, with a **reducer** that folds updates in. |
| **Thread** | One long-lived conversation/workflow instance. Owns checkpoints. Maps to botiva `conversationId`. |
| **Run** | One invocation of the graph on a thread. A thread has many runs (an interrupt ends a run; the answer starts the next). |
| **Superstep** | One BSP round: a set of tasks runs concurrently, then updates are reduced and a checkpoint is written. |
| **Task** | One (node, superstep) pair — a single node execution. Owns a **journal**. |
| **Step** | A journaled unit of side effect inside a task. Memoized across replays. |
| **Journal** | The per-task record of completed steps and interrupt answers. |
| **Checkpoint** | The per-thread record of channel values + which tasks come next. |

A **thread** has many **runs**; a run has many **supersteps**; a superstep has
many **tasks**; a task has many **steps**.

## 2. State & channels

State is a map of named channels. A node returns a **partial update**; the
engine folds each key into its channel via that channel's **reducer**. A node
never mutates state and never sees another node's update within the same
superstep (§4).

Reducer signature: `(current, incoming) -> next`. `current` is absent on the
first write.

Built-in reducers every implementation MUST provide:

| Reducer | Behaviour |
|---------|-----------|
| `last_write` | `incoming` wins. **The default.** |
| `append` | List concat: `current ++ List.wrap(incoming)`. |
| `merge` | Shallow map merge, `incoming` wins per key. |
| custom | Any `(current, incoming) -> next` function. |

**Conflict rule:** when two tasks in the same superstep write the same channel,
the reducer folds both. Fold order for a non-commutative reducer (e.g.
`last_write`, `append`) is **task order**: the order nodes appear in the
graph's node list, not completion order. This keeps a superstep deterministic
regardless of scheduling.

Channels MUST be JSON-serializable — they are checkpointed. Non-serializable
values belong in a step result only if the serializer round-trips them (§5.4).

## 3. Graph

```
channel :messages, reducer: :append
channel :cart,     reducer: :last_write

node :agent,    &Agent.run/2
node :checkout, &Checkout.run/2

edge :__start__, :agent
edge :agent, :checkout, when: &(&1.intent == :buy)   # conditional
edge :checkout, :__end__
```

Two reserved node names: `__start__` (virtual entry) and `__end__` (virtual
exit). Both are implicit; neither has a body.

A conditional edge's function returns a node name, a list of node names
(fan-out), or `__end__`. It MUST be pure — it runs inside planning, not inside
a task, and therefore has **no journal** and MUST NOT perform side effects.
Route on state; compute in nodes.

A graph is **data** (§9): the compiled form is derived from a spec, never the
other way round.

## 4. Execution — supersteps (BSP)

```
loop:
  1. PLAN     — resolve edges from the last checkpoint → the set of next tasks
  2. RUN      — execute all tasks of this superstep CONCURRENTLY
  3. REDUCE   — fold every update into channels, in task order (§2)
  4. CHECKPOINT — persist {channels, next tasks} atomically
  5. halt if: no next tasks · an interrupt is pending · recursion limit hit
```

Guarantees:

* Tasks within a superstep are **isolated**: each sees the state as of the
  previous checkpoint. A task never observes a sibling's update.
* A superstep is **atomic**: either all updates are reduced and checkpointed, or
  none are. A crash mid-superstep resumes the whole superstep — surviving
  tasks fast-forward through their journals (§5), so no completed step re-runs.
* **Recursion limit** (default 25) bounds superstep count; exceeding it raises
  `RecursionLimitError`.

## 5. The journal — durable steps

This is the core of ilmek and the reason it is not a LangGraph clone.

### 5.1 The problem

In a pure-replay engine (LangGraph's model), resuming an interrupted node
re-executes it from the top. Everything before the pause runs **again** — so a
side effect before the pause happens twice. LangGraph documents this as a rule
for the author to obey: *"a side effect belongs after the pause gating it."*
Ilmek does not push that onto the author. The engine remembers.

### 5.2 The contract

> A node body MUST be deterministic **modulo steps**. Every side effect, every
> nondeterministic read (clock, RNG, uuid, network, DB, LLM call) MUST be
> wrapped in a step. Given the same state and the same journal, a node MUST
> request the same steps.

Obey this and replay is invisible. Violate it and the engine's strict mode
(§5.5) tells you where.

### 5.3 Semantics

`step(ctx, key, fun)`:

1. Look `key` up in the task's journal.
2. **Hit** → return the recorded value. `fun` is **NOT called**.
3. **Miss** → call `fun`, append `{key, value}` to the journal, **persist the
   journal**, return the value.

So on the pass after an interrupt, the node re-runs from the top, but every
step it already completed returns instantly from the journal:

```
def run(state, ctx) do
  # 1st pass: calls Orders.create, journals the result.
  # resume pass: returns the journaled order. Orders.create is NOT called.
  order = step ctx, "create_order", fn -> Orders.create(state.cart) end

  # 1st pass: no answer in the journal → the task halts here.
  # resume pass: returns the user's answer from the journal.
  answer = interrupt ctx, %{question: "Charge #{order.total}?"}

  # Only ever reached on the resume pass.
  step ctx, "charge", fn -> Payments.charge(order, answer) end

  {:ok, %{messages: ["done"]}}
end
```

`Orders.create` runs exactly once across both passes. That is what "resume from
the line" means here: not a restored call stack, but **an effect that cannot
happen twice**.

### 5.4 Keys

Keys are **explicit strings**, looked up by name — not by call order. A node may
branch and skip steps; lookup by name stays correct.

Colliding keys within one task are auto-suffixed by occurrence:
`"charge"` called three times journals `charge#0`, `charge#1`, `charge#2`.
This restores order-dependence for that key, so **loops SHOULD carry a stable
key** derived from the data:

```
for item <- state.cart do
  step ctx, "charge:#{item.id}", fn -> Payments.charge(item) end   # stable
end
```

Journaled values MUST survive a serializer round-trip: what a step returns on a
fresh call and what it returns from the journal MUST be equal. A step returning
a PID, socket, or stream handle violates this — return an id and re-resolve it.

### 5.5 Strict mode

When enabled (default in dev/test), the engine records the observed key
sequence and compares it against the journal on the next replay. A journaled
key that the replay never requests, or a divergent order for an auto-suffixed
key, raises `NondeterminismError` naming the key. This turns a silent
double-charge into a loud test failure.

### 5.6 Lifetime

A journal is scoped to a **task** — `(thread, checkpoint, node)` — and is
discarded when that task completes and its update is reduced. It is *replay
memory*, not history. Checkpoints (§7) are the durable record.

## 6. Interrupts & resume (HITL)

**An interrupt is a step whose value comes from a human instead of a function.**
That single idea gives the whole HITL feature set for free.

`interrupt(ctx, payload)`:

1. Look the key up in the journal. **Hit** → return the recorded answer.
2. **Miss** → journal `{key, :pending, payload}`, then **halt the task**.

Halting a task halts its superstep: the engine checkpoints (journals included),
emits an `interrupt` event carrying the payload, and ends the run. The thread
now has a **pending interrupt**. The next run for that thread MUST supply an
answer; the engine writes it into the journal entry and replays the task.

Consequences, all of which fall out of §5 rather than being special-cased:

* **Multiple pauses per node** work. Each `interrupt` has its own key; each
  resolves once; replay fast-forwards through the already-answered ones. No
  index-matching, no "the Nth pause maps to the Nth answer" bookkeeping.
* **Pauses inside loops** work, given stable keys (§5.4).
* **Concurrent interrupts** work: two tasks in one superstep may both halt
  pending; the run emits both and resumes both when both are answered.
* **Effects before a pause do not re-run** — §5.3.
* An interrupt is a **first-class control signal**, never an exception smuggled
  through an error channel. Consumers MUST NOT need to inspect error strings to
  discover a pause.

### 6.1 `id` vs `key` — two scopes, do not conflate them

A journal key is unique **within its task** and nowhere else. Two nodes that
each call a bare `interrupt` in the same superstep therefore *both* journal
`interrupt#0`, because each task counts occurrences on its own.

So a pending interrupt carries two handles:

| Field | Scope | Used for |
|-------|-------|----------|
| `key` | the task (`"interrupt#0"`) | addressing the journal entry |
| `id`  | the thread (`"<node>:<key>"`) | addressing the pause from outside |

**Resume answers MUST be keyed by `id`.** Keying by `key` looks fine until the
first concurrent pause, then silently drops an answer and hands both nodes the
same one — a data-corruption bug with no error.

Two resume forms, and which one applies is decided by the **number of pending
interrupts**, never by the answer's own type:

* **bare answer** — legal only when exactly one interrupt is pending. Because
  the count decides, an object/map answer (`{approved: true}`) can never be
  mistaken for a key map.
* **keyed answers** — a map of `id => answer`. Works for any count, so a UI that
  renders every open pause needs no special case for "exactly one".

Auto-suffixing (§5.4) makes the bare form safe:

```
ok?  = interrupt ctx, %{question: "Delete production?"}    # interrupt#0
sure? = interrupt ctx, %{question: "Really sure?"}         # interrupt#1
```

## 7. Checkpointer — the memory port

One port; many backends. This is where "state'in tutulduğu memory" lives.

```
put(thread_id, checkpoint)          -> :ok
get(thread_id, checkpoint_id | nil) -> checkpoint | nil     # nil = latest
list(thread_id, opts)               -> [checkpoint]          # newest first
put_journal(task_id, entries)       -> :ok
get_journal(task_id)                -> [entry]
delete_thread(thread_id)            -> :ok
```

A **checkpoint** is:

```jsonc
{
  "id": "ckpt-…",              // monotonic, sortable
  "parent_id": "ckpt-…",       // enables branching / time travel
  "thread_id": "thread-…",
  "channels": { "messages": [...], "cart": {...} },
  "next": ["checkout"],        // tasks for the NEXT superstep
  "pending": [                 // open interrupts, empty when running normally
    { "id": "checkout:interrupt#0",   // thread-scoped — answer by THIS (§6.1)
      "task_id": "…", "node": "checkout",
      "key": "interrupt#0",           // task-scoped — addresses the journal entry
      "payload": {...} }
  ],
  "step": 7,                   // superstep counter
  "ts": 1752570000000
}
```

Because every checkpoint names its parent, a thread is a **tree**, not a line:
resuming from a non-latest checkpoint forks a branch (time travel / what-if).
Implementations MUST NOT assume a single chain.

Reference backends: `InMemory` (ETS in Elixir, Map in TS) and `Redis`. A
`Postgres` backend is the intended production default and the natural home for
the DB-driven builder (§9).

Ilmek checkpoints are ilmek's own. They are **not** botiva state — botiva keeps
its transcript and `conv:*` keyspace independently.

## 8. Context

`ctx` is passed explicitly to every node, every step, every conditional. It is
the single handle to everything, per the request "context üstünden tüm grapha
erişim". Explicit passing is normative because it ports to every language;
ambient capture (Elixir process dictionary, TS `AsyncLocalStorage`) MAY be
offered as sugar over the same object but MUST NOT be the only path.

| Field | Meaning |
|-------|---------|
| `ctx.graph` | The compiled graph — nodes, edges, channels. Read-only introspection. |
| `ctx.state` | Channel values as of the last checkpoint (this task's view). Read-only. |
| `ctx.thread_id` | Thread. Botiva maps `conversationId` here. |
| `ctx.run_id` | This run. Changes across an interrupt/resume boundary. |
| `ctx.node` | The currently executing node's name. |
| `ctx.step_index` | Superstep counter. |
| `ctx.journal` | This task's journal. Read-only; for debugging. |
| `ctx.meta` | Free-form map from the caller (botiva puts its `TurnContext` here). |
| `ctx.emit(event)` | Push a custom event into the run's stream (§10). |
| `ctx.log` | Logger scoped with thread/run/node. |

`ctx.state` deliberately exposes the **checkpoint** view, not a live one — §4's
isolation guarantee. Reading it inside a task and reading it in a conditional in
the same superstep MUST agree.

## 9. Graphs as data — the DB-driven builder

A graph is always constructible from a serializable spec. This is normative
from day one, not a later feature, because retrofitting it is expensive.

```jsonc
{
  "name": "support-agent",
  "channels": { "messages": { "reducer": "append" }, "cart": { "reducer": "last_write" } },
  "nodes": [
    { "id": "agent",    "type": "llm",   "config": { "model": "claude-opus-4-8", "tools": ["search"] } },
    { "id": "checkout", "type": "http",  "config": { "url": "…" } }
  ],
  "edges": [
    { "from": "__start__", "to": "agent" },
    { "from": "agent", "to": "checkout", "when": { "channel": "intent", "eq": "buy" } },
    { "from": "checkout", "to": "__end__" }
  ]
}
```

* `type` resolves against a **node registry**: `type -> (config) -> node_fn`.
  Code-defined graphs register anonymous types; DB-defined graphs reference
  registered ones. The engine cannot tell the difference.
* `when` in a stored spec is a **declarative predicate**, not code — a stored
  graph must never carry executable text. Code-defined graphs may pass a real
  function; the spec serializer refuses to emit one.
* Round-trip is a conformance test: `compile(spec) |> to_spec() == spec`.

The drag-and-drop builder is therefore a CRUD app over this document plus a
registry browser. Nothing in the engine knows it exists.

## 10. Events

A run yields a stream. Every event carries a common **envelope** plus its
type-specific fields:

```jsonc
{ "run_id": "…", "thread_id": "…", "seq": 7, "ns": [], "type": "node_end", … }
```

* **`seq`** — monotonic within a run, 1-based, no gaps. A consumer that drops its
  connection reconnects and skips everything up to its last-seen `seq`, so a
  streamed run survives a broken pipe without replaying from the top. (This is
  the primitive LangGraph's v3 stream protocol added; weft/ilmek bakes it into
  the envelope from the start.)
* **`ns`** — the namespace path to the (sub)graph that emitted the event; `[]` at
  the root. Always `[]` today, reserved so subgraphs (a future addition) can tag
  their events with a `["node:task_id", …]` path without a breaking envelope
  change. Consumers that filter by graph MUST key off `ns`, not the run id.

Every implementation MUST emit these types, in this order:

| Event | When |
|-------|------|
| `run_start` | run begins |
| `step_start {step, tasks}` | superstep begins |
| `node_start {node, task_id}` | task begins |
| `custom {payload}` | `ctx.emit(...)` — delivered live, mid-superstep |
| `node_end {node, update}` | task returns |
| `node_error {node, error}` | task raises |
| `state {channels}` | after REDUCE |
| `checkpoint {id}` | after CHECKPOINT |
| `interrupt {pending}` | run halts on a pause |
| `run_end {status}` | `:done` · `:interrupted` · `:error` · `:aborted` |

`interrupt` is a distinct event type. Consumers MUST NOT parse error text or
poll graph state to discover a pause — the defect this design exists to remove.

### 10.1 Projection modes

The single typed stream above is canonical. A consumer MAY **project** it into
the mode-shaped views popularized by LangGraph's `stream_mode`, without the
engine offering a second stream:

| Mode | From | Yields |
|------|------|--------|
| `values` | `state` | full channel state after each superstep |
| `updates` | `node_end` | `{ [node]: update }` per node that ran |
| `custom` | `custom` | each `ctx.emit` payload |
| `messages` | `custom` | just the payloads that are token chunks (§10.2) |
| `debug` | every event | the event itself |

Projection adds no information: each projected part MUST carry the `seq` and `ns`
of the event it came from, so reconnect and subgraph-grouping still work after
projecting. Projecting is a pure function of one event, so it applies equally to
a live stream, a resume stream, or a replay of a reconnect buffer.

### 10.2 Token convention

ilmek is LLM-agnostic — the core never calls a model. But "stream the answer as
it is generated" is universal, so a **token** has a fixed shape:
`{ type: "token", text, meta? }`. A node streams one with `ctx.emitToken(text,
meta?)`, sugar for `ctx.emit(token(...))`. Tokens ride the same transient
channel as `emit` and are therefore **NOT journaled**: on replay a node
re-streams its tokens, and only the values it commits through `step` are
memoized. The `messages` projection mode is exactly "the `custom` payloads that
are tokens".

### 10.3 Cancellation

A run MAY be given an `AbortSignal`. The engine checks it at every **superstep
boundary**: an aborted run stops there and ends with `run_end {status:
:aborted, reason}`. The last committed checkpoint stands — abort stops the
stream, it does not roll back — so the thread resumes cleanly later. The same
signal reaches node code as `ctx.signal`; a node MUST forward it to its own long
awaits (an LLM call, a fetch) for cancellation to interrupt work already in
flight. The engine never force-kills a running task; cancellation is only as
responsive as the node's own signal handling.


## 11. Surface — canonical names per language

| Concept | TypeScript | .NET |
|---------|------------|------|
| define graph (untyped) | `graph(name).channel(…).node(…).edge(…).router(…)` | `Graph.Create(name).Channel(…).Node(…).Edge(…).Router(…)` |
| define graph (typed) | `graph(name, schema).node(…)` — state inferred/nameable | `Graph.Create<TState>(name).Channel(s => s.X, …).Node(…)` |
| compile | `.compile()` | `.Compile()` |
| stream | `stream(g, input, opts): AsyncGenerator<IlmekEvent>` | `g.StreamEvents(input, opts): IAsyncEnumerable<IlmekEvent>` |
| run | `run(g, input, opts): Promise<Result>` | `g.RunAsync(input, opts): Task<Result>` |
| resume (one pause) | `resume(g, answer, opts)` | `g.ResumeAsync(answer, opts)` |
| resume (by id, §6.1) | `resumeKeyed(g, answers, opts)` | `g.ResumeKeyedAsync(answers, opts)` |
| step | `ctx.step(key, fn)` | `ctx.StepAsync(key, fn)` |
| interrupt | `ctx.interrupt(payload?, key?)` | `ctx.InterruptAsync<T>(payload?, key?)` |
| emit | `ctx.emit(payload)` | `ctx.Emit(payload)` |
| emit token (§10.2) | `ctx.emitToken(text, meta?)` | `ctx.EmitToken(text, meta?)` |
| project stream (§10.1) | `streamModes(…)` · `projected(…)` · `project(…)` | `Streaming.StreamModes(…)` · `Streaming.Projected(…)` · `Streaming.Project(…)` |
| dynamic fan-out (§14) | `send(node, input)` from a router or `command` goto | `new Send(node, input)`, same places |
| node-directed routing (§15) | `command({ update?, goto? })` | `Command.Create(update, goto)` · `Command.Goto_(…)` |
| retry (§16) | `.node(id, fn, { retry: {...} })` | `.Node(id, fn, retry: new RetryPolicy {...})` |
| open pauses | `pendingInterrupts(cp, threadId)` | `IlmekRuntime.PendingInterruptsAsync(cp, threadId)` |
| thread state | `threadState(g, cp, threadId)` | `IlmekRuntime.ThreadStateAsync(g, cp, threadId)` |
| checkpointer | `interface Checkpointer` | `interface ICheckpointer` |
| spec round-trip | `fromSpec(spec, registry)` · `toSpec(g)` | `Spec.FromSpec(spec, registry)` · `Spec.ToSpec(g)` |
| node return | `update` \| `void` \| `command(...)` \| `throw` | update dict \| `null` \| `Command` \| `throw` |
| entry / exit | `START` · `END` | `Graph.Start` · `Graph.End` |
| cancellation (§10.3) | `AbortSignal` → `ctx.signal` | `CancellationToken` → `ctx.CancellationToken` |

Where the two must differ, and why:

* **State typing.** Channels are inherently string-keyed (§2), so an untyped map
  is always available (`state["key"]` / `state.Get<T>("key")`). Both languages
  also offer a fully typed surface over it: TypeScript infers the state from the
  declared channels (`graph(name)` chained, or `graph(name, schema)` for a
  nameable type); .NET declares the state as a class and names channels by
  selector (`Graph.Create<TState>()` + `.Channel(s => s.Cart, …)`), returning
  `Update.For<TState>().Set(s => s.X, v)`. Same channel map underneath — the
  typing is a facade the engine never sees.
* **The pause signal.** TS throws a non-`Error` value so a
  `catch (e instanceof Error)` cannot swallow a pause. The CLR has no such
  option: `InterruptSignalException` derives from `Exception`, so a node with a
  blanket `catch (Exception)` **will** swallow it and must rethrow when
  `InterruptSignalException.IsInterrupt(ex)`.
* **Entry-point naming.** A .NET static class named `Ilmek` inside namespace
  `Ilmek` binds ambiguously (the namespace wins), so the port exposes
  `IlmekRuntime` plus extension methods on the compiled graph.

Run options (`RunOptions`): `threadId`, `checkpointer`, `checkpointId`,
`recursionLimit` (default 25), `strict` (default true), `meta`, `log`, `signal`
(§10.3). Context fields beyond the graph handles: `stepIndex`, `recursionLimit`,
`remainingSteps` (`recursionLimit − stepIndex`, floored at 0, for graceful
wind-down), `signal`, `meta`, `journal` (read-only).

Node bodies are `(state, ctx)`. A `threadId` is required for any run that uses a
checkpointer.

Explicit `ctx` passing is normative. Ambient capture (`AsyncLocalStorage`, or a
process dictionary in another port) MAY be offered as sugar over the *same*
object, never as a second source of truth.

## 12. Conformance

An implementation is conformant when it passes the scenario list below —
executable as the `test/` suite in the reference, and the arbiter of §2–§16.
A future port is conformant when it reproduces the same list.

Non-negotiable scenarios:

1. `step` executes once across an interrupt/resume cycle.
2. Two interrupts in one node resolve independently, in one resume each.
3. An interrupt inside a loop with stable keys resumes at the right iteration.
4. Superstep reduce order is task order, not completion order.
5. A crash mid-superstep replays the superstep with zero re-executed steps.
6. `compile(spec) |> to_spec()` round-trips.
7. Strict mode raises on a step key that vanishes on replay.
8. Concurrent pauses in one superstep resolve to **their own** answers, and a
   bare answer to more than one pending interrupt is refused (§6.1).
9. A `send` fan-out runs the target once per payload, each with its own input
   state, and the results reduce into a channel independent of scheduling (§14).
10. A node's `command({goto})` overrides its static edges; `command({update})`
    reduces before the goto is planned (§15).
11. A node that fails and retries re-runs its body but **not** its completed
    steps — retries are safe because the journal already paid for them (§16).

Scenario 8 exists because the engine once shipped this bug: two nodes pausing in
the same superstep each journal `interrupt#0`, so answers keyed by `key`
collapsed to one entry and both nodes received the same answer. Nothing raised. A
conformance list is worth exactly the failures it has caught.

## 13. Versioning

`ilmek/<major>`. Breaking changes to checkpoint layout, journal semantics, or the
event catalog bump the major. Additive channels/events/fields do not — consumers
MUST ignore unknown fields.

## 14. Dynamic fan-out — `send`

Static edges and routers (§3) pick *which nodes* run next; every task then reads
the same checkpoint state (§4). `send` adds the missing axis: run **one node many
times in parallel, each with its own input**. It is the map-reduce primitive.

A router MAY return `send(node, input)` values, alone or mixed with plain node
names:

```ts
.router("fanout", (state) => state.items.map((item) => send("worker", { item }))
```

Semantics:

* Each `send(node, input)` schedules one task for `node` in the next superstep.
  N sends to the same node are N distinct parallel tasks.
* That task's `ctx.state` **is the send `input`**, not the channel state — the
  worker sees exactly what the mapper handed it. The task still writes updates
  that reduce into the graph's channels as usual, so workers fan results back in
  through an `append` channel.
* `input` MUST be JSON-serializable: it is written into the checkpoint's `next`
  so a resumed run re-dispatches the same fan-out.
* Task identity disambiguates sends: the Nth send to a node in a superstep has a
  distinct task id (and therefore its own journal), so a pause or a step inside
  one fan-out branch never collides with another.
* Fan-in is just a normal superstep boundary: a downstream node with an edge
  from `worker` runs once, after all worker tasks reduce, seeing the collected
  channel.

When the fan-out set is only known *after* a node's work, return the sends in a
`command` goto (§15): `command({ goto: items.map((i) => send("worker", i)) })`.

## 15. Node-directed routing — `command`

Routing in §3 is decided at plan time from state, before the node runs. A node
that only *discovers* where to go next — an agent choosing its next tool —
returns a **command** instead:

```ts
.node("agent", (state, ctx) =>
    command({ update: { messages: [reply] }, goto: reply.done ? END : "tools" }))
```

Semantics:

* `update` is reduced exactly like a normal node return (same channels, same
  task-order rules).
* `goto` (a node name, a list, `END`, or `send(...)` values) **replaces** the
  node's static outgoing edges for this superstep. A node with no static edges
  is legal when it always returns a `goto`.
* A bare update and `command({update})` are equivalent; `command` exists only to
  carry `goto` alongside.
* `goto` is planned *after* `update` reduces, so the routing decision sees the
  state the node just wrote.
* A `command` whose `goto` is omitted falls back to static edges — so you can add
  a `goto` to one branch of a node without wiring every path through `command`.

`command` keeps the purity rule intact: routers/guards (§3) still must not have
side effects, because they still run at plan time. `command` is how a node that
*has* run its (journaled) side effects then directs the flow.

## 16. Retry & resilience

A node MAY declare a retry policy:

```ts
.node("call_api", fn, { retry: { maxAttempts: 3, backoffMs: 200, factor: 2, retryOn: isTransient } })
```

When the node throws a non-interrupt error, the engine re-invokes it up to
`maxAttempts` times (`backoffMs * factor^(n-1)` between attempts, optionally
gated by `retryOn(error)`). Each retry emits a `node_retry {node, attempt,
error}` event before the next attempt.

The retry re-runs the **node body**, but every `ctx.step` it already completed
returns from the journal (§5) instead of re-executing. So a node that charged a
card in one step and then hit a flaky API in the next retries the API call
**without charging twice**. This is the same guarantee interrupts rely on, turned
toward failure instead of a human — and it is why retries in ilmek are safe by
default where a pure-replay engine's are not.

Retries are within a single superstep; they do not create checkpoints. If all
attempts are exhausted the node fails normally (§4) and the run ends `:error`.
An `AbortSignal` (§10.3) fires between attempts stops the retry loop.
