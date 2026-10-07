using System.Net;
using Ilmek;
using Ilmek.A2A;

namespace Ilmek.A2A.Tests;

/// <summary>
/// What a broken or hostile A2A agent can answer: JSON-RPC errors, responses with
/// neither result nor error, results of the wrong shape, tasks without a status,
/// HTTP failures. Each must surface as a clear A2aException (or HTTP error), never
/// a NullReference/InvalidCast crash. Mirrors the TS error-path suite.
/// </summary>
public class A2aErrorPathTests
{
    private static readonly IReadOnlyDictionary<string, object?> Card = new Dictionary<string, object?> { ["name"] = "Helper Bot" };

    /// <summary>A transport that answers every request with whatever the test scripts.</summary>
    private sealed class Scripted(Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>> answer) : IA2aTransport
    {
        public int Posts;
        public Task<IReadOnlyDictionary<string, object?>> GetAgentCardAsync(CancellationToken ct = default) => Task.FromResult(Card);

        public Task<IReadOnlyDictionary<string, object?>> PostAsync(IReadOnlyDictionary<string, object?> request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Posts);
            return Task.FromResult(answer(request));
        }
    }

    private static Dictionary<string, object?> Rpc(object? result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = 1L, ["result"] = result };

    private static Dictionary<string, object?> Task_(string id, string state, string? reply = null) => new()
    {
        ["kind"] = "task", ["id"] = id, ["contextId"] = "ctx-1",
        ["status"] = new Dictionary<string, object?> { ["state"] = state },
        ["artifacts"] = reply is null ? new List<object?>() : new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["artifactId"] = "a1",
                ["parts"] = new List<object?> { new Dictionary<string, object?> { ["kind"] = "text", ["text"] = reply } },
            },
        },
    };

    private static Task<A2aAgent> Connect(IA2aTransport t) => A2aAgent.ConnectAsync(t);

    // ── JSON-RPC envelope ───────────────────────────────────────────────────

    [Fact(DisplayName = "a JSON-RPC error surfaces as A2aException with its code, message and data")]
    public async Task RpcError()
    {
        var agent = await Connect(new Scripted(_ => new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = 1L,
            ["error"] = new Dictionary<string, object?> { ["code"] = -32001L, ["message"] = "task not found", ["data"] = "t-9" },
        }));
        var ex = await Assert.ThrowsAsync<A2aException>(() => agent.GetTaskAsync("t-9"));
        Assert.Equal(-32001, ex.Code);
        Assert.Equal("task not found", ex.Message);
        Assert.Equal("t-9", ex.Data2);
    }

    [Fact(DisplayName = "a JSON-RPC error with a non-numeric code or no message still surfaces as A2aException")]
    public async Task SloppyRpcError()
    {
        var agent = await Connect(new Scripted(_ => new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?> { ["code"] = "oops" },
        }));
        var ex = await Assert.ThrowsAsync<A2aException>(() => agent.InvokeAsync("hi"));
        Assert.Equal(-32603, ex.Code);
        Assert.Equal("error", ex.Message);
    }

    public static TheoryData<string, object?> MalformedResults() => new()
    {
        { "message/send", null },
        { "message/send", "a string" },
        { "message/send", new List<object?>() },
        { "tasks/get", null },
        { "tasks/get", 42L },
        { "tasks/cancel", null },
    };

    [Theory(DisplayName = "a response with neither a result object nor an error is A2aException -32603 naming the method")]
    [MemberData(nameof(MalformedResults))]
    public async Task NoResultNoError(string method, object? result)
    {
        var agent = await Connect(new Scripted(_ => Rpc(result)));
        Func<Task> call = method switch
        {
            "message/send" => () => agent.InvokeAsync("hi"),
            "tasks/get" => () => agent.GetTaskAsync("t1"),
            _ => () => agent.CancelTaskAsync("t1"),
        };

        var ex = await Assert.ThrowsAsync<A2aException>(call);
        Assert.Equal(-32603, ex.Code);
        Assert.Equal($"A2A {method}: malformed JSON-RPC response — no result object and no error", ex.Message);
    }

    [Fact(DisplayName = "an empty response object is A2aException, not a crash")]
    public async Task EmptyResponse()
    {
        var agent = await Connect(new Scripted(_ => new Dictionary<string, object?>()));
        Assert.Equal(-32603, (await Assert.ThrowsAsync<A2aException>(() => agent.InvokeAsync("hi"))).Code);
    }

    // ── result shapes ───────────────────────────────────────────────────────

    [Fact(DisplayName = "a message/send result that is neither a task nor a message is refused")]
    public async Task NeitherTaskNorMessage()
    {
        var agent = await Connect(new Scripted(_ => Rpc(new Dictionary<string, object?> { ["hello"] = "world" })));
        var ex = await Assert.ThrowsAsync<A2aException>(() => agent.InvokeAsync("hi"));
        Assert.Equal(-32603, ex.Code);
        Assert.Equal("A2A message/send: the result is neither a task nor a message", ex.Message);
    }

    [Fact(DisplayName = "a message missing its parts list is not a message")]
    public async Task MessageWithoutParts()
    {
        var agent = await Connect(new Scripted(_ => Rpc(new Dictionary<string, object?> { ["kind"] = "message", ["messageId"] = "m1" })));
        await Assert.ThrowsAsync<A2aException>(() => agent.InvokeAsync("hi"));
    }

    [Fact(DisplayName = "a bare message result is wrapped as a completed task carrying its text")]
    public async Task BareMessageWrapped()
    {
        var agent = await Connect(new Scripted(_ => Rpc(new Dictionary<string, object?>
        {
            ["kind"] = "message", ["messageId"] = "m1", ["role"] = "agent",
            ["parts"] = new List<object?> { new Dictionary<string, object?> { ["kind"] = "text", ["text"] = "hi back" } },
        })));
        var r = await agent.InvokeAsync("hi");
        Assert.Equal(("message:m1", "helper-bot", "completed", "hi back"), (r.TaskId, r.ContextId, r.State, r.Text));
        Assert.False(r.NeedsInput);
    }

    [Fact(DisplayName = "a task without a status is A2aException -32603 naming the task")]
    public async Task TaskWithoutStatus()
    {
        var agent = await Connect(new Scripted(_ => Rpc(new Dictionary<string, object?> { ["kind"] = "task", ["id"] = "t-7" })));
        var ex = await Assert.ThrowsAsync<A2aException>(() => agent.GetTaskAsync("t-7"));
        Assert.Equal(-32603, ex.Code);
        Assert.Equal("A2A task t-7 has no status", ex.Message);
        Assert.Throws<A2aException>(() => A2aAgent.Normalize(new Dictionary<string, object?> { ["id"] = "t-8" }));
    }

    [Fact(DisplayName = "a task with sloppy parts normalizes from its well-formed pieces only")]
    public void SloppyParts()
    {
        var r = A2aAgent.Normalize(new Dictionary<string, object?>
        {
            ["id"] = "t", ["contextId"] = "c",
            ["status"] = new Dictionary<string, object?>
            {
                ["state"] = "input-required",
                ["message"] = new Dictionary<string, object?>
                {
                    ["parts"] = new List<object?>
                    {
                        null, "not a part", new Dictionary<string, object?> { ["kind"] = "text", ["text"] = 5L },
                        new Dictionary<string, object?> { ["kind"] = "text", ["text"] = "need a date" },
                        new Dictionary<string, object?> { ["kind"] = "data", ["data"] = new Dictionary<string, object?> { ["pending"] = 1L } },
                    },
                },
            },
            ["artifacts"] = new List<object?> { null, "junk", new Dictionary<string, object?> { ["parts"] = "not a list" } },
        });
        Assert.Equal("", r.Text);
        Assert.Equal("need a date", r.StatusText);
        Assert.Equal(1L, r.StatusData!["pending"]);
        Assert.True(r.NeedsInput);
    }

    // ── journaling ──────────────────────────────────────────────────────────

    [Fact(DisplayName = "a JSON-RPC error is not journaled: a retry sends again and gets the real answer")]
    public async Task ErrorNotJournaled()
    {
        var t = new Scripted(req => Interlocked.CompareExchange(ref _attempt, 1, 0) == 0
            ? new Dictionary<string, object?> { ["error"] = new Dictionary<string, object?> { ["code"] = -32000L, ["message"] = "busy" } }
            : Rpc(Task_("t1", "completed", "done")));
        var agent = await Connect(t);
        var g = Graph.Create()
            .Channel("out", Channels.LastWrite())
            .Node("n", async (_, ctx) => Update.Of("out", (await agent.SendAsync(ctx, "go")).Text), retry: new RetryPolicy { MaxAttempts = 2 })
            .Edge(Graph.Start, "n")
            .Compile();

        var result = await g.RunAsync(null, new RunOptions { ThreadId = "t", Checkpointer = new InMemoryCheckpointer() });
        Assert.Equal("done", result.State!["out"]);
        Assert.Equal(2, t.Posts);
    }

    private int _attempt;

    [Fact(DisplayName = "an input-required task is journaled: replay after a pause does not message the agent again")]
    public async Task InputRequiredJournaled()
    {
        var t = new Scripted(_ => Rpc(Task_("t1", "input-required")));
        var agent = await Connect(t);
        var g = Graph.Create()
            .Channel("out", Channels.LastWrite())
            .Node("n", async (_, ctx) =>
            {
                var r = await agent.SendAsync(ctx, "book it");
                var answer = await ctx.InterruptAsync<string>(r.State);
                return Update.Of("out", $"{r.TaskId}:{answer}");
            })
            .Edge(Graph.Start, "n")
            .Compile();
        var opts = new RunOptions { ThreadId = "t", Checkpointer = new InMemoryCheckpointer() };

        var paused = await g.RunAsync(null, opts);
        Assert.Equal("input-required", paused.Pending.Single().Payload);
        var done = await g.ResumeAsync("tuesday", opts);
        Assert.Equal("t1:tuesday", done.State!["out"]);
        Assert.Equal(1, t.Posts);
    }

    // ── agent construction and messages ─────────────────────────────────────

    [Fact(DisplayName = "an agent with no name on its card and none given is refused")]
    public async Task NamelessAgent()
    {
        var t = new NamelessTransport();
        await Assert.ThrowsAsync<ArgumentException>(() => A2aAgent.ConnectAsync(t));
        Assert.Equal("given", (await A2aAgent.ConnectAsync(t, "given")).Name);
    }

    private sealed class NamelessTransport : IA2aTransport
    {
        public Task<IReadOnlyDictionary<string, object?>> GetAgentCardAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["name"] = "!!!" });
        public Task<IReadOnlyDictionary<string, object?>> PostAsync(IReadOnlyDictionary<string, object?> request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact(DisplayName = "a message with neither text nor data is refused before anything is sent")]
    public async Task EmptyMessage()
    {
        var t = new Scripted(_ => Rpc(Task_("t", "completed")));
        var agent = await Connect(t);
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync(""));
        Assert.Equal(0, t.Posts);
    }

    [Fact(DisplayName = "the message carries taskId, contextId, data and a fresh id per call")]
    public async Task MessageShape()
    {
        var seen = new List<IReadOnlyDictionary<string, object?>>();
        var agent = await Connect(new Scripted(req =>
        {
            seen.Add((IReadOnlyDictionary<string, object?>)((IReadOnlyDictionary<string, object?>)req["params"]!)["message"]!);
            return Rpc(Task_("t", "completed"));
        }));

        await agent.InvokeAsync("a", new SendOptions { TaskId = "t-1", ContextId = "c-1", Data = new Dictionary<string, object?> { ["k"] = 1L } });
        await agent.InvokeAsync("b");

        Assert.Equal("t-1", seen[0]["taskId"]);
        Assert.Equal("c-1", seen[0]["contextId"]);
        Assert.Equal(2, ((List<object?>)seen[0]["parts"]!).Count);
        Assert.False(seen[1].ContainsKey("taskId"));
        Assert.NotEqual(seen[0]["messageId"], seen[1]["messageId"]);
    }

    // ── HTTP transport ──────────────────────────────────────────────────────

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Calls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body) };

    [Fact(DisplayName = "a card without a url and no endpoint given is a clear error, not a request to nowhere")]
    public async Task CardWithoutUrl()
    {
        var handler = new Handler(_ => Json("""{"name":"X"}"""));
        var transport = new HttpA2aTransport(new HttpClient(handler), "https://bot.example.com");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.PostAsync(new Dictionary<string, object?> { ["method"] = "message/send" }));
        Assert.Equal("A2A message/send: the agent card at https://bot.example.com/.well-known/agent-card.json has no url and no endpoint was given", ex.Message);
        Assert.Equal(new[] { "GET https://bot.example.com/.well-known/agent-card.json" }, handler.Calls);
    }

    [Fact(DisplayName = "HTTP failures and non-object bodies surface as clear errors")]
    public async Task HttpFailures()
    {
        var rpcStatus = HttpStatusCode.BadGateway;
        var body = "{}";
        var handler = new Handler(req => req.Method == HttpMethod.Get
            ? Json("""{"name":"X","url":"https://bot.example.com/rpc"}""")
            : Json(body, rpcStatus));
        var transport = new HttpA2aTransport(new HttpClient(handler), "https://bot.example.com");
        var ping = new Dictionary<string, object?> { ["method"] = "ping" };

        Assert.Contains("A2A ping: HTTP 502", (await Assert.ThrowsAsync<HttpRequestException>(() => transport.PostAsync(ping))).Message);

        rpcStatus = HttpStatusCode.OK;
        body = "[1,2]";
        Assert.Contains("not a JSON object", (await Assert.ThrowsAsync<InvalidOperationException>(() => transport.PostAsync(ping))).Message);

        body = "not json";
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => transport.PostAsync(ping));
    }

    [Fact(DisplayName = "a card that is not a JSON object is refused")]
    public async Task CardNotObject()
    {
        var transport = new HttpA2aTransport(new HttpClient(new Handler(_ => Json("\"just a string\""))), "https://bot.example.com");
        Assert.Contains("not a JSON object", (await Assert.ThrowsAsync<InvalidOperationException>(() => transport.GetAgentCardAsync())).Message);
    }

    [Fact(DisplayName = "PlainJson keeps integers integral and lowers nested JSON to plain CLR data")]
    public void PlainJsonShapes()
    {
        var v = (Dictionary<string, object?>)PlainJson.Parse("""{"i":3,"d":1.5,"b":true,"n":null,"a":[1,"x"],"o":{"k":"v"}}""")!;
        Assert.Equal(3L, v["i"]);
        Assert.Equal(1.5, v["d"]);
        Assert.Equal(true, v["b"]);
        Assert.Null(v["n"]);
        Assert.Equal(new List<object?> { 1L, "x" }, v["a"]);
        Assert.Equal("v", ((Dictionary<string, object?>)v["o"]!)["k"]);
        Assert.Equal("""{"t":"ü<>"}""", PlainJson.Serialize(new Dictionary<string, object?> { ["t"] = "ü<>" }));
    }

    // ── node registry ───────────────────────────────────────────────────────

    [Fact(DisplayName = "a2a_call refuses a missing agent, an unknown agent, no text source and a bad channel")]
    public async Task NodeRegistryRefusals()
    {
        var agent = await Connect(new Scripted(_ => Rpc(Task_("t", "completed"))));
        var registry = A2aNodes.Registry(new Dictionary<string, A2aAgent> { ["helper"] = agent });

        GraphException Build(Dictionary<string, object?> config) => Assert.Throws<GraphException>(() => registry["a2a_call"](config));
        Assert.Contains("config.agent", Build(new()).Message);
        Assert.Contains("\"ghost\"", Build(new() { ["agent"] = "ghost" }).Message);
        Assert.Contains("config.text or config.textFrom", Build(new() { ["agent"] = "helper" }).Message);
        Assert.Contains("config.to", Build(new() { ["agent"] = "helper", ["text"] = "x", ["to"] = "" }).Message);
    }
}
