# @ilmek/checkpoint-postgres

A durable [ilmek](https://github.com/AimTune/ilmek) checkpointer backed by
Postgres — for threads shared across processes. No hard `pg` dependency: it talks
to any node-postgres-style client (`query(text, params)`), so a `Client` or
`Pool` drops in as-is.

```sh
npm install @ilmek/core @ilmek/checkpoint-postgres pg
```

```ts
import { Pool } from "pg";
import { run } from "@ilmek/core";
import { PostgresCheckpointer } from "@ilmek/checkpoint-postgres";

const pool = new Pool({ connectionString: process.env.DATABASE_URL });

const cp = new PostgresCheckpointer(pool);
await cp.migrate();
await run(graph, input, { threadId, checkpointer: cp });
```

Pass `tablePrefix` so several apps can share one database. It becomes part of a
table name, so it must be a plain SQL identifier (letters, digits, `_`; not
starting with a digit) — anything else is refused at construction:

```ts
new PostgresCheckpointer(pool, { tablePrefix: "billing" });   // billing_checkpoints, billing_journals
```

State and journals are stored as `jsonb`, which cannot hold U+0000 — a value
containing a NUL character fails to write. [PGlite](https://pglite.dev) also
satisfies the client shape, which makes in-process tests easy.

For single-process durability with no external database, use
`@ilmek/checkpoint-sqlite`. Docs: <https://ilmek.aimtune.dev/checkpointers/postgres>.
