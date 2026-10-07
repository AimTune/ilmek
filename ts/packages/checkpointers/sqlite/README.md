# @ilmek/checkpoint-sqlite

A durable [ilmek](https://github.com/AimTune/ilmek) checkpointer in a single
SQLite file — zero dependencies, on Node's built-in `node:sqlite`. A parked
interrupt survives a process restart, a deploy, or a crash.

```sh
npm install @ilmek/core @ilmek/checkpoint-sqlite
```

```ts
import { run } from "@ilmek/core";
import { SqliteCheckpointer } from "@ilmek/checkpoint-sqlite";

const cp = await SqliteCheckpointer.open("./agent.db");   // creates + migrates
await run(graph, input, { threadId, checkpointer: cp });
```

Talks to a duck-typed database (`exec`/`prepare`), so `better-sqlite3` drops in
unchanged: `new SqliteCheckpointer(db, opts?)`. Options: `tablePrefix` (default
`"ilmek"`, must be a plain SQL identifier) and `wal` (default `true`). Requires
Node ≥ 22.5. For threads shared across processes, use
`@ilmek/checkpoint-postgres`. Docs: <https://ilmek.aimtune.dev/checkpointers/sqlite>.
