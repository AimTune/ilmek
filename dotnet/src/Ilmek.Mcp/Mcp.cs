namespace Ilmek.Mcp;

/// <summary>One tool a server advertises (<c>tools/list</c>). <see cref="InputSchema"/> is JSON Schema as a plain dictionary.</summary>
public sealed record McpToolInfo
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, object?> InputSchema { get; init; } = new Dictionary<string, object?> { ["type"] = "object" };
}

/// <summary>
/// One block of a tool result's <c>content</c>, as a plain dictionary
/// (<c>type</c>, <c>text</c>, <c>data</c>, <c>mimeType</c>, <c>resource</c>, …) so
/// anything a server sends passes through and canonicalizes like the TypeScript side.
/// </summary>
public sealed record McpCallToolResult
{
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Content { get; init; } = [];
    public IReadOnlyDictionary<string, object?>? StructuredContent { get; init; }
    public bool IsError { get; init; }
}

public sealed record McpResourceInfo
{
    public required string Uri { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? MimeType { get; init; }
}

public sealed record McpResourceContents
{
    public required string Uri { get; init; }
    public string? MimeType { get; init; }
    public string? Text { get; init; }
    public string? Blob { get; init; }
}

public sealed record McpPromptArgument
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
}

public sealed record McpPromptInfo
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<McpPromptArgument> Arguments { get; init; } = [];
}

/// <summary>One prompt message: a role and its content blocks (each a plain dictionary with <c>type</c>, <c>text</c>, …).</summary>
public sealed record McpPromptMessage
{
    public required string Role { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Content { get; init; } = [];
}

public sealed record McpGetPromptResult
{
    public string? Description { get; init; }
    public IReadOnlyList<McpPromptMessage> Messages { get; init; } = [];
}

/// <summary>
/// A connected MCP client, as <c>Ilmek.Mcp</c> reads it — the six operations of the
/// protocol this package uses. Implement it over the official
/// <c>ModelContextProtocol</c> client in a few lines (see the docs), or over a
/// fake in tests. Resources and prompts default to "none": a server that does not
/// advertise them has nothing to list.
/// </summary>
public interface IMcpClient
{
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default);

    Task<McpCallToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default);

    Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

    Task<IReadOnlyList<McpResourceContents>> ReadResourceAsync(string uri, CancellationToken ct = default) =>
        throw new NotSupportedException("this MCP client has no resources");

    Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

    Task<McpGetPromptResult> GetPromptAsync(string name, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default) =>
        throw new NotSupportedException("this MCP client has no prompts");
}

/// <summary>
/// A tool result as a node wants it: the text a model can read, the structured
/// payload when the server gave one, the error flag, and the raw content for
/// anything else. Plain data — it goes in the journal. Mirror of TypeScript's
/// <c>McpToolResult</c>.
/// </summary>
public sealed record McpToolResult
{
    /// <summary>Every text block (and embedded text resource) joined with newlines; <c>""</c> when there is none.</summary>
    public required string Text { get; init; }

    /// <summary><c>structuredContent</c>, verbatim, when the server returned one.</summary>
    public IReadOnlyDictionary<string, object?>? Structured { get; init; }

    /// <summary>The server flagged the result as an error (the tool ran; it failed).</summary>
    public bool IsError { get; init; }

    /// <summary>The raw content blocks, for anything <see cref="Text"/> cannot carry.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Content { get; init; } = [];

    /// <summary>The TypeScript object shape: <c>{text, structured?, isError, content}</c>.</summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var d = new Dictionary<string, object?> { ["text"] = Text, ["isError"] = IsError, ["content"] = Content.Cast<object?>().ToList() };
        if (Structured is not null) d["structured"] = Structured;
        return d;
    }
}

/// <summary>Pure reductions over the wire shapes — mirror of TypeScript's <c>normalizeToolResult</c> / <c>promptText</c>.</summary>
public static class McpResults
{
    /// <summary>Normalize a raw <c>tools/call</c> result into <see cref="McpToolResult"/>.</summary>
    public static McpToolResult Normalize(McpCallToolResult raw)
    {
        var texts = new List<string>();
        foreach (var block in raw.Content)
        {
            var type = block.GetValueOrDefault("type") as string;
            if (type == "text" && block.GetValueOrDefault("text") is string t) texts.Add(t);
            else if (type == "resource" && block.GetValueOrDefault("resource") is IReadOnlyDictionary<string, object?> r && r.GetValueOrDefault("text") is string rt) texts.Add(rt);
        }
        return new McpToolResult
        {
            Text = string.Join("\n", texts),
            Structured = raw.StructuredContent,
            IsError = raw.IsError,
            Content = raw.Content,
        };
    }

    /// <summary>The text of a prompt's messages: each message's text blocks joined by newlines, messages joined by blank lines, empty ones skipped.</summary>
    public static string PromptText(McpGetPromptResult result)
    {
        var parts = new List<string>();
        foreach (var m in result.Messages)
        {
            var text = string.Join("\n", m.Content
                .Where(b => b.GetValueOrDefault("type") as string == "text" && b.GetValueOrDefault("text") is string)
                .Select(b => (string)b["text"]!));
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join("\n\n", parts);
    }
}
