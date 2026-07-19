// The pitch, executable.
//
//   dotnet run --project examples/Ilmek.Examples              → checkout demo
//   dotnet run --project examples/Ilmek.Examples -- mapreduce → fan-out + retry
//
// A node opens an order, pauses for a human, then charges. The interesting part
// is what happens on resume: the node body re-runs from the top, but the order is
// NOT opened a second time.
//
// Both demos use the typed graph API: the state is a class, channels are its
// properties, and `state.Cart` / `result.State.Log` are typed all the way out.

using Ilmek;

var demo = args.FirstOrDefault() ?? "checkout";
return demo switch
{
    "checkout" => await CheckoutDemo.RunAsync(),
    "mapreduce" => await MapReduceDemo.RunAsync(),
    _ => Fail($"unknown demo \"{demo}\" — try: checkout | mapreduce"),
};

static int Fail(string message)
{
    Console.WriteLine($"   ❌ {message}");
    return 1;
}

internal static class CheckoutDemo
{
    // The state lives in one named place. Cart replaces (default last_write); Log
    // accumulates ([Append]) — the reducers are declared on the class, so the
    // graph needs no .Channel() calls.
    public sealed class CheckoutState
    {
        public List<string> Cart { get; set; } = new();
        [Append] public List<string> Log { get; set; } = new();
    }

    // The pause payload, typed too — the client renders these fields.
    public sealed record Question(string Text, IReadOnlyList<string> Options);

    // Stand-ins that shout when called, so double effects are visible.
    private static readonly List<string> Calls = new();

    private static (string Id, decimal Total) CreateOrder(IReadOnlyList<string> cart)
    {
        Calls.Add("create_order");
        Console.WriteLine($"   💳 SIDE EFFECT: opening an order for [{string.Join(", ", cart)}]");
        return ("ord-9001", 249.9m);
    }

    private static string Charge(string orderId, decimal total)
    {
        Calls.Add("charge");
        Console.WriteLine($"   💰 SIDE EFFECT: charging {total} on {orderId}");
        return "charged";
    }

    public static async Task<int> RunAsync()
    {
        var g = Graph.Create<CheckoutState>("checkout")
            .Node("checkout", async (state, ctx) =>
            {
                // state.Cart is List<string> — no casts, no string keys.
                var order = await ctx.StepAsync("create_order", () => CreateOrder(state.Cart));

                var answer = await ctx.InterruptAsync<string>(
                    new Question($"Charge {order.Total}?", new[] { "yes", "no" }));

                if (answer != "yes")
                    return Update.For<CheckoutState>().Append(s => s.Log, $"order {order.Id} cancelled");

                await ctx.StepAsync("charge", () => Charge(order.Id, order.Total));
                return Update.For<CheckoutState>().Append(s => s.Log, $"order {order.Id} charged");
            })
            .Edge(Graph.Start, "checkout")
            .Edge("checkout", Graph.End)
            .Compile();

        var opts = new RunOptions { ThreadId = "conv-42", Checkpointer = new InMemoryCheckpointer() };
        var input = Update.For<CheckoutState>().Set(s => s.Cart, new List<string> { "coffee", "mug" });

        Console.WriteLine("\n── run 1: user asks to check out ─────────────────────────────");
        var result = await g.RunAsync(input, opts);

        var round = 1;
        while (result.Status == RunStatus.Interrupted)
        {
            var question = (Question)result.Pending[0].Payload!;
            Console.WriteLine($"\n   ⏸  paused at {result.Pending[0].Id}: {question.Text}");
            Console.WriteLine("\n── the process could die here. a deploy could happen here. ───");
            Console.WriteLine("   the pause lives in the checkpointer, not in memory.");

            Console.WriteLine($"\n── run {++round}: the human answers ──────────────────────────");
            result = await g.ResumeAsync("yes", opts);
        }

        Console.WriteLine($"\n   status: {result.Status}");
        Console.WriteLine($"   log:    [{string.Join(", ", result.State!.Log)}]");

        Console.WriteLine("\n── what actually got called ──────────────────────────────────");
        Console.WriteLine($"   [{string.Join(", ", Calls)}]");

        var creates = Calls.Count(c => c == "create_order");
        if (creates != 1) return Fail($"create_order ran {creates} times across {round} runs — expected 1");

        Console.WriteLine($"""

   ✅ create_order ran ONCE across {round} runs.

      The node re-ran from the top on resume — but the journal had already
      paid for create_order, so it returned the recorded order instead of
      opening another one. A pure-replay engine (LangGraph's model) would
      print create_order {round} times here, and the fix would be your problem:
      "put the side effect below the pause that gates it."

""");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.WriteLine($"\n   ❌ {message}\n");
        return 1;
    }
}

internal static class MapReduceDemo
{
    // The graph state: words to shout in, results out (accumulated), a round counter.
    public sealed class ShoutState
    {
        public List<string> Words { get; set; } = new();
        [Append] public List<string> Shouted { get; set; } = new();
        public long Rounds { get; set; }
    }

    // A fan-out worker's input is its OWN type, not the graph state (MODEL.md §14).
    public sealed class Job
    {
        public string Word { get; set; } = "";
        public int FailsFor { get; set; }
    }

    private static readonly Dictionary<string, int> Attempts = new();

    // A service that fails the first `failsFor` calls per key, then succeeds.
    private static string FlakyUppercase(string key, string text, int failsFor)
    {
        var n = Attempts.TryGetValue(key, out var c) ? c + 1 : 1;
        Attempts[key] = n;
        if (n <= failsFor) throw new InvalidOperationException($"503 on {key} (attempt {n})");
        return text.ToUpperInvariant();
    }

    public static async Task<int> RunAsync()
    {
        var retry = new RetryPolicy { MaxAttempts = 4, Backoff = TimeSpan.FromMilliseconds(5), Factor = 2 };

        var g = Graph.Create<ShoutState>("shout")
            .Node("plan", (_, _) => null)
            // A typed fan-out worker: it receives a Job, and retries its flaky call
            // safely because completed steps are journaled.
            .Node<Job>("worker", (job, ctx) =>
            {
                var shouted = FlakyUppercase(job.Word, job.Word, job.FailsFor);
                ctx.Emit(new Dictionary<string, object?> { ["worked"] = job.Word });
                return Update.For<ShoutState>().Append(s => s.Shouted, shouted);
            }, retry: retry)
            // Node-directed routing: loop once, then finish.
            .Node("gate", (state, _) => state.Rounds == 0
                ? Command.Create(
                    Update.For<ShoutState>().Set(s => s.Rounds, 1L).Set(s => s.Words, new List<string> { "again" }),
                    "plan")
                : Command.Goto_(Graph.End))
            .Edge(Graph.Start, "plan")
            .Router("plan", (state, _) => state.Words
                .Select((word, i) => (object)new Send("worker", new Job { Word = word, FailsFor = i })))
            .Edge("worker", "gate")
            .Compile();

        Console.WriteLine("\n── fan-out with retries, then one routed loop ────────────────");

        var retries = 0;
        var workers = 0;
        List<string> shouted = new();

        var input = Update.For<ShoutState>().Set(s => s.Words, new List<string> { "red", "green", "blue" });

        await foreach (var ev in g.StreamEvents(input))
        {
            switch (ev)
            {
                case NodeRetryEvent r:
                    retries++;
                    Console.WriteLine($"   ↻ retry {r.Node} attempt {r.Attempt}: {r.Error.Message}");
                    break;
                case CustomEvent { Payload: IReadOnlyDictionary<string, object?> p } when p.ContainsKey("worked"):
                    workers++;
                    Console.WriteLine($"   ✓ worked {p["worked"]}");
                    break;
                case RunEndEvent { Status: RunStatus.Done, FinalState: not null } end:
                    shouted = new State(end.FinalState).GetList<string>(nameof(ShoutState.Shouted));
                    break;
            }
        }

        Console.WriteLine($"\n   worker successes: {workers}   retries observed: {retries}");
        Console.WriteLine($"   shouted: [{string.Join(", ", shouted)}]");

        if (retries == 0 || shouted.Count != 4 || shouted.Any(s => s != s.ToUpperInvariant()))
        {
            Console.WriteLine($"\n   ❌ unexpected: retries={retries}, shouted=[{string.Join(", ", shouted)}]\n");
            return 1;
        }

        Console.WriteLine("""

   ✅ 4 words shouted across two rounds; every flaky call retried to success,
      and no word was processed twice despite the retries (the journal held
      each worker's step).

""");
        return 0;
    }
}
