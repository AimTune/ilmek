---
id: versioning
title: Versioning
sidebar_label: Versioning
sidebar_position: 3
---

# Versioning

*Normative: [MODEL.md §13](/reference/spec).*

The spec is versioned `ilmek/<major>`.

- **Breaking changes** to checkpoint layout, journal semantics, or the event
  catalog bump the major.
- **Additive** channels, events, or fields do **not** bump the major — and
  consumers **must ignore unknown fields**. Writing a consumer that tolerates
  fields it does not recognize is what lets the envelope grow (subgraph `ns`
  paths, new event types) without a breaking release.

## Package versions

Every package versions together, independently of the spec major — the current
release is **0.2.0**:

| npm | NuGet |
|---|---|
| [`@ilmek/core`](https://www.npmjs.com/package/@ilmek/core) | [`Ilmek.Core`](https://www.nuget.org/packages/Ilmek.Core) |
| [`@ilmek/checkpoint-sqlite`](https://www.npmjs.com/package/@ilmek/checkpoint-sqlite) | [`Ilmek.Checkpointer.Sqlite`](https://www.nuget.org/packages/Ilmek.Checkpointer.Sqlite) |
| [`@ilmek/checkpoint-postgres`](https://www.npmjs.com/package/@ilmek/checkpoint-postgres) | — |
| [`@ilmek/skills`](https://www.npmjs.com/package/@ilmek/skills) | [`Ilmek.Skills`](https://www.nuget.org/packages/Ilmek.Skills) |
| [`@ilmek/mcp`](https://www.npmjs.com/package/@ilmek/mcp) | [`Ilmek.Mcp`](https://www.nuget.org/packages/Ilmek.Mcp) |
| [`@ilmek/a2a`](https://www.npmjs.com/package/@ilmek/a2a) | [`Ilmek.A2A`](https://www.nuget.org/packages/Ilmek.A2A) |

A single `v*` git tag drives both the npm and the NuGet release, so a version
number means the same thing on both registries. While the packages are `0.x`, a
minor bump may carry breaking API changes; the spec major (`ilmek/1`) only moves
for the reasons above.
