# Ilmek.Core

An agent graph runtime for .NET: state, nodes, edges, checkpointed memory, and
**durable** human-in-the-loop. No third-party dependencies.

A node that pauses for a human re-runs from the top on resume, so a pure-replay
engine repeats every side effect before the pause. ilmek wraps each effect in
`ctx.StepAsync(...)` — the journal replays its recorded result instead of calling
it again. An interrupt is just a step whose value comes from a human.

```csharp
var g = Graph.Create<CheckoutState>("checkout")
    .Node("checkout", async (state, ctx) =>
    {
        var order = await ctx.StepAsync("create_order", () => Orders.Create(state.Cart)); // once, ever
        var ok = await ctx.InterruptAsync<string>(new { question = "Charge?" });
        await ctx.StepAsync("charge", () => Payments.Charge(order, ok));
        return Update.For<CheckoutState>().Append(s => s.Log, "done");
    })
    .Edge(Graph.Start, "checkout").Edge("checkout", Graph.End)
    .Compile();

var opts = new RunOptions { ThreadId = "conv-42", Checkpointer = new InMemoryCheckpointer() };
await g.RunAsync(input, opts);      // Interrupted
await g.ResumeAsync("yes", opts);   // Done
```

Spec and docs: <https://github.com/AimTune/ilmek>. Durable storage:
`Ilmek.Checkpointer.Sqlite`.
