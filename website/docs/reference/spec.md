---
id: spec
title: The spec (MODEL.md)
sidebar_label: Spec (MODEL.md)
sidebar_position: 1
---

# The spec

[**MODEL.md**](https://github.com/AimTune/ilmek/blob/main/MODEL.md) is the
normative, language-neutral specification — `ilmek/1`. Every implementation
reproduces it exactly; these docs are the tour, MODEL.md is the law. Where the two
disagree, MODEL.md wins.

## Section map

| § | Topic | Docs page |
|---|---|---|
| 1 | Vocabulary | — |
| 2 | State & channels | [State & channels](/model/state-and-channels) |
| 3 | Graph | [Graph](/model/graph) |
| 4 | Execution — supersteps (BSP) | [Supersteps](/model/supersteps) |
| 5 | The journal — durable steps | [The journal](/model/journal) |
| 6 | Interrupts & resume (HITL) | [Interrupts & resume](/model/interrupts) |
| 7 | Checkpointer — the memory port | [Checkpointers](/checkpointers/overview) |
| 8 | Context | [The journal](/model/journal) |
| 9 | Graphs as data | [Graphs as data](/graphs-as-data) |
| 10 | Events | [Streaming](/streaming/overview) |
| 11 | Surface — canonical names per language | [below](#surface) |
| 12 | Conformance | [Conformance](/reference/conformance) |
| 13 | Versioning | [Versioning](/reference/versioning) |
| 14 | Dynamic fan-out — `send` | [send](/control-flow/send) |
| 15 | Node-directed routing — `command` | [command](/control-flow/command) |
| 16 | Retry & resilience | [retry](/control-flow/retry) |

## Surface — names per language {#surface}

The same concepts, spelled to each language's idiom (MODEL.md §11):

| Concept | TypeScript | .NET |
|---|---|---|
| define graph (untyped) | `graph(name).channel(…).node(…).edge(…).router(…)` | `Graph.Create(name).Channel(…).Node(…).Edge(…).Router(…)` |
| define graph (typed) | `graph(name, schema).node(…)` | `Graph.Create<TState>(name).Node(…)` — reducers from `[Append]` / `[Merge]` |
| compile | `.compile()` | `.Compile()` |
| stream | `stream(g, input, opts): AsyncGenerator<IlmekEvent>` | `g.StreamEvents(input, opts): IAsyncEnumerable<IlmekEvent>` |
| run | `run(g, input, opts): Promise<Result>` | `g.RunAsync(input, opts): Task<Result>` |
| resume (one pause) | `resume(g, answer, opts)` | `g.ResumeAsync(answer, opts)` |
| resume (by id) | `resumeKeyed(g, answers, opts)` | `g.ResumeKeyedAsync(answers, opts)` |
| step | `ctx.step(key, fn)` | `ctx.StepAsync(key, fn)` |
| interrupt | `ctx.interrupt(payload?, key?)` | `ctx.InterruptAsync<T>(payload?, key?)` |
| emit / token | `ctx.emit(payload)` · `ctx.emitToken(text, meta?)` | `ctx.Emit(payload)` · `ctx.EmitToken(text, meta?)` |
| project stream | `streamModes` · `projected` · `project` | `Streaming.StreamModes` · `Streaming.Projected` · `Streaming.Project` |
| fan-out / routing | `send(node, input)` · `command({ update?, goto? })` | `new Send(node, input)` · `Command.Create(update, goto)` · `Command.Goto_(…)` |
| retry | `.node(id, fn, { retry: {...} })` | `.Node(id, fn, retry: new RetryPolicy {...})` |
| open pauses / thread state | `pendingInterrupts(cp, threadId)` · `threadState(g, cp, threadId)` | `IlmekRuntime.PendingInterruptsAsync(cp, threadId)` · `IlmekRuntime.ThreadStateAsync(g, cp, threadId)` |
| checkpointer | `interface Checkpointer` | `interface ICheckpointer` |
| journaled-value JSON | `JSON` (plain data) | `JournalJson.Options` · `JournalJson.ToPlain` · `JournalJson.ConvertTo<T>` |
| spec round-trip | `fromSpec(spec, registry)` · `toSpec(g)` | `Spec.FromSpec(spec, registry)` · `Spec.ToSpec(g)` |
| cancellation | `AbortSignal` → `ctx.signal` | `CancellationToken` → `ctx.CancellationToken` |
| errors | `GraphError`, `ResumeError`, `NondeterminismError`, … | `GraphException`, `ResumeException`, `NondeterminismException`, … |

MODEL.md §11 has the full table and the three places the languages must differ
(state typing, the pause signal, entry-point naming).

Two reserved node names — `START` (`__start__`) and `END` (`__end__`) — are
implicit in every language.
