using Ilmek;
using Ilmek.Mcp;

namespace Ilmek.Mcp.Tests;

/// <summary>
/// What a sloppy or failing MCP server (or client adapter) can hand the toolbox:
/// missing lists, null entries, error results, thrown exceptions, cancellation.
/// None of it may surface as a NullReferenceException, and the journal must keep
/// exactly what it should. Mirrors the TS error-path suite.
/// </summary>
public class McpErrorPathTests
{
    /// <summary>A client whose every answer is scripted by the test.</summary>
    private sealed class StubClient : IMcpClient
    {
        public Func<IReadOnlyList<McpToolInfo>> Tools { get; init; } = () => [new McpToolInfo { Name = "echo" }];
        public Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<McpCallToolResult>> Call { get; init; } =
            (_, _, _) => Task.FromResult(new McpCallToolResult { Content = [Text("ok")] });
        public Func<string, IReadOnlyList<McpResourceContents>> Read { get; init; } = _ => [];
        public Func<McpGetPromptResult> Prompt { get; init; } = () => new McpGetPromptResult();
        public int Calls;

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default) => Task.FromResult(Tools());

        public Task<McpCallToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Call(name, arguments, ct);
        }

        public Task<IReadOnlyList<McpResourceContents>> ReadResourceAsync(string uri, CancellationToken ct = default) =>
            Task.FromResult(Read(uri));

        public Task<McpGetPromptResult> GetPromptAsync(string name, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default) =>
            Task.FromResult(Prompt());
    }

    private static IReadOnlyDictionary<string, object?> Text(string t) => new Dictionary<string, object?> { ["type"] = "text", ["text"] = t };

    private static Task<McpToolbox> Connect(StubClient client) =>
        McpToolbox.ConnectAsync(client, new McpToolboxOptions { Name = "srv" });

    // ── tools/list ──────────────────────────────────────────────────────────

    [Fact(DisplayName = "tools/list answering null gives a toolbox with no tools, not a crash")]
    public async Task NullToolList()
    {
        var tb = await Connect(new StubClient { Tools = () => null! });
        Assert.Empty(tb.Tools());
    }

    [Fact(DisplayName = "null entries in tools/list are skipped")]
    public async Task NullToolEntries()
    {
        var tb = await Connect(new StubClient { Tools = () => [null!, new McpToolInfo { Name = "a" }, null!] });
        Assert.Equal(new[] { "srv__a" }, tb.Tools().Select(t => t.Name));
    }

    [Fact(DisplayName = "a server name is required")]
    public async Task ServerNameRequired()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => McpToolbox.ConnectAsync(new StubClient(), new McpToolboxOptions { Name = "" }));
    }

    [Fact(DisplayName = "a tools/list failure propagates out of ConnectAsync")]
    public async Task ListFailurePropagates()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Connect(new StubClient { Tools = () => throw new InvalidOperationException("server down") }));
        Assert.Equal("server down", ex.Message);
    }

    // ── tools/call ──────────────────────────────────────────────────────────

    [Fact(DisplayName = "an unknown tool fails the returned task — CallAsync never throws synchronously")]
    public async Task UnknownToolIsAFaultedTask()
    {
        var tb = await Connect(new StubClient());
        var ctx = new FakeContext();

        ValueTask<McpToolResult> pending = default;
        var ex0 = Record.Exception(() => pending = tb.CallAsync(ctx, "srv__nope")); // must not throw here
        Assert.Null(ex0);
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(async () => await pending);
        Assert.Contains("\"srv__nope\"", ex.Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => tb.InvokeAsync("echo")); // the remote name is not exposed
    }

    [Fact(DisplayName = "null content, and null entries inside content, normalize to nothing")]
    public async Task NullContentEntries()
    {
        var tb = await Connect(new StubClient
        {
            Call = (_, _, _) => Task.FromResult(new McpCallToolResult { Content = [null!, Text("a"), null!, Text("b")] }),
        });
        var r = await tb.InvokeAsync("srv__echo");
        Assert.Equal("a\nb", r.Text);
        Assert.Equal(2, r.Content.Count);

        var empty = await (await Connect(new StubClient
        {
            Call = (_, _, _) => Task.FromResult(new McpCallToolResult { Content = null! }),
        })).InvokeAsync("srv__echo");
        Assert.Equal("", empty.Text);
        Assert.Empty(empty.Content);
    }

    [Fact(DisplayName = "non-text blocks, non-string text, and embedded text resources normalize as in TS")]
    public void NormalizeShapes()
    {
        var r = McpResults.Normalize(new McpCallToolResult
        {
            Content =
            [
                new Dictionary<string, object?> { ["type"] = "image", ["data"] = "AAAA", ["mimeType"] = "image/png" },
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = 42L },
                new Dictionary<string, object?> { ["type"] = "resource", ["resource"] = new Dictionary<string, object?> { ["text"] = "embedded" } },
                new Dictionary<string, object?> { ["text"] = "no type" },
                Text("plain"),
            ],
            StructuredContent = new Dictionary<string, object?> { ["n"] = 1L },
            IsError = true,
        });
        Assert.Equal("embedded\nplain", r.Text);
        Assert.True(r.IsError);
        Assert.Equal(1L, r.Structured!["n"]);
        Assert.Equal(5, r.Content.Count);
        var d = r.ToDictionary();
        Assert.True((bool)d["isError"]!);
        Assert.True(d.ContainsKey("structured"));
    }

    [Fact(DisplayName = "a server-side tool error (isError) is journaled like any result: not called again on replay")]
    public async Task ToolErrorIsJournaled()
    {
        var client = new StubClient
        {
            Call = (_, _, _) => Task.FromResult(new McpCallToolResult { Content = [Text("rate limited")], IsError = true }),
        };
        var tb = await Connect(client);
        var g = Graph.Create("tool-error")
            .Channel("log", Channels.Append())
            .Node("n", async (_, ctx) =>
            {
                var r = await tb.CallAsync(ctx, "srv__echo");
                var ok = await ctx.InterruptAsync<string>();
                return Update.Of("log", $"{r.IsError}:{r.Text}:{ok}");
            })
            .Edge(Graph.Start, "n")
            .Compile();
        var opts = new RunOptions { ThreadId = "t", Checkpointer = new InMemoryCheckpointer() };

        await g.RunAsync(null, opts);
        var done = await g.ResumeAsync("go", opts);

        Assert.Equal(new[] { "True:rate limited:go" }, done.State!.GetList<string>("log"));
        Assert.Equal(1, client.Calls);
    }

    [Fact(DisplayName = "a thrown client exception is not journaled: a retry calls the server again")]
    public async Task ThrownExceptionIsRetried()
    {
        var attempts = 0;
        var failFirst = new StubClient
        {
            Call = (_, _, _) => Interlocked.Increment(ref attempts) == 1
                ? throw new HttpRequestException("connection reset")
                : Task.FromResult(new McpCallToolResult { Content = [Text("fine")] }),
        };
        var tb = await Connect(failFirst);
        var g = Graph.Create("retry")
            .Channel("log", Channels.Append())
            .Node("n", async (_, ctx) => Update.Of("log", (await tb.CallAsync(ctx, "srv__echo")).Text),
                retry: new RetryPolicy { MaxAttempts = 2 })
            .Edge(Graph.Start, "n")
            .Compile();

        var result = await g.RunAsync(null, new RunOptions { ThreadId = "t", Checkpointer = new InMemoryCheckpointer() });
        Assert.Equal(RunStatus.Done, result.Status);
        Assert.Equal(new[] { "fine" }, result.State!.GetList<string>("log"));
        Assert.Equal(2, failFirst.Calls);
        Assert.Single(result.Events.OfType<NodeRetryEvent>());
    }

    [Fact(DisplayName = "the run's cancellation token reaches the client call")]
    public async Task CancellationReachesClient()
    {
        CancellationToken seen = default;
        var tb = await Connect(new StubClient
        {
            Call = (_, _, ct) => { seen = ct; return Task.FromResult(new McpCallToolResult()); },
        });
        using var cts = new CancellationTokenSource();
        var g = Graph.Create().Node("n", async (_, ctx) => { await tb.CallAsync(ctx, "srv__echo"); return null; })
            .Edge(Graph.Start, "n").Compile();
        await g.RunAsync(null, new RunOptions { CancellationToken = cts.Token });

        Assert.True(seen.CanBeCanceled);
        cts.Cancel();
        Assert.True(seen.IsCancellationRequested);
    }

    [Fact(DisplayName = "a client honouring cancellation ends the node in error with OperationCanceledException")]
    public async Task CancelledCall()
    {
        var tb = await Connect(new StubClient
        {
            Call = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return new McpCallToolResult(); },
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var g = Graph.Create().Node("n", async (_, ctx) => { await tb.CallAsync(ctx, "srv__echo"); return null; })
            .Edge(Graph.Start, "n").Compile();

        var result = await g.RunAsync(null, new RunOptions { CancellationToken = cts.Token });
        Assert.Equal(RunStatus.Error, result.Status);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Errors.Single().Error);
    }

    // ── resources and prompts ───────────────────────────────────────────────

    [Fact(DisplayName = "resources/read with null contents, null items or non-text items reads as text of the string items only")]
    public async Task ResourceReadShapes()
    {
        Assert.Equal("", await (await Connect(new StubClient { Read = _ => null! })).FetchResourceAsync("x://a"));

        var tb = await Connect(new StubClient
        {
            Read = _ =>
            [
                null!,
                new McpResourceContents { Uri = "x://a", Text = "one" },
                new McpResourceContents { Uri = "x://a", Blob = "AAAA" },
                new McpResourceContents { Uri = "x://a", Text = "two" },
            ],
        });
        Assert.Equal("one\ntwo", await tb.FetchResourceAsync("x://a"));
    }

    [Fact(DisplayName = "a client without resources or prompts reports none, and reading one is NotSupported")]
    public async Task DefaultInterfaceMembers()
    {
        IMcpClient bare = new BareClient();
        var tb = await McpToolbox.ConnectAsync(bare, new McpToolboxOptions { Name = "bare" });
        Assert.Empty(await tb.ResourcesAsync());
        Assert.Empty(await tb.PromptsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => tb.FetchResourceAsync("x://a"));
        await Assert.ThrowsAsync<NotSupportedException>(() => tb.FetchPromptAsync("p"));
        Assert.Empty(await McpSkills.FromPromptsAsync(tb));
    }

    private sealed class BareClient : IMcpClient
    {
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpToolInfo>>([]);

        public Task<McpCallToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default) =>
            Task.FromResult(new McpCallToolResult());
    }

    [Fact(DisplayName = "prompts/get with null messages, null messages, null content or null blocks renders those as nothing")]
    public void PromptTextShapes()
    {
        Assert.Equal("", McpResults.PromptText(new McpGetPromptResult { Messages = null! }));
        var text = McpResults.PromptText(new McpGetPromptResult
        {
            Messages =
            [
                null!,
                new McpPromptMessage { Role = "user", Content = null! },
                new McpPromptMessage { Role = "user", Content = [null!, Text("first"), new Dictionary<string, object?> { ["type"] = "image" }] },
                new McpPromptMessage { Role = "assistant", Content = [] },
                new McpPromptMessage { Role = "user", Content = [Text("second"), Text("line")] },
            ],
        });
        Assert.Equal("first\n\nsecond\nline", text);
    }

    [Theory(DisplayName = "ToSkillName coerces anything to a valid skill name")]
    [InlineData("GitHub Review!", "github-review")]
    [InlineData("---", "prompt")]
    [InlineData("", "prompt")]
    [InlineData("a__b..c", "a-b-c")]
    [InlineData("çok güzel", "ok-g-zel")]
    public void ToSkillName(string raw, string expected) => Assert.Equal(expected, McpSkills.ToSkillName(raw));

    [Fact(DisplayName = "ToSkillName caps at 64 characters without a trailing hyphen")]
    public void ToSkillNameCap()
    {
        var name = McpSkills.ToSkillName(new string('a', 63) + "-bbbb");
        Assert.True(name.Length <= 64);
        Assert.False(name.EndsWith('-'));
    }

    // ── node registry ───────────────────────────────────────────────────────

    [Fact(DisplayName = "mcp node types refuse a missing server, an unknown server, an unknown tool, a missing uri and a bad channel")]
    public async Task NodeRegistryRefusals()
    {
        var tb = await Connect(new StubClient());
        var registry = McpNodes.Registry(new Dictionary<string, McpToolbox> { ["srv"] = tb });

        GraphException Build(string type, Dictionary<string, object?> config) =>
            Assert.Throws<GraphException>(() => registry[type](config));

        Assert.Contains("config.server", Build("mcp_tool", new()).Message);
        Assert.Contains("\"ghost\"", Build("mcp_tool", new() { ["server"] = "ghost" }).Message);
        Assert.Contains("known: [\"srv__echo\"]", Build("mcp_tool", new() { ["server"] = "srv", ["tool"] = "echo" }).Message);
        Assert.Contains("config.uri", Build("mcp_resource", new() { ["server"] = "srv" }).Message);
        Assert.Contains("config.to", Build("mcp_tool", new() { ["server"] = "srv", ["tool"] = "srv__echo", ["to"] = 5L }).Message);
    }

    /// <summary>A minimal IContext for calling the toolbox outside a run.</summary>
    private sealed class FakeContext : IContext
    {
        public CompiledGraph Graph => null!;
        public State State => new(new Dictionary<string, object?>());
        public string ThreadId => "t";
        public string RunId => "r";
        public string Node => "n";
        public string TaskId => "t:root:n";
        public int StepIndex => 0;
        public int RecursionLimit => 25;
        public int RemainingSteps => 25;
        public IReadOnlyDictionary<string, object?> Meta => new Dictionary<string, object?>();
        public IReadOnlyList<KeyValuePair<string, JournalEntry>> Journal => [];
        public CancellationToken CancellationToken => default;
        public async ValueTask<T> StepAsync<T>(string key, Func<ValueTask<T>> fn) => await fn();
        public ValueTask<T> StepAsync<T>(string key, Func<T> fn) => new(fn());
        public ValueTask<T> InterruptAsync<T>(object? payload = null, string key = "interrupt") => throw new NotSupportedException();
        public void Emit(object? payload) { }
        public void EmitToken(string text, IReadOnlyDictionary<string, object?>? meta = null) { }
    }
}
