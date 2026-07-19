# @ilmek/checkpoint-postgres

A durable [ilmek](https://github.com/AimTune/ilmek) checkpointer backed by
Postgres — for threads shared across processes. No hard `pg` dependency: it talks
to any node-postgres-style client (`query(text, params)`), so a `Client` or
`Pool` drops in as-is.

```ts
import { PostgresCheckpointer } from "@ilmek/checkpoint-postgres";

const cp = new PostgresCheckpointer(pool);
await cp.migrate();
await run(graph, input, { threadId, checkpointer: cp });
```

For single-process durability with no external database, use
`@ilmek/checkpoint-sqlite`.
