# Conformance

An ilmek implementation is conformant when it reproduces the scenarios below —
the arbiter of [MODEL.md](../MODEL.md) §2–§16. Each is a behavior a port must
exhibit, phrased language-neutrally.

Both implementations encode this list as their test suite:

| | suite | run |
|---|---|---|
| TypeScript | [ts/packages/core/test](../ts/packages/core/test) | `cd ts && pnpm test` |
| .NET | [dotnet/test/Ilmek.Core.Tests](../dotnet/test/Ilmek.Core.Tests) | `cd dotnet && dotnet test Ilmek.sln` |

These are the non-negotiables from MODEL.md §12. A port is "done enough to trust"
when all pass.

| # | Scenario | Spec |
|---|----------|------|
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

## Companion packages

The skills reader (`@ilmek/skills` / `Ilmek.Skills`) has its own shared
fixture: the folders under [skills/](skills/) are parsed by both languages and
compared with `skills/expected.json` as canonical JSON. See
[skills/README.md](skills/README.md). The MCP toolbox (`@ilmek/mcp` /
`Ilmek.Mcp`) likewise reduces the scripted server in [mcp/](mcp/) to
`mcp/expected.json`; see [mcp/README.md](mcp/README.md). The A2A client
(`@ilmek/a2a` / `Ilmek.A2A`) reduces the scripted agent in [a2a/](a2a/) to
`a2a/expected.json`; see [a2a/README.md](a2a/README.md).

Checkpointers share a **contract suite** — the §7 claims (latest-by-id, history
order, journals, `deleteThread` dropping the thread's journals and nothing else,
…) as tests every backend runs:
[ts/packages/checkpointers/sqlite/test/contract.ts](../ts/packages/checkpointers/sqlite/test/contract.ts)
(the Postgres package carries the same file, run against PGlite) and
[dotnet/test/Ilmek.Core.Tests/CheckpointerContract.cs](../dotnet/test/Ilmek.Core.Tests/CheckpointerContract.cs).
A new backend starts by passing it.

## Why a list and not a shared harness (yet)

Each language runs its own harness against its own idioms — Node's `node:test`,
xUnit for .NET. The scenarios are the shared truth. A machine-readable
`scenarios.json` both harnesses drive from is a plausible refinement, but the
scenarios exercise *code shapes* (a node that branches, a step that throws twice)
more than data, so most of each test would stay hand-written anyway; the payoff
is smaller than it looks.

Scenario 8 is here because the reference engine once shipped its inverse as a
bug — two nodes pausing in one superstep both journaled `interrupt#0`, so answers
keyed by `key` collapsed and both got the same one, silently. A conformance list
earns its place by the failures it has caught; port authors should assume the
subtle ones (3, 5, 8, 9) are where a fresh implementation goes wrong.
