# ilmek — .NET

The .NET port. [../MODEL.md](../MODEL.md) is the spec; this is the developer
guide.

It passes the same [conformance](../conformance/) list as the TypeScript
reference — all 11 non-negotiable scenarios, plus the shared skills, MCP and A2A
fixtures — and its two demos print the same output as their TS counterparts. That sameness is what
having a spec is for.

## Layout

```
dotnet/
  Ilmek.sln
  src/
    Ilmek.Core/                  the engine. no third-party dependencies.
    Ilmek.Checkpointer.Sqlite/   durable threads in a file (Microsoft.Data.Sqlite)
    Ilmek.Skills/                SKILL.md folders, progressive disclosure (no dependencies)
    Ilmek.Mcp/                   MCP servers as journaled tools (IMcpClient interface, no dependencies)
    Ilmek.A2A/                   call A2A agents, journaled (IA2aTransport + HttpClient, no dependencies)
  test/
    Ilmek.Core.Tests/            the MODEL.md §12 conformance suite (xUnit)
    Ilmek.Checkpointer.Sqlite.Tests/
    Ilmek.Skills.Tests/          shares conformance/skills with the TypeScript suite
    Ilmek.Mcp.Tests/             shares conformance/mcp with the TypeScript suite
    Ilmek.A2A.Tests/             shares conformance/a2a with the TypeScript suite
  examples/
    Ilmek.Examples/              runnable demos
```

Each provider is its own project, so its driver never becomes a dependency of the
engine. Postgres (Npgsql) is the next sibling.

## Commands

```bash
dotnet build Ilmek.sln
dotnet test Ilmek.sln                                     # every suite: core, sqlite, skills, mcp, a2a
dotnet run --project examples/Ilmek.Examples              # checkout: durable HITL
dotnet run --project examples/Ilmek.Examples -- mapreduce # send + retry + command
```

Coverage: every test project references `coverlet.collector`, so

```bash
dotnet test Ilmek.sln --collect:"XPlat Code Coverage" --results-directory ./coverage
```

writes one `coverage.cobertura.xml` per test project (line and branch rates per
assembly, readable by any Cobertura viewer or ReportGenerator).

The checkpointer contract (`test/Ilmek.Core.Tests/CheckpointerContract.cs`) is
compiled into both the core and the SQLite test projects, so every backend passes
the same list of MODEL.md §7 claims — a new backend's tests start by subclassing it.

## Durable checkpointing

```csharp
using Ilmek;
using Ilmek.Checkpointers.Sqlite;

using var cp = SqliteCheckpointer.Open("./agent.db");   // creates + migrates
var paused = await graph.RunAsync(input, new RunOptions { ThreadId = "t1", Checkpointer = cp });
// …process exits, deploys, comes back…
using var cp2 = SqliteCheckpointer.Open("./agent.db");
var done = await graph.ResumeAsync("yes", new RunOptions { ThreadId = "t1", Checkpointer = cp2 });
```

The tests prove the claim that matters: a pause written by one connection is
answered by another after the file is closed and reopened, **and the effect
before that pause does not re-run**.

One .NET-specific constraint, enforced by the decoder and worth knowing: values
cross the file boundary as JSON, so they come back as plain CLR data —
`Dictionary<string, object?>`, `List<object?>`, `string`, `long`/`double`, `bool`,
`null`. A custom type journaled into a step or written to a channel returns as its
JSON shape. Journal what serializes (ids, strings, numbers) and re-resolve richer
objects from it — the same rule MODEL.md §5.4 already states. `StepAsync<T>` casts
the recorded value to `T`, so a step that returns a record or a tuple replays
from `InMemoryCheckpointer` but fails with `InvalidCastException` once its
journal has been read back from SQLite.

Targets `net9.0`. No SDK on the machine? `curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0`
installs to `~/.dotnet` without admin rights.

## Typed graphs

Channels are string-keyed underneath (MODEL.md §2), but you rarely touch that.
Declare the state as a class — **the class is the schema**. Each property is a
channel; its reducer lives on the property (a plain property is `last_write`,
`[Append]` accumulates, `[Merge]` merges), so the graph derives every channel
from the class with no restatement:

```csharp
using Ilmek;

public sealed class CheckoutState
{
    public List<string> Cart { get; set; } = [];       // last_write (default)
    [Append] public List<string> Log { get; set; } = [];
    public string Intent { get; set; } = "";
}

var g = Graph.Create<CheckoutState>("checkout")
    .Node("checkout", (state, ctx) =>          // state.Cart is List<string> — a typo is a compile error
        Update.For<CheckoutState>()
            .Set(s => s.Intent, "buy")         // last-write: the value
            .Append(s => s.Log, "classified")  // append: element-typed item
    )
    .Edge(Graph.Start, "checkout")
    .Edge("checkout", Graph.End)
    .Compile();

Result<CheckoutState> r = await g.RunAsync(input, opts);
List<string> log = r.State!.Log;   // typed all the way out
```

It compiles to the same channel map as the untyped builder, so reducers,
journals, sends, retries — everything in MODEL.md — work identically. `Result<T>`
materializes the class for you, coercing JSON-round-tripped values (a durable
checkpointer hands back `List<object?>`) back to the property's type.

A **custom** reducer (a lambda) can't be an attribute, so it stays an explicit
override — the one remaining reason to call `.Channel()`:

```csharp
.Channel(s => s.Total, Channels.Reduce((cur, inc) => (long)(cur ?? 0L) + (long)inc, 0L))
```

## Untyped surface

When the shape is dynamic, skip the class: `state.Get<T>("key")` /
`state.GetList<T>("key")`, returning an update dictionary, `null`, or a
`Command`.

```csharp
var g = Graph.Create("checkout")
    .Channel("cart", Channels.LastWrite(new List<object?>()))
    .Channel("log", Channels.Append())
    .Node("checkout", async (state, ctx) =>
    {
        // Called once, ever. On the resume pass this returns the journaled order.
        var order = await ctx.StepAsync("create_order", () => CreateOrder(state.GetList<string>("cart")));

        // First pass: the task halts here. Resume pass: returns the human's answer.
        var ok = await ctx.InterruptAsync<string>(new { question = $"Charge {order.Total}?" });

        await ctx.StepAsync("charge", () => Charge(order));
        return Update.Of("log", "done");
    })
    .Edge(Graph.Start, "checkout")
    .Edge("checkout", Graph.End)
    .Compile();

var opts = new RunOptions { ThreadId = "conv-42", Checkpointer = new InMemoryCheckpointer() };
var paused = await g.RunAsync(input, opts);      // Status: Interrupted
var done   = await g.ResumeAsync("yes", opts);   // Status: Done
```

| Concept | TypeScript | .NET |
|---------|------------|------|
| define graph (untyped) | `graph(name).channel(…).node(…)` | `Graph.Create(name).Channel(…).Node(…)` |
| define graph (typed) | `graph(name, schema).node(…)` | `Graph.Create<TState>(name).Node(…)` — reducers from `[Append]` / `[Merge]` |
| typed update | the update object itself | `Update.For<TState>().Set(…).Append(…)` · untyped `Update.Of(key, value)` |
| compile | `.compile()` | `.Compile()` |
| run | `run(g, input, opts)` | `g.RunAsync(input, opts)` |
| stream | `stream(g, input, opts)` | `g.StreamEvents(input, opts)` |
| resume (one pause) | `resume(g, answer, opts)` | `g.ResumeAsync(answer, opts)` |
| resume (by id, §6.1) | `resumeKeyed(g, answers, opts)` | `g.ResumeKeyedAsync(answers, opts)` |
| step | `ctx.step(key, fn)` | `ctx.StepAsync(key, fn)` |
| interrupt | `ctx.interrupt(payload?, key?)` | `ctx.InterruptAsync<T>(payload?, key?)` |
| emit / token | `ctx.emit` · `ctx.emitToken` | `ctx.Emit` · `ctx.EmitToken` |
| fan-out (§14) | `send(node, input)` | `new Send(node, input)` |
| node routing (§15) | `command({update, goto})` | `Command.Create(update, goto)` |
| retry (§16) | `{ retry: {...} }` | `retry: new RetryPolicy { … }` |
| projection (§10.1) | `streamModes` · `projected` · `project` | `Streaming.StreamModes` · `Streaming.Projected` · `Streaming.Project` |
| open pauses | `pendingInterrupts(cp, threadId)` | `IlmekRuntime.PendingInterruptsAsync(cp, threadId)` |
| thread state | `threadState(g, cp, threadId)` | `IlmekRuntime.ThreadStateAsync(g, cp, threadId)` |
| spec round-trip (§9) | `fromSpec(spec, registry)` · `toSpec(g)` | `Spec.FromSpec(spec, registry)` · `Spec.ToSpec(g)` |
| checkpointer | `interface Checkpointer` · `InMemoryCheckpointer` | `interface ICheckpointer` · `InMemoryCheckpointer` |
| a thread's journal prefixes | `threadJournalPrefixes(threadId)` | `Checkpoint.ThreadJournalPrefixes(threadId)` |
| checkpoint id | `generateCheckpointId()` | `Checkpoint.GenerateId()` |
| cancellation (§10.3) | `signal` → `ctx.signal` | `CancellationToken` → `ctx.CancellationToken` |
| is it a pause? | `isInterrupt(e)` | `InterruptSignalException.IsInterrupt(ex)` |
| errors | `GraphError` · `ReducerError` · `RecursionLimitError` · `NondeterminismError` · `ResumeError` | `GraphException` · `ReducerException` · `RecursionLimitException` · `NondeterminismException` · `ResumeException` |
| entry / exit | `START` · `END` | `Graph.Start` · `Graph.End` |

`taskIdFor(threadId, planId, taskKey)`, the `log` run option (a `Logger`) and the
`isSend` / `isCommand` / `isToken` guards are TypeScript-only.

Two naming notes, both deliberate:

- The entry points live on **`IlmekRuntime`**, not `Ilmek` — a static class that
  shares its namespace's name binds ambiguously at call sites, because C#
  resolves the namespace first. `GraphExtensions` re-exposes them as extension
  methods, which is the form to use: `g.RunAsync(...)`. For a typed graph
  (`CompiledGraph<TState>`) the extension methods are the only form, and they
  return a `Result<TState>`; `IlmekRuntime` takes the untyped `CompiledGraph`,
  which a typed graph exposes as `g.Inner` (e.g.
  `IlmekRuntime.ThreadStateAsync(g.Inner, cp, threadId)`).
- **`InterruptSignalException`** is an exception because the CLR has no other way
  to unwind. It is still ordinary control flow, not a failure — but it means a
  node with a blanket `catch (Exception)` *will* swallow a pause. Rethrow when
  `InterruptSignalException.IsInterrupt(ex)` says so.

## Debugging HITL pauses

`ILMEK_DEBUG_BREAK_ON_INTERRUPT=1` arms a `Debugger.Break()` in
`Context.InterruptAsync` right where the pause is journaled, with the journal key
and the outgoing question in scope. It is inert without both the env var and an
attached debugger, so it costs nothing in production.

## Not yet ported

The engine, journal, interrupts, streaming envelope, sends, commands, retries,
graphs-as-data and the SQLite checkpointer are all here. Still TypeScript-only:

- `Ilmek.Checkpointer.Postgres` — for threads shared across processes
