using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Ilmek;
using Ilmek.A2A;

namespace Ilmek.A2A.Tests;

/// <summary>
/// The agent over a scripted transport, journaled sends, task normalization, the
/// HTTP transport's URL handling, and the <c>a2a_call</c> node type. The scripted
/// agent in conformance/a2a is the cross-language fixture.
/// </summary>
public class A2aTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "a2a");

    private sealed class FakeTransport : IA2aTransport
    {
        private readonly IReadOnlyDictionary<string, object?> _script = (IReadOnlyDictionary<string, object?>)PlainJson.Parse(File.ReadAllText(Path.Combine(Fixtures, "agent.json")))!;
        public List<IReadOnlyDictionary<string, object?>> Requests { get; } = new();
        public int CardFetches;
        public IReadOnlyDictionary<string, object?> Sends => (IReadOnlyDictionary<string, object?>)_script["sends"]!;

        public Task<IReadOnlyDictionary<string, object?>> GetAgentCardAsync(CancellationToken ct = default)
        {
            CardFetches++;
            return Task.FromResult((IReadOnlyDictionary<string, object?>)_script["card"]!);
        }

        public Task<IReadOnlyDictionary<string, object?>> PostAsync(IReadOnlyDictionary<string, object?> request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var id = request["id"];
            var p = (IReadOnlyDictionary<string, object?>)request["params"]!;
            var method = (string)request["method"]!;
            if (method == "message/send")
            {
                var text = A2aParts.TextOf(((IReadOnlyDictionary<string, object?>)p["message"]!)["parts"]);
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(Sends.TryGetValue(text, out var result)
                    ? new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }
                    : new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new Dictionary<string, object?> { ["code"] = -32602L, ["message"] = $"unscripted message \"{text}\"" } });
            }
            if (method is "tasks/get" or "tasks/cancel")
            {
                var task = Sends.Values.OfType<IReadOnlyDictionary<string, object?>>().FirstOrDefault(t => t.GetValueOrDefault("id") as string == p.GetValueOrDefault("id") as string);
                if (task is null)
                    return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = ((IReadOnlyDictionary<string, object?>)_script["errors"]!)["unknown task"] });
                var result = method == "tasks/cancel" ? new Dictionary<string, object?>(task) { ["status"] = new Dictionary<string, object?> { ["state"] = "canceled" } } : task;
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new Dictionary<string, object?> { ["code"] = -32601L, ["message"] = $"method not found: {method}" } });
        }
    }

    private static readonly JsonSerializerOptions CanonicalOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Canonical(object? value) => Sort(JsonSerializer.SerializeToNode(value, CanonicalOptions))?.ToJsonString(CanonicalOptions) ?? "null";
    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
        _ => node.DeepClone(),
    };

    private static Task<A2aAgent> Connect(FakeTransport? t = null) => A2aAgent.ConnectAsync(t ?? new FakeTransport(), mintId: () => "m-x");

    [Fact(DisplayName = "the scripted agent reduces to expected.json")]
    public async Task FixtureMatchesExpected()
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "expected.json")));
        var transport = new FakeTransport();
        var agent = await Connect(transport);
        var results = new Dictionary<string, object?>();
        foreach (var text in transport.Sends.Keys) results[text] = (await agent.InvokeAsync(text)).ToDictionary();
        object? error;
        try { await agent.GetTaskAsync("task-nope"); error = null; }
        catch (A2aException ex) { error = new Dictionary<string, object?> { ["code"] = (long)ex.Code, ["message"] = ex.Message }; }
        var actual = new Dictionary<string, object?> { ["name"] = agent.Name, ["results"] = results, ["error"] = error };
        Assert.Equal(Sort(expected)!.ToJsonString(CanonicalOptions), Canonical(actual));
    }

    [Fact(DisplayName = "connect fetches the card once and slugs the name unless given")]
    public async Task ConnectSlugsName()
    {
        var t = new FakeTransport();
        var agent = await Connect(t);
        Assert.Equal("support-desk", agent.Name);
        Assert.Equal("https://bot.example.com/a2a", agent.Card["url"]);
        Assert.Equal(1, t.CardFetches);
        Assert.Equal("desk", (await A2aAgent.ConnectAsync(new FakeTransport(), name: "desk")).Name);
    }

    [Fact(DisplayName = "invoke sends a user message with text and data, and reads the task")]
    public async Task InvokeSends()
    {
        var t = new FakeTransport();
        var agent = await Connect(t);
        var r = await agent.InvokeAsync("where is my order?", new SendOptions { ContextId = "conv-1", Data = new Dictionary<string, object?> { ["hint"] = 1L } });
        var message = (IReadOnlyDictionary<string, object?>)((IReadOnlyDictionary<string, object?>)t.Requests[0]["params"]!)["message"]!;
        Assert.Equal("message/send", t.Requests[0]["method"]);
        Assert.Equal("conv-1", message["contextId"]);
        Assert.Equal("m-x", message["messageId"]);
        Assert.Equal(2, ((IEnumerable<object?>)message["parts"]!).Count());
        Assert.Equal("completed", r.State);
        Assert.Equal("Order ORD-42 totals 249.9.", r.Text);
        Assert.False(r.NeedsInput);
        Assert.Equal("", r.StatusText);
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync(""));
    }

    [Fact(DisplayName = "an input-required task exposes the question and data; a reply on the task continues it")]
    public async Task InputRequired()
    {
        var agent = await Connect();
        var paused = await agent.InvokeAsync("refund please");
        Assert.True(paused.NeedsInput);
        Assert.Contains("needs input before it can continue", paused.StatusText);
        var pending = ((IEnumerable<object?>)paused.StatusData!["pending"]!).Cast<IReadOnlyDictionary<string, object?>>().ToList();
        Assert.Equal("agent:interrupt#0", pending[0]["id"]);
        var done = await agent.InvokeAsync("Approve", new SendOptions { TaskId = paused.TaskId });
        Assert.Equal("completed", done.State);
        Assert.Equal("Refunded.", done.Text);
    }

    [Fact(DisplayName = "a JSON-RPC error becomes an A2aException; a bare message becomes a completed task")]
    public async Task ErrorsAndBareMessages()
    {
        var agent = await Connect();
        Assert.Equal(-32001, (await Assert.ThrowsAsync<A2aException>(() => agent.GetTaskAsync("task-nope"))).Code);
        Assert.Equal(-32602, (await Assert.ThrowsAsync<A2aException>(() => agent.InvokeAsync("unscripted"))).Code);
        var bare = await agent.InvokeAsync("just a message");
        Assert.Equal("completed", bare.State);
        Assert.Equal("A bare message, no task.", bare.Text);
        Assert.Equal("conv-3", bare.ContextId);
        Assert.Equal("canceled", (await agent.CancelTaskAsync("task-1")).State);
    }

    [Fact(DisplayName = "send is journaled: once across an interrupt/resume; distinct keys for ask and answer")]
    public async Task SendIsJournaled()
    {
        var t = new FakeTransport();
        var agent = await Connect(t);
        var g = Graph.Create("delegate")
            .Channel("log", Channels.Append())
            .Node("ask", async (State _, IContext ctx) =>
            {
                var first = await agent.SendAsync(ctx, "refund please");
                var ok = await ctx.InterruptAsync<string>(new Dictionary<string, object?> { ["q"] = first.StatusText });
                var done = await agent.SendAsync(ctx, ok, new SendOptions { TaskId = first.TaskId, Key = "answer" });
                return Update.Of("log", new List<object?> { first.State, done.Text });
            })
            .Edge(Graph.Start, "ask").Edge("ask", Graph.End)
            .Compile();
        var opts = new RunOptions { ThreadId = "t-1", Checkpointer = new InMemoryCheckpointer() };
        var paused = await g.RunAsync(new Dictionary<string, object?>(), opts);
        Assert.Equal(RunStatus.Interrupted, paused.Status);
        Assert.Single(t.Requests);
        var done = await g.ResumeAsync("Approve", opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(["input-required", "Refunded."], done.State!.Get<IReadOnlyList<object?>>("log").Cast<string>().ToArray());
        Assert.Equal(2, t.Requests.Count);
    }

    [Fact(DisplayName = "send survives a SQLite reload: the replayed A2aResult is typed and identical")]
    public async Task SendSurvivesSqliteReload()
    {
        // A durable checkpointer hands the journal back as plain JSON data; the
        // replayed SendAsync must still produce an A2aResult, not throw an
        // InvalidCastException, and must not post to the agent again.
        var dir = Directory.CreateTempSubdirectory("ilmek-a2a-").FullName;
        var path = Path.Combine(dir, "a2a.db");
        try
        {
            var t = new FakeTransport();
            var agent = await Connect(t);
            var firsts = new List<A2aResult>();
            var g = Graph.Create("delegate")
                .Channel("log", Channels.Append())
                .Node("ask", async (State _, IContext ctx) =>
                {
                    var first = await agent.SendAsync(ctx, "refund please");
                    firsts.Add(first);
                    var ok = await ctx.InterruptAsync<string>(new Dictionary<string, object?> { ["q"] = first.StatusText });
                    var done = await agent.SendAsync(ctx, ok, new SendOptions { TaskId = first.TaskId, Key = "answer" });
                    return Update.Of("log", new List<object?> { first.State, done.Text });
                })
                .Edge(Graph.Start, "ask").Edge("ask", Graph.End)
                .Compile();

            using (var cp = Ilmek.Checkpointers.Sqlite.SqliteCheckpointer.Open(path))
            {
                var paused = await g.RunAsync(new Dictionary<string, object?>(), new RunOptions { ThreadId = "t-sqlite", Checkpointer = cp });
                Assert.Equal(RunStatus.Interrupted, paused.Status);
            }

            using (var cp = Ilmek.Checkpointers.Sqlite.SqliteCheckpointer.Open(path))
            {
                var done = await g.ResumeAsync("Approve", new RunOptions { ThreadId = "t-sqlite", Checkpointer = cp });
                Assert.Equal(RunStatus.Done, done.Status);
                Assert.Equal(["input-required", "Refunded."], done.State!.Get<IReadOnlyList<object?>>("log").Cast<string>().ToArray());
            }

            Assert.Equal(2, t.Requests.Count); // the first send was not re-posted
            Assert.Equal(2, firsts.Count);
            Assert.Equal(Canonical(firsts[0].ToDictionary()), Canonical(firsts[1].ToDictionary()));
            Assert.Equal(Canonical(firsts[0].Task), Canonical(firsts[1].Task));
            Assert.True(firsts[1].NeedsInput);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact(DisplayName = "Normalize joins artifact text, reads status text and data, flags input-required")]
    public void NormalizeReducesATask()
    {
        var task = (IReadOnlyDictionary<string, object?>)PlainJson.Parse("""
            {"id":"t","contextId":"c","status":{"state":"input-required","message":{"messageId":"m","role":"agent","parts":[{"kind":"text","text":"why?"},{"kind":"data","data":{"a":1}}]}},
             "artifacts":[{"artifactId":"a","parts":[{"kind":"text","text":"one"}]},{"artifactId":"b","parts":[{"kind":"text","text":"two"}]}]}
            """)!;
        var r = A2aAgent.Normalize(task);
        Assert.Equal("one\ntwo", r.Text);
        Assert.Equal("why?", r.StatusText);
        Assert.Equal(1L, Convert.ToInt64(r.StatusData!["a"]));
        Assert.True(r.NeedsInput);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<string> Calls { get; } = new();
        public HttpStatusCode CardStatus = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add($"{request.Method} {request.RequestUri}");
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".json"))
            {
                return Task.FromResult(new HttpResponseMessage(CardStatus) { Content = new StringContent("""{"name":"X","url":"https://bot.example.com/rpc"}""") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"jsonrpc":"2.0","id":1,"result":{}}""") });
        }
    }

    [Fact(DisplayName = "HttpA2aTransport derives the card URL from an origin and the endpoint from the card")]
    public async Task HttpTransportUrls()
    {
        var handler = new ScriptedHandler();
        var http = new HttpClient(handler);
        var ping = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = 1L, ["method"] = "ping" };

        await new HttpA2aTransport(http, "https://bot.example.com").PostAsync(ping);
        Assert.Equal(["GET https://bot.example.com/.well-known/agent-card.json", "POST https://bot.example.com/rpc"], handler.Calls.ToArray());

        handler.Calls.Clear();
        await new HttpA2aTransport(http, "https://bot.example.com/a2a").PostAsync(ping);
        Assert.Equal(["POST https://bot.example.com/a2a"], handler.Calls.ToArray());

        handler.Calls.Clear();
        Assert.Equal("X", (await new HttpA2aTransport(http, "https://bot.example.com/custom/card.json").GetAgentCardAsync())["name"]);
        Assert.Equal(["GET https://bot.example.com/custom/card.json"], handler.Calls.ToArray());

        handler.CardStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<HttpRequestException>(() => new HttpA2aTransport(http, "https://bot.example.com").GetAgentCardAsync());
    }

    [Fact(DisplayName = "a2a_call sends channel text, continues a context, answers a task, writes text or the whole result")]
    public async Task NodeType()
    {
        var t = new FakeTransport();
        var agents = new Dictionary<string, A2aAgent> { ["desk"] = await Connect(t) };
        var spec = new GraphSpec
        {
            Name = "delegate",
            Channels = new Dictionary<string, SpecChannel> { ["question"] = new(), ["ctx"] = new(), ["task"] = new(), ["answer"] = new(), ["full"] = new() },
            Nodes =
            [
                new SpecNode("ask", "a2a_call", new Dictionary<string, object?> { ["agent"] = "desk", ["textFrom"] = "question", ["contextFrom"] = "ctx", ["to"] = "answer", ["textOnly"] = true }),
                new SpecNode("approve", "a2a_call", new Dictionary<string, object?> { ["agent"] = "desk", ["text"] = "Approve", ["taskFrom"] = "task", ["to"] = "full" }),
            ],
            Edges = [new SpecEdge(Graph.Start, "ask"), new SpecEdge("ask", "approve"), new SpecEdge("approve", Graph.End)],
        };
        var g = Spec.FromSpec(spec, A2aNodes.Registry(agents)).Compile();
        var result = await g.RunAsync(new Dictionary<string, object?> { ["question"] = "where is my order?", ["ctx"] = "conv-1", ["task"] = "task-2" }, new RunOptions { ThreadId = "t-2" });
        Assert.Equal(RunStatus.Done, result.Status);
        Assert.Equal("Order ORD-42 totals 249.9.", result.State!.Get<string>("answer"));
        Assert.Equal("Refunded.", ((IReadOnlyDictionary<string, object?>)result.State!["full"]!)["text"]);
        var ask = (IReadOnlyDictionary<string, object?>)((IReadOnlyDictionary<string, object?>)t.Requests[0]["params"]!)["message"]!;
        var approve = (IReadOnlyDictionary<string, object?>)((IReadOnlyDictionary<string, object?>)t.Requests[1]["params"]!)["message"]!;
        Assert.Equal("conv-1", ask["contextId"]);
        Assert.Equal("task-2", approve["taskId"]);
    }

    [Fact(DisplayName = "bad references fail at build time")]
    public async Task BadReferences()
    {
        var registry = A2aNodes.Registry(new Dictionary<string, A2aAgent> { ["desk"] = await Connect() });
        GraphSpec Spec1(Dictionary<string, object?> config) => new()
        {
            Name = "bad",
            Channels = new Dictionary<string, SpecChannel> { ["result"] = new() },
            Nodes = [new SpecNode("n", "a2a_call", config)],
            Edges = [new SpecEdge(Graph.Start, "n"), new SpecEdge("n", Graph.End)],
        };
        Assert.Contains("known: [\"desk\"]", Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(new() { ["agent"] = "other", ["text"] = "x" }), registry)).Message);
        Assert.Contains("config.agent", Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(new() { ["text"] = "x" }), registry)).Message);
        Assert.Contains("config.text or config.textFrom", Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(new() { ["agent"] = "desk" }), registry)).Message);
        Assert.Contains("config.to", Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(new() { ["agent"] = "desk", ["text"] = "x", ["to"] = 5L }), registry)).Message);
    }
}
