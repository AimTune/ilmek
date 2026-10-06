# A2A conformance

[`agent.json`](agent.json) scripts a remote A2A agent — its Agent Card and the
task (or bare message) each `message/send` returns, keyed by the message text,
plus one JSON-RPC error. `@ilmek/a2a` and `Ilmek.A2A` each build a fake
transport from it, connect an `A2aAgent`, send every scripted text, and must
reduce the results to [`expected.json`](expected.json) as canonical JSON:

| field | pins |
|---|---|
| `name` | the agent name slugified from the card (`support-desk`) |
| `results` | every send's **normalized** result — `taskId`, `contextId`, `state`, `text` (artifact text joined), `statusText`, `statusData`, `needsInput` |
| `error` | the `A2aError` code and message for the scripted JSON-RPC error |

The raw `task` is not pinned (it is the fixture's own input). `expected.json`
is generated once by the TypeScript reference
(`node ts/packages/a2a/scripts/gen-expected.ts --write`), hand-reviewed and
committed; the .NET suite reruns it unchanged. Journaling (a `send` runs once
across an interrupt/resume) and the `a2a_call` node type are asserted inline in
each language.
