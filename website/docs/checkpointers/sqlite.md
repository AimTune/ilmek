---
id: sqlite
title: SQLite checkpointer
sidebar_label: SQLite
sidebar_position: 1
---

# `@ilmek/checkpoint-sqlite`

A durable [checkpointer](/checkpointers/overview) in a single SQLite file — zero
dependencies, on Node's built-in `node:sqlite`. A parked
[interrupt](/model/interrupts) survives a process restart, a deploy, or a crash.

## Install

```bash
npm install @ilmek/core @ilmek/checkpoint-sqlite
```

Requires **Node ≥ 22.5** (for the built-in `node:sqlite` module).

## Usage

```ts
import { run } from "@ilmek/core";
import { SqliteCheckpointer } from "@ilmek/checkpoint-sqlite";

const cp = await SqliteCheckpointer.open("./agent.db");   // creates + migrates
await run(graph, input, { threadId, checkpointer: cp });
```

`SqliteCheckpointer.open()` creates the file if absent and runs its migrations, so
there is no separate setup step.

## Bring your own driver

It talks to a **duck-typed** database (`exec` / `prepare`), so
[`better-sqlite3`](https://github.com/WiseLibs/better-sqlite3) drops in unchanged
if you would rather use it than the built-in module.

## When to reach for Postgres instead

SQLite gives you single-process durability. For threads that must be read and
resumed from **more than one process** — a web tier plus a worker tier, say — use
[`@ilmek/checkpoint-postgres`](/checkpointers/postgres) instead.
