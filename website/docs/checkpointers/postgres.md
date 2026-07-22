---
id: postgres
title: Postgres checkpointer
sidebar_label: Postgres
sidebar_position: 2
---

# `@ilmek/checkpoint-postgres`

A durable [checkpointer](/checkpointers/overview) backed by Postgres — for threads
shared across processes. No hard `pg` dependency: it talks to any
node-postgres-style client (`query(text, params)`), so a `Client` or `Pool` drops
in as-is.

## Install

```bash
npm install @ilmek/core @ilmek/checkpoint-postgres pg
```

`pg` is not a hard dependency — any client exposing `query(text, params)` works —
but it is the usual choice.

## Usage

```ts
import { Pool } from "pg";
import { run } from "@ilmek/core";
import { PostgresCheckpointer } from "@ilmek/checkpoint-postgres";

const pool = new Pool({ connectionString: process.env.DATABASE_URL });

const cp = new PostgresCheckpointer(pool);
await cp.migrate();
await run(graph, input, { threadId, checkpointer: cp });
```

`migrate()` creates the two tables it needs (`ilmek_checkpoints`, `ilmek_journals`)
if absent. It is idempotent — safe to call on every boot — or you can run the DDL
yourself and skip it.

## Sharing one database

Pass `tablePrefix` so several apps can share a database without colliding:

```ts
new PostgresCheckpointer(pool, { tablePrefix: "billing" });
// → billing_checkpoints, billing_journals
```

## Bring your own client

The checkpointer never opens or closes a connection — you own the `Pool`'s
lifecycle. Any object with `query(text, params) => { rows }` satisfies its
`SqlClient` interface, so a custom pool or a proxied client works as long as it
speaks that shape.
