# @ilmek/checkpoint-sqlite

A durable [ilmek](https://github.com/AimTune/ilmek) checkpointer in a single
SQLite file — zero dependencies, on Node's built-in `node:sqlite`. A parked
interrupt survives a process restart, a deploy, or a crash.

```sh
npm install @ilmek/core @ilmek/checkpoint-sqlite
```

```ts
import { SqliteCheckpointer } from "@ilmek/checkpoint-sqlite";

const cp = await SqliteCheckpointer.open("./agent.db");   // creates + migrates
await run(graph, input, { threadId, checkpointer: cp });
```

Talks to a duck-typed database (`exec`/`prepare`), so `better-sqlite3` drops in
unchanged. Requires Node ≥ 22.5. For threads shared across processes, use
`@ilmek/checkpoint-postgres`.
