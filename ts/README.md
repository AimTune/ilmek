# ilmek — TypeScript

The reference implementation. [../MODEL.md](../MODEL.md) is the spec; this is the
developer guide for the TS workspace.

## Layout

```
ts/
  packages/
    core/                    @ilmek/core — the engine. zero runtime deps.
      src/  test/            tests import ../src directly (build-free)
    checkpointers/
      sqlite/                @ilmek/checkpoint-sqlite   (node:sqlite, zero deps)
      postgres/              @ilmek/checkpoint-postgres (any pg-shaped client)
  examples/                  @ilmek/examples — runnable demos
  tsconfig.base.json         shared compiler options
  tsconfig.json              solution: `tsc -b` builds every package
  pnpm-workspace.yaml
```

Each provider is its own package so `@ilmek/core` stays dependency-free: a
checkpointer that needs a driver keeps it out of core. New checkpointers are
siblings under `packages/checkpointers/`.

## Commands

```bash
pnpm install
pnpm build          # tsc -b — builds core, then providers, in dep order
pnpm test           # runs each package's test suite
pnpm test:core      # just core, against src (no build needed)
pnpm demos          # the three example demos (needs a build first)
pnpm check          # build → test → demos. the CI command.
```

`pnpm test` and `pnpm demos` consume the **built** `@ilmek/core`, so `pnpm check`
builds first. Core's own tests import `../src` relatively and run build-free —
that's the fast inner loop (`pnpm test:core`, or point node at a file).

### Why a build at all, given Node runs `.ts` directly?

Node's native type stripping runs `.ts` sources with no build — but it refuses
`.ts` files resolved through `node_modules` (`ERR_UNSUPPORTED_NODE_MODULES_TYPE_STRIPPING`).
A pnpm workspace symlinks `@ilmek/core`, whose real path is outside
`node_modules`, so importing it *does* work unbuilt in dev — but published
packages must ship JS. So `@ilmek/core` exports `./dist`, and anything importing
it by name (examples, provider tests) wants a build. Core's own tests sidestep
this with relative `../src` imports. Type stripping also does **not** typecheck,
which is the other reason `pnpm build` (real `tsc`) is part of `check`.

## Providers

Each provider is its own package implementing core's `Checkpointer` (MODEL.md §7)
over a **duck-typed** database handle — so none of them adds a hard driver
dependency, and core stays dependency-free.

| | when |
|---|---|
| in-memory (in core) | dev, tests. A restart loses parked interrupts. |
| `@ilmek/checkpoint-sqlite` | one process. Durable, zero dependencies — Node ships SQLite. |
| `@ilmek/checkpoint-postgres` | several processes sharing the same threads. |

```ts
import { SqliteCheckpointer } from "@ilmek/checkpoint-sqlite";
const cp = await SqliteCheckpointer.open("./agent.db");   // migrates for you
await run(graph, input, { threadId, checkpointer: cp });

import { PostgresCheckpointer } from "@ilmek/checkpoint-postgres";
const pg = new PostgresCheckpointer(pgPool);              // any pg-shaped client
await pg.migrate();
```

**SQLite** runs on Node's built-in `node:sqlite`, so its tests exercise real
SQLite and prove the thing that actually matters: a pause written by one
connection is answered by another after the file is closed and reopened — and the
effect before that pause does *not* re-run. `better-sqlite3` drops in unchanged
(same `exec`/`prepare` shape).

**Postgres** talks to anything with `query(text, params)` — node-postgres'
`Client`/`Pool` fit as-is. Its tests drive an in-memory fake client, since a
database is not always around; a live smoke test is gated on `DATABASE_URL`.

## Debugging

`../.vscode/launch.json` has configs for each demo, the core tests, the provider
tests, and the current file. Notes, all verified over CDP:

- **Positions are exact, no source maps needed.** Type stripping replaces types
  with whitespace rather than rewriting code, so a breakpoint on a line of real
  code stops on that line with real scopes. Stepping into built core resolves
  through its sourceMap back to `packages/core/src`.
- **A breakpoint that stops somewhere senseless is stale.** That whitespace trick
  is also the trap: a comment, an `import type`, an `interface` body hold no
  executable code after stripping, so a breakpoint there silently *slides
  forward* to the next real statement. Everything above a module's first
  statement collapses onto that statement — reads as "stops for no reason on line
  31". Clear all breakpoints and re-set them on lines that survive stripping.
- **Watch the journal.** Break on the `ctx.step("create_order", …)` line and the
  `ctx.step("charge", …)` line, run *Demo: checkout — scripted*, and walk both
  runs: on the second, the node re-enters from the top but `create_order` never
  fires — the debugger steps straight over it. [MODEL.md §5](../MODEL.md) with a
  stack trace.

### HITL pauses and exception breakpoints

`ctx.interrupt()` halts a task by throwing `InterruptSignal`, caught by the
engine as ordinary control flow. To V8 it is still a throw — and since
`ctx.interrupt()` is async, it surfaces as a *promiseRejection*. Two knobs:

| Want | Do |
|---|---|
| Stop on pauses, nothing else | Run *Demo: checkout — break on every HITL pause* (sets `ILMEK_DEBUG_BREAK_ON_INTERRUPT=1`) |
| Stop on real errors, not on pauses | Tick **Caught Exceptions**, then Edit Condition → `!error?.isIlmekInterrupt` |

`ILMEK_DEBUG_BREAK_ON_INTERRUPT=1` arms a `debugger` in
[packages/core/src/context.ts](packages/core/src/context.ts) where the pause is
journaled, with the journal key and outgoing question in scope; inert without the
env var. The condition uses a plain `isIlmekInterrupt` property, not `instanceof`,
because breakpoint conditions run in the paused frame where ilmek's exports are
out of scope. `skipFiles` will **not** filter it — measured: it compiles to V8
blackboxing, which still pauses on exceptions thrown inside the file.
