using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Ilmek;
using Ilmek.Mcp;

namespace Ilmek.Mcp.Tests;

/// <summary>
/// The toolbox over a fake <see cref="IMcpClient"/>, journaled calls, prompts as
/// skills, and the <c>mcp_tool</c> / <c>mcp_resource</c> node types. The scripted
/// server in conformance/mcp is the cross-language fixture: this suite and the
/// TypeScript one must reduce it to the same JSON.
/// </summary>
public class McpTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "mcp");

    // ── a fake client scripted by conformance/mcp/server.json ─────────────────

    private sealed class FakeMcpClient : IMcpClient
    {
        private readonly JsonObject _script = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "server.json")))!.AsObject();
        public List<(string Name, IReadOnlyDictionary<string, object?> Arguments)> Calls { get; } = new();
        public int Reads;

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpToolInfo>>(_script["tools"]!.AsArray().Select(t => new McpToolInfo
            {
                Name = t!["name"]!.GetValue<string>(),
                Description = t["description"]?.GetValue<string>(),
                InputSchema = (IReadOnlyDictionary<string, object?>)Plain(t["inputSchema"])!,
            }).ToList());

        public Task<McpCallToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
        {
            Calls.Add((name, arguments));
            var r = _script["calls"]![name] ?? throw new KeyNotFoundException($"unknown tool {name}");
            return Task.FromResult(new McpCallToolResult
            {
                Content = Blocks(r["content"]),
                StructuredContent = Plain(r["structuredContent"]) as IReadOnlyDictionary<string, object?>,
                IsError = r["isError"]?.GetValue<bool>() ?? false,
            });
        }

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpResourceInfo>>(_script["resources"]!.AsArray().Select(r => new McpResourceInfo
            {
                Uri = r!["uri"]!.GetValue<string>(), Name = r["name"]?.GetValue<string>(), MimeType = r["mimeType"]?.GetValue<string>(),
            }).ToList());

        public Task<IReadOnlyList<McpResourceContents>> ReadResourceAsync(string uri, CancellationToken ct = default)
        {
            Reads++;
            var c = _script["resourceContents"]![uri] ?? throw new KeyNotFoundException($"unknown resource {uri}");
            return Task.FromResult<IReadOnlyList<McpResourceContents>>(c.AsArray().Select(x => new McpResourceContents
            {
                Uri = x!["uri"]!.GetValue<string>(), MimeType = x["mimeType"]?.GetValue<string>(), Text = x["text"]?.GetValue<string>(),
            }).ToList());
        }

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpPromptInfo>>(_script["prompts"]!.AsArray().Select(p => new McpPromptInfo
            {
                Name = p!["name"]!.GetValue<string>(),
                Description = p["description"]?.GetValue<string>(),
                Arguments = (p["arguments"]?.AsArray() ?? []).Select(a => new McpPromptArgument
                {
                    Name = a!["name"]!.GetValue<string>(), Description = a["description"]?.GetValue<string>(), Required = a["required"]?.GetValue<bool>() ?? false,
                }).ToList(),
            }).ToList());

        public Task<McpGetPromptResult> GetPromptAsync(string name, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default)
        {
            var r = _script["promptResults"]![name] ?? throw new KeyNotFoundException($"unknown prompt {name}");
            return Task.FromResult(new McpGetPromptResult
            {
                Messages = r["messages"]!.AsArray().Select(m => new McpPromptMessage
                {
                    Role = m!["role"]!.GetValue<string>(),
                    Content = m["content"] is JsonArray ? Blocks(m["content"]) : [(IReadOnlyDictionary<string, object?>)Plain(m["content"])!],
                }).ToList(),
            });
        }

        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Blocks(JsonNode? node) =>
            (node?.AsArray() ?? []).Select(b => (IReadOnlyDictionary<string, object?>)Plain(b)!).ToList();
    }

    /// <summary>JsonNode → the plain Dictionary/List/string/long/double/bool shape the library works on.</summary>
    private static object? Plain(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => Plain(kv.Value)) as Dictionary<string, object?>,
        JsonArray a => a.Select(Plain).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v => v.GetValue<double>(),
        _ => throw new InvalidOperationException(),
    };

    private static readonly JsonSerializerOptions CanonicalOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Canonical(object? value) => Sort(JsonSerializer.SerializeToNode(value, CanonicalOptions))?.ToJsonString(CanonicalOptions) ?? "null";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
        _ => node.DeepClone(),
    };

    private static Task<McpToolbox> Connect(FakeMcpClient? client = null, string? prefix = null, IReadOnlyList<string>? allow = null) =>
        McpToolbox.ConnectAsync(client ?? new FakeMcpClient(), new McpToolboxOptions { Name = "github", Prefix = prefix, Allow = allow });

    // ── conformance/mcp (shared with TypeScript) ──────────────────────────────

    [Fact(DisplayName = "the scripted server reduces to expected.json")]
    public async Task FixtureMatchesExpected()
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "expected.json")));
        var toolbox = await Connect();
        var calls = new Dictionary<string, object?>();
        foreach (var t in toolbox.Tools()) calls[t.Name] = (await toolbox.InvokeAsync(t.Name)).ToDictionary();
        var actual = new Dictionary<string, object?>
        {
            ["tools"] = toolbox.Tools().Select(t => (object?)t.ToDictionary()).ToList(),
            ["calls"] = calls,
            ["resource"] = await toolbox.FetchResourceAsync("file:///README.md"),
            ["skills"] = (await McpSkills.FromPromptsAsync(toolbox)).Select(s => (object?)s.ToDictionary()).ToList(),
        };
        Assert.Equal(Sort(expected)!.ToJsonString(CanonicalOptions), Canonical(actual));
    }

    // ── McpToolbox ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "connect lists once; tools are prefixed and keep their remote name")]
    public async Task PrefixedTools()
    {
        var toolbox = await Connect();
        Assert.Equal([("github__search", "search"), ("github__get_file", "get_file"), ("github__internal_admin", "internal_admin")],
            toolbox.Tools().Select(t => (t.Name, t.RemoteName)).ToArray());
        Assert.Equal("Search repositories.", toolbox.Tool("github__search")!.Description);
        Assert.Null(toolbox.Tool("search"));
    }

    [Fact(DisplayName = "prefix and allow options")]
    public async Task PrefixAndAllow()
    {
        var bare = await Connect(prefix: "", allow: ["search", "get_file"]);
        Assert.Equal(["search", "get_file"], bare.Tools().Select(t => t.Name).ToArray());
        var custom = await Connect(prefix: "gh_");
        Assert.Equal("gh_search", custom.Tools()[0].Name);
        await Assert.ThrowsAsync<ArgumentException>(() => McpToolbox.ConnectAsync(new FakeMcpClient(), new McpToolboxOptions { Name = "" }));
    }

    [Fact(DisplayName = "invoke dispatches to the remote name and normalizes the result")]
    public async Task InvokeNormalizes()
    {
        var client = new FakeMcpClient();
        var toolbox = await Connect(client);
        var result = await toolbox.InvokeAsync("github__search", new Dictionary<string, object?> { ["q"] = "ilmek" });
        Assert.Single(client.Calls);
        Assert.Equal("search", client.Calls[0].Name);
        Assert.Equal("ilmek", client.Calls[0].Arguments["q"]);
        Assert.Equal("3 results\nilmek, mekik, chativa", result.Text);
        Assert.Equal(3L, result.Structured!["count"]);
        Assert.False(result.IsError);
        Assert.Equal(2, result.Content.Count);

        var file = await toolbox.InvokeAsync("github__get_file");
        Assert.Equal("# README", file.Text);
        Assert.Null(file.Structured);
        Assert.True((await toolbox.InvokeAsync("github__internal_admin")).IsError);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => toolbox.InvokeAsync("github__nope"));
    }

    [Fact(DisplayName = "call is journaled: once across an interrupt/resume")]
    public async Task CallIsJournaled()
    {
        var client = new FakeMcpClient();
        var toolbox = await Connect(client);
        var g = Graph.Create("mcp")
            .Channel("log", Channels.Append())
            .Node("work", async (State _, IContext ctx) =>
            {
                var hits = await toolbox.CallAsync(ctx, "github__search", new Dictionary<string, object?> { ["q"] = "ilmek" });
                var file = await toolbox.CallAsync(ctx, "github__get_file", new Dictionary<string, object?> { ["path"] = "README.md" });
                var ok = await ctx.InterruptAsync<string>(new Dictionary<string, object?> { ["q"] = "proceed?" });
                return Update.Of("log", new List<object?> { hits.Text.Split('\n')[0], file.Text, ok });
            })
            .Edge(Graph.Start, "work")
            .Edge("work", Graph.End)
            .Compile();
        var opts = new RunOptions { ThreadId = "t-1", Checkpointer = new InMemoryCheckpointer() };
        var paused = await g.RunAsync(new Dictionary<string, object?>(), opts);
        Assert.Equal(RunStatus.Interrupted, paused.Status);
        var done = await g.ResumeAsync("yes", opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(["3 results", "# README", "yes"], done.State!.Get<IReadOnlyList<object?>>("log").Cast<string>().ToArray());
        Assert.Equal(["search", "get_file"], client.Calls.Select(c => c.Name).ToArray());
    }

    [Fact(DisplayName = "call survives a SQLite reload: the replayed McpToolResult is typed and identical")]
    public async Task CallSurvivesSqliteReload()
    {
        // A durable checkpointer hands the journal back as plain JSON data; the
        // replayed CallAsync must still produce an McpToolResult, not throw an
        // InvalidCastException, and must not call the server again.
        var dir = Directory.CreateTempSubdirectory("ilmek-mcp-").FullName;
        var path = Path.Combine(dir, "mcp.db");
        try
        {
            var client = new FakeMcpClient();
            var toolbox = await Connect(client);
            var seen = new List<(McpToolResult Hits, McpToolResult File)>();
            var g = Graph.Create("mcp")
                .Channel("log", Channels.Append())
                .Node("work", async (State _, IContext ctx) =>
                {
                    var hits = await toolbox.CallAsync(ctx, "github__search", new Dictionary<string, object?> { ["q"] = "ilmek" });
                    var file = await toolbox.CallAsync(ctx, "github__get_file", new Dictionary<string, object?> { ["path"] = "README.md" });
                    seen.Add((hits, file));
                    var ok = await ctx.InterruptAsync<string>(new Dictionary<string, object?> { ["q"] = "proceed?" });
                    return Update.Of("log", new List<object?> { hits.Text.Split('\n')[0], file.Text, ok });
                })
                .Edge(Graph.Start, "work")
                .Edge("work", Graph.End)
                .Compile();

            using (var first = Ilmek.Checkpointers.Sqlite.SqliteCheckpointer.Open(path))
            {
                var paused = await g.RunAsync(new Dictionary<string, object?>(), new RunOptions { ThreadId = "t-sqlite", Checkpointer = first });
                Assert.Equal(RunStatus.Interrupted, paused.Status);
            }

            using (var second = Ilmek.Checkpointers.Sqlite.SqliteCheckpointer.Open(path))
            {
                var done = await g.ResumeAsync("yes", new RunOptions { ThreadId = "t-sqlite", Checkpointer = second });
                Assert.Equal(RunStatus.Done, done.Status);
                Assert.Equal(["3 results", "# README", "yes"], done.State!.Get<IReadOnlyList<object?>>("log").Cast<string>().ToArray());
            }

            Assert.Equal(["search", "get_file"], client.Calls.Select(c => c.Name).ToArray()); // no second call
            Assert.Equal(2, seen.Count);
            Assert.Equal(Canonical(seen[0].Hits.ToDictionary()), Canonical(seen[1].Hits.ToDictionary()));
            Assert.Equal(Canonical(seen[0].File.ToDictionary()), Canonical(seen[1].File.ToDictionary()));
            Assert.Equal(3L, seen[1].Hits.Structured!["count"]); // plain CLR data, not JsonElement
            Assert.Equal("image", seen[1].File.Content[0]["type"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact(DisplayName = "resources and prompts: listed, read and fetched once")]
    public async Task ResourcesAndPrompts()
    {
        var client = new FakeMcpClient();
        var toolbox = await Connect(client);
        Assert.Equal(["file:///README.md"], (await toolbox.ResourcesAsync()).Select(r => r.Uri).ToArray());
        Assert.Equal(["Code Review", "summarize"], (await toolbox.PromptsAsync()).Select(p => p.Name).ToArray());
        Assert.Equal("# README\n\nHello.", await toolbox.FetchResourceAsync("file:///README.md"));
        Assert.Equal("Review the diff below.\n\nFocus on correctness first.", await toolbox.FetchPromptAsync("Code Review"));

        client.Reads = 0; // the direct fetch above is not the journaled path under test
        var g = Graph.Create("res")
            .Channel("out", Channels.LastWrite(""))
            .Node("read", async (State _, IContext ctx) =>
            {
                var text = await toolbox.ReadResourceAsync(ctx, "file:///README.md");
                var prompt = await toolbox.PromptAsync(ctx, "Code Review");
                await ctx.InterruptAsync<object?>();
                return Update.Of("out", $"{text}|{prompt.Length}");
            })
            .Edge(Graph.Start, "read")
            .Edge("read", Graph.End)
            .Compile();
        var opts = new RunOptions { ThreadId = "t-2", Checkpointer = new InMemoryCheckpointer() };
        await g.RunAsync(new Dictionary<string, object?>(), opts);
        var done = await g.ResumeAsync("go", opts);
        Assert.Equal("# README\n\nHello.|51", done.State!.Get<string>("out"));
        Assert.Equal(1, client.Reads);
    }

    private sealed class MinimalClient : IMcpClient
    {
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
        public Task<McpCallToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default) => Task.FromResult(new McpCallToolResult());
    }

    [Fact(DisplayName = "a client without resources or prompts reports none, and fetching throws")]
    public async Task MinimalClientDefaults()
    {
        var toolbox = await McpToolbox.ConnectAsync(new MinimalClient(), new McpToolboxOptions { Name = "min" });
        Assert.Empty(toolbox.Tools());
        Assert.Empty(await toolbox.ResourcesAsync());
        Assert.Empty(await toolbox.PromptsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => toolbox.FetchResourceAsync("x"));
        await Assert.ThrowsAsync<NotSupportedException>(() => toolbox.FetchPromptAsync("x"));
        Assert.Empty(await McpSkills.FromPromptsAsync(toolbox));
    }

    // ── normalization ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "Normalize joins text and embedded text resources, keeps structured and isError")]
    public void Normalize()
    {
        var r = McpResults.Normalize(new McpCallToolResult
        {
            Content =
            [
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = "a" },
                new Dictionary<string, object?> { ["type"] = "image", ["data"] = "x", ["mimeType"] = "image/png" },
                new Dictionary<string, object?> { ["type"] = "resource", ["resource"] = new Dictionary<string, object?> { ["uri"] = "u", ["text"] = "b" } },
                new Dictionary<string, object?> { ["type"] = "resource", ["resource"] = new Dictionary<string, object?> { ["uri"] = "v", ["blob"] = "..." } },
            ],
            StructuredContent = new Dictionary<string, object?> { ["k"] = 1L },
            IsError = true,
        });
        Assert.Equal("a\nb", r.Text);
        Assert.Equal(1L, r.Structured!["k"]);
        Assert.True(r.IsError);
        Assert.Equal(4, r.Content.Count);
        var empty = McpResults.Normalize(new McpCallToolResult());
        Assert.Equal("", empty.Text);
        Assert.False(empty.IsError);
    }

    [Theory]
    [InlineData("github-Code Review", "github-code-review")]
    [InlineData("  ***  ", "prompt")]
    public void ToSkillName(string raw, string expected) => Assert.Equal(expected, McpSkills.ToSkillName(raw));

    [Fact(DisplayName = "ToSkillName caps at 64 without a trailing hyphen")]
    public void ToSkillNameLength()
    {
        Assert.Equal(64, McpSkills.ToSkillName(new string('a', 70)).Length);
        Assert.Equal(new string('x', 63), McpSkills.ToSkillName(new string('x', 63) + "-y"));
    }

    // ── skills ────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "argument-less prompts are fetched; prompts with required arguments get the argument note")]
    public async Task PromptsAsSkills()
    {
        var skills = await McpSkills.FromPromptsAsync(await Connect());
        Assert.Equal(["github-code-review", "github-summarize"], skills.Select(s => s.Name).ToArray());
        Assert.Equal("Review the diff below.\n\nFocus on correctness first.", skills[0].Instructions);
        Assert.Equal("Code Review", skills[0].Metadata["prompt"]);
        Assert.Equal("The \"summarize\" prompt of MCP server \"github\".", skills[1].Description);
        Assert.EndsWith("- url (required): What to summarize\n- tone", skills[1].Instructions);
        Assert.Equal("url,tone", skills[1].Metadata["arguments"]);
        Assert.Equal("code-review", (await McpSkills.FromPromptsAsync(await Connect(), prefix: ""))[0].Name);
    }

    // ── node types ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "mcp_tool merges fixed and channel arguments, writes text or the whole result; mcp_resource reads text")]
    public async Task NodeTypes()
    {
        var client = new FakeMcpClient();
        var toolboxes = new Dictionary<string, McpToolbox> { ["github"] = await Connect(client) };
        var spec = new GraphSpec
        {
            Name = "mcp-graph",
            Channels = new Dictionary<string, SpecChannel> { ["query"] = new(), ["hits"] = new(), ["file"] = new(), ["readme"] = new() },
            Nodes =
            [
                new SpecNode("search", "mcp_tool", new Dictionary<string, object?>
                {
                    ["server"] = "github", ["tool"] = "github__search", ["to"] = "hits", ["text"] = true,
                    ["arguments"] = new Dictionary<string, object?> { ["q"] = "default", ["page"] = 1L }, ["argumentsFrom"] = "query",
                }),
                new SpecNode("file", "mcp_tool", new Dictionary<string, object?> { ["server"] = "github", ["tool"] = "github__get_file", ["to"] = "file" }),
                new SpecNode("readme", "mcp_resource", new Dictionary<string, object?> { ["server"] = "github", ["uri"] = "file:///README.md", ["to"] = "readme" }),
            ],
            Edges = [new SpecEdge(Graph.Start, "search"), new SpecEdge("search", "file"), new SpecEdge("file", "readme"), new SpecEdge("readme", Graph.End)],
        };
        var g = Spec.FromSpec(spec, McpNodes.Registry(toolboxes)).Compile();
        var result = await g.RunAsync(
            new Dictionary<string, object?> { ["query"] = new Dictionary<string, object?> { ["q"] = "ilmek" } },
            new RunOptions { ThreadId = "t-3" });
        Assert.Equal(RunStatus.Done, result.Status);
        Assert.Equal("3 results\nilmek, mekik, chativa", result.State!.Get<string>("hits"));
        Assert.Equal("ilmek", client.Calls[0].Arguments["q"]);
        Assert.Equal(1L, client.Calls[0].Arguments["page"]);
        Assert.Equal("# README", ((IReadOnlyDictionary<string, object?>)result.State!["file"]!)["text"]);
        Assert.Equal("# README\n\nHello.", result.State!.Get<string>("readme"));
    }

    [Fact(DisplayName = "bad references fail at build time")]
    public async Task BadReferences()
    {
        var registry = McpNodes.Registry(new Dictionary<string, McpToolbox> { ["github"] = await Connect() });
        GraphSpec Spec1(Dictionary<string, object?> config, string type = "mcp_tool") => new()
        {
            Name = "bad",
            Channels = new Dictionary<string, SpecChannel> { ["result"] = new() },
            Nodes = [new SpecNode("n", type, config)],
            Edges = [new SpecEdge(Graph.Start, "n"), new SpecEdge("n", Graph.End)],
        };
        Assert.Contains("known: [\"github\"]", Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(new() { ["server"] = "gitlab", ["tool"] = "x" }), registry)).Message);
        Assert.Contains("references tool \"search\"", Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(new() { ["server"] = "github", ["tool"] = "search" }), registry)).Message);
        Assert.Contains("config.server", Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(new() { ["tool"] = "github__search" }), registry)).Message);
        Assert.Contains("config.uri", Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(new() { ["server"] = "github" }, "mcp_resource"), registry)).Message);
        Assert.Contains("config.to", Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(new() { ["server"] = "github", ["tool"] = "github__search", ["to"] = 5L }), registry)).Message);
    }
}
