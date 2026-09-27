# @ilmek/a2a

Call [Agent2Agent](https://a2a-protocol.org) agents from an
[ilmek](https://github.com/AimTune/ilmek) graph — every round-trip journaled,
so a pause/resume never re-sends a message the remote agent already acted on.
Zero dependencies; the HTTP transport rides the global `fetch`.

```sh
npm install @ilmek/core @ilmek/a2a
```

```ts
import { A2aAgent, HttpA2aTransport } from "@ilmek/a2a";

const desk = await A2aAgent.connect(new HttpA2aTransport("https://desk.example.com"), { name: "desk" });
const r = await desk.send(ctx, "Where is ORD-42?");                       // once across resumes
if (r.needsInput) await desk.send(ctx, "Approve", { taskId: r.taskId, key: "answer" });
```

A remote agent's human-in-the-loop pause arrives as an `input-required` task
with the open questions in `statusText` / `statusData`; the node decides whether
to answer itself, ask its own human (`ctx.interrupt`), or give up.
`a2aNodes({ desk })` adds an `a2a_call` node type for stored graph specs.
Docs: <https://ilmek.aimtune.dev/a2a>.
