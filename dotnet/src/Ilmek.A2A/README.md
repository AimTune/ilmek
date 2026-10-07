# Ilmek.A2A

Call [Agent2Agent](https://a2a-protocol.org) agents from an
[ilmek](https://github.com/AimTune/ilmek) graph — every round-trip journaled,
so a pause/resume never re-sends a message the remote agent already acted on.
No third-party dependencies: the transport is a two-method interface and the
HTTP one rides `HttpClient`.

```csharp
using Ilmek.A2A;

var desk = await A2aAgent.ConnectAsync(new HttpA2aTransport(http, "https://bot.example.com"), name: "desk");
var r = await desk.SendAsync(ctx, "Where is ORD-42?");                                   // once across resumes
if (r.NeedsInput) await desk.SendAsync(ctx, "Approve", new() { TaskId = r.TaskId, Key = "answer" });
```

A remote agent's human-in-the-loop pause arrives as an `input-required` task
with the open questions in `StatusText` / `StatusData`; the node decides whether
to answer itself, ask its own human (`ctx.InterruptAsync`), or give up.
`A2aNodes.Registry(agents)` adds an `a2a_call` node type for stored graph specs.
Byte-identical to `@ilmek/a2a` (shared fixture in `conformance/a2a`).
Docs: <https://ilmek.aimtune.dev/a2a>.
