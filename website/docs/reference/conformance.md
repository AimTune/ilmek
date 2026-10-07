---
id: conformance
title: Conformance
sidebar_label: Conformance
sidebar_position: 2
---

# Conformance

*Normative: [MODEL.md §12](/reference/spec).*

An implementation is **conformant** when it reproduces the scenarios below — the
arbiter of MODEL.md §2–§16. Both implementations encode this list as their test
suite:

| | suite | run |
|---|---|---|
| TypeScript | `ts/packages/core/test` | `cd ts && pnpm test` |
| .NET | `dotnet/test/Ilmek.Core.Tests` | `cd dotnet && dotnet test Ilmek.sln` |

## The non-negotiable scenarios

| # | Scenario | Spec |
|---|---|---|
| 1 | `step` executes **once** across an interrupt/resume cycle | §5, §6 |
| 2 | Two interrupts in one node resolve independently, one resume each | §6 |
| 3 | An interrupt inside a loop with stable keys resumes at the right iteration | §5.4, §6 |
| 4 | Superstep reduce order is **task order**, not completion order | §2, §4 |
| 5 | A crash mid-superstep replays it with **zero** re-executed steps | §4, §5 |
| 6 | `compile(spec)` → `toSpec()` round-trips | §9 |
| 7 | Strict mode raises when a journaled step key vanishes on replay | §5.5 |
| 8 | Concurrent pauses resolve to **their own** answers; a bare answer to >1 pending is refused | §6.1 |
| 9 | A `send` fan-out runs the target once per payload, each with its own input; results reduce independent of scheduling | §14 |
| 10 | A node's `command({goto})` overrides its static edges; `command({update})` reduces before the goto is planned | §15 |
| 11 | A retried node re-runs its body but **not** its completed steps | §16 |

## Why scenario 8 exists

The engine once shipped this bug: two nodes pausing in the same superstep each
journal `interrupt#0`, so answers keyed by `key` collapsed to one entry and both
nodes received the same answer. **Nothing raised.** A conformance list is worth
exactly the failures it has caught — this is the one that earned the
[`id` vs `key` rule](/model/interrupts#id-vs-key).

## Shared fixtures and contracts

Beyond the scenario list, the two implementations are held to one result by
shared data:

| what | fixture | both suites compare against |
|---|---|---|
| Skills reader | the folders in [`conformance/skills`](https://github.com/AimTune/ilmek/tree/main/conformance/skills) | `skills/expected.json` |
| MCP toolbox | the scripted server in [`conformance/mcp`](https://github.com/AimTune/ilmek/tree/main/conformance/mcp) | `mcp/expected.json` |
| A2A client | the scripted agent in [`conformance/a2a`](https://github.com/AimTune/ilmek/tree/main/conformance/a2a) | `a2a/expected.json` |

Each `expected.json` is generated once by the TypeScript reference, reviewed and
committed; the .NET suite reruns it unchanged.

The checkpointers also pass a shared **checkpointer contract** — the MODEL.md
§7 claims as a test suite (`ts/packages/checkpointers/*/test/contract.ts`,
`dotnet/test/Ilmek.Core.Tests/CheckpointerContract.cs`): SQLite in both
languages (in memory and on a file), Postgres in TypeScript through PGlite
(in-process Postgres), and the in-memory checkpointer in .NET.

## Status

Green against the list in both languages: TypeScript and .NET. See the
[capability matrix](https://github.com/AimTune/ilmek#status) in the repository for
the current per-feature breakdown across the two ports.
