namespace Ilmek.Mcp;

/// <summary>A tool as the toolbox exposes it: the prefixed name a model calls, plus the server's own name.</summary>
public sealed record McpTool
{
    public required string Name { get; init; }
    /// <summary>The server's own name for the tool (what <c>tools/call</c> receives).</summary>
    public required string RemoteName { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyDictionary<string, object?> InputSchema { get; init; }

    /// <summary>The TypeScript object shape: <c>{name, remoteName, description?, inputSchema}</c>.</summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var d = new Dictionary<string, object?> { ["name"] = Name, ["remoteName"] = RemoteName, ["inputSchema"] = InputSchema };
        if (Description is not null) d["description"] = Description;
        return d;
    }
}

public sealed record McpToolboxOptions
{
    /// <summary>The server's name — the journal-key namespace and the default tool prefix.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Prepended to every advertised tool name. Default <c>"&lt;name&gt;__"</c>, so server
    /// <c>github</c>'s <c>search</c> is exposed as <c>github__search</c>. <c>""</c> exposes raw names.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>Keep only these advertised tool names (before prefixing). Default: all.</summary>
    public IReadOnlyList<string>? Allow { get; init; }
}

/// <summary>
/// A connected MCP server's tools, resources and prompts, as a graph consumes
/// them. Mirror of <c>@ilmek/mcp</c>' <c>McpToolbox</c>.
/// <code>
/// var github = await McpToolbox.ConnectAsync(client, new() { Name = "github" });
/// var hits = await github.CallAsync(ctx, "github__search", new() { ["q"] = "ilmek" });   // journaled
/// </code>
/// <para><see cref="ConnectAsync"/> lists the tools once; <see cref="Tools"/> is that
/// snapshot. <see cref="CallAsync"/> runs inside <c>ctx.StepAsync</c> — on the replay pass
/// after an interrupt the recorded result is returned and the server is <b>not</b>
/// called again. <see cref="InvokeAsync"/> is the raw, unjournaled call, for a host
/// that journals on its own.</para>
/// </summary>
public sealed class McpToolbox
{
    public string Name { get; }
    public string Prefix { get; }
    private readonly IMcpClient _client;
    private readonly Dictionary<string, McpTool> _byName = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    private McpToolbox(IMcpClient client, McpToolboxOptions options, IReadOnlyList<McpToolInfo> tools)
    {
        _client = client;
        Name = options.Name;
        Prefix = options.Prefix ?? $"{options.Name}__";
        var allow = options.Allow is null ? null : new HashSet<string>(options.Allow, StringComparer.Ordinal);
        // A sloppy server may answer tools/list without a list, or with holes in it.
        foreach (var t in tools ?? [])
        {
            if (t is null) continue;
            if (allow is not null && !allow.Contains(t.Name)) continue;
            var exposed = Prefix + t.Name;
            _byName[exposed] = new McpTool { Name = exposed, RemoteName = t.Name, Description = t.Description, InputSchema = t.InputSchema };
            _order.Add(exposed);
        }
    }

    /// <summary>List the server's tools once and wrap them.</summary>
    public static async Task<McpToolbox> ConnectAsync(IMcpClient client, McpToolboxOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(options.Name)) throw new ArgumentException("an MCP toolbox needs a server name", nameof(options));
        return new McpToolbox(client, options, await client.ListToolsAsync(ct).ConfigureAwait(false));
    }

    /// <summary>The exposed tools, in the server's order — ready for a model's tool list.</summary>
    public IReadOnlyList<McpTool> Tools() => _order.Select(n => _byName[n]).ToList();

    /// <summary>One exposed tool by its prefixed name.</summary>
    public McpTool? Tool(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Raw, <b>unjournaled</b> call by exposed name. Prefer <see cref="CallAsync"/> inside a node.</summary>
    public async Task<McpToolResult> InvokeAsync(string name, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken ct = default)
    {
        var tool = _byName.GetValueOrDefault(name)
            ?? throw new KeyNotFoundException($"MCP toolbox \"{Name}\" has no tool \"{name}\"");
        var raw = await _client.CallToolAsync(tool.RemoteName, arguments ?? new Dictionary<string, object?>(), ct).ConfigureAwait(false);
        return McpResults.Normalize(raw);
    }

    /// <summary>
    /// Call a tool <b>once</b> across any number of resumes: the invocation runs in
    /// <c>ctx.StepAsync</c>, keyed <c>mcp:&lt;server&gt;:&lt;tool&gt;</c> unless
    /// <paramref name="key"/> says otherwise, and the normalized result is what the journal keeps.
    /// </summary>
    public ValueTask<McpToolResult> CallAsync(IContext ctx, string name, IReadOnlyDictionary<string, object?>? arguments = null, string? key = null)
    {
        // An unknown tool faults the returned task rather than throwing before
        // there is one, so `await` and `.AsTask()` callers see it the same way.
        if (_byName.GetValueOrDefault(name) is not { } tool)
            return ValueTask.FromException<McpToolResult>(
                new KeyNotFoundException($"MCP toolbox \"{Name}\" has no tool \"{name}\""));
        return ctx.StepAsync(key ?? $"mcp:{Name}:{tool.RemoteName}", async () =>
            await InvokeAsync(name, arguments, ctx.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>The server's resources, or empty when it advertises none.</summary>
    public Task<IReadOnlyList<McpResourceInfo>> ResourcesAsync(CancellationToken ct = default) => _client.ListResourcesAsync(ct);

    /// <summary>Read a resource's text <b>once</b> across resumes (key <c>mcp:&lt;server&gt;:resource:&lt;uri&gt;</c>). Non-text contents yield <c>""</c>.</summary>
    public ValueTask<string> ReadResourceAsync(IContext ctx, string uri, string? key = null) =>
        ctx.StepAsync(key ?? $"mcp:{Name}:resource:{uri}", async () => await FetchResourceAsync(uri, ctx.CancellationToken).ConfigureAwait(false));

    /// <summary>Raw, unjournaled resource read.</summary>
    public async Task<string> FetchResourceAsync(string uri, CancellationToken ct = default)
    {
        var contents = await _client.ReadResourceAsync(uri, ct).ConfigureAwait(false);
        return string.Join("\n", (contents ?? []).Select(c => c?.Text ?? "").Where(t => t.Length > 0));
    }

    /// <summary>The server's prompts, or empty when it advertises none.</summary>
    public Task<IReadOnlyList<McpPromptInfo>> PromptsAsync(CancellationToken ct = default) => _client.ListPromptsAsync(ct);

    /// <summary>Fetch a prompt's text <b>once</b> across resumes (key <c>mcp:&lt;server&gt;:prompt:&lt;name&gt;</c>).</summary>
    public ValueTask<string> PromptAsync(IContext ctx, string name, IReadOnlyDictionary<string, string>? arguments = null, string? key = null) =>
        ctx.StepAsync(key ?? $"mcp:{Name}:prompt:{name}", async () => await FetchPromptAsync(name, arguments, ctx.CancellationToken).ConfigureAwait(false));

    /// <summary>Raw, unjournaled prompt fetch, rendered to text (see <see cref="McpResults.PromptText"/>).</summary>
    public async Task<string> FetchPromptAsync(string name, IReadOnlyDictionary<string, string>? arguments = null, CancellationToken ct = default) =>
        McpResults.PromptText(await _client.GetPromptAsync(name, arguments ?? new Dictionary<string, string>(), ct).ConfigureAwait(false));
}
