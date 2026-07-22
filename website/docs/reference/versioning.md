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

The npm packages version together, independently of the spec major:

| Package | Registry |
|---|---|
| `@ilmek/core` | [npmjs.com/package/@ilmek/core](https://www.npmjs.com/package/@ilmek/core) |
| `@ilmek/checkpoint-sqlite` | [npmjs.com/package/@ilmek/checkpoint-sqlite](https://www.npmjs.com/package/@ilmek/checkpoint-sqlite) |
| `@ilmek/checkpoint-postgres` | [npmjs.com/package/@ilmek/checkpoint-postgres](https://www.npmjs.com/package/@ilmek/checkpoint-postgres) |

The .NET packages (`Ilmek.Core`, `Ilmek.Checkpointer.Sqlite`) ship to NuGet on the
same release tag. A single `v*` git tag drives both the npm and NuGet releases, so
a version number means the same thing on both registries.
