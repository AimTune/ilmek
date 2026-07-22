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
| define graph (typed) | `graph(name, schema).node(…)` | `Graph.Create<TState>(name).Channel(s => s.X, …).Node(…)` |
| compile | `.compile()` | `.Compile()` |
| stream | `stream(g, input, opts): AsyncGenerator<IlmekEvent>` | `g.StreamEvents(input, opts): IAsyncEnumerable<IlmekEvent>` |
| run | `run(g, input, opts): Promise<Result>` | `g.RunAsync(input, opts): Task<Result>` |
| resume (one pause) | `resume(g, answer, opts)` | `g.ResumeAsync(answer, opts)` |
| resume (by id) | `resumeKeyed(g, answers, opts)` | `g.ResumeKeyedAsync(answers, opts)` |

Two reserved node names — `START` (`__start__`) and `END` (`__end__`) — are
implicit in every language.
