namespace Ilmek.Mcp;

/// <summary>
/// MCP as graph data (MODEL.md §9): node types a stored spec can name. The spec
/// carries the server and tool names — data — and the registry resolves them
/// against connected toolboxes at build time. Mirror of <c>@ilmek/mcp</c>' <c>mcpNodes</c>.
/// </summary>
public static class McpNodes
{
    /// <summary>The default channel the MCP node types write to.</summary>
    public const string DefaultChannel = "result";

    /// <summary>
    /// A node registry with two types, over toolboxes keyed by server name:
    /// <list type="bullet">
    ///   <item><c>mcp_tool</c> — config <c>{ server, tool, arguments?, argumentsFrom?, to?, text? }</c>.
    ///   Calls <c>tool</c> (the <b>exposed</b> name) on <c>server</c> with <c>arguments</c> merged
    ///   under the object found in channel <c>argumentsFrom</c>, journaled, and writes the result
    ///   to channel <c>to</c> (default <c>"result"</c>): the whole result as a dictionary, or just
    ///   its text when <c>text: true</c>. A tool the server did not advertise fails at build time.</item>
    ///   <item><c>mcp_resource</c> — config <c>{ server, uri, to? }</c>. Reads the resource's text,
    ///   journaled, into channel <c>to</c>.</item>
    /// </list>
    /// </summary>
    public static Dictionary<string, NodeBuilder> Registry(IReadOnlyDictionary<string, McpToolbox> toolboxes)
    {
        McpToolbox Toolbox(IReadOnlyDictionary<string, object?> config, string type)
        {
            if (config.GetValueOrDefault("server") is not string server || server.Length == 0)
                throw new GraphException($"an \"{type}\" node needs config.server");
            return toolboxes.GetValueOrDefault(server)
                ?? throw new GraphException($"\"{type}\" node references MCP server \"{server}\"; known: [{string.Join(", ", toolboxes.Keys.Select(k => $"\"{k}\""))}]");
        }

        return new Dictionary<string, NodeBuilder>
        {
            ["mcp_tool"] = config =>
            {
                var tb = Toolbox(config, "mcp_tool");
                if (config.GetValueOrDefault("tool") is not string name || tb.Tool(name) is null)
                    throw new GraphException(
                        $"\"mcp_tool\" node references tool \"{config.GetValueOrDefault("tool")}\" on \"{tb.Name}\"; " +
                        $"known: [{string.Join(", ", tb.Tools().Select(t => $"\"{t.Name}\""))}]");
                var to = ChannelOf(config);
                var fixedArgs = config.GetValueOrDefault("arguments") as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();
                var from = config.GetValueOrDefault("argumentsFrom") as string;
                var textOnly = config.GetValueOrDefault("text") is true;
                return async (state, ctx) =>
                {
                    var args = new Dictionary<string, object?>(fixedArgs);
                    if (from is not null && state[from] is IReadOnlyDictionary<string, object?> dynamic)
                        foreach (var (k, v) in dynamic) args[k] = v;
                    var result = await tb.CallAsync(ctx, name, args).ConfigureAwait(false);
                    return new Dictionary<string, object?> { [to] = textOnly ? result.Text : result.ToDictionary() };
                };
            },
            ["mcp_resource"] = config =>
            {
                var tb = Toolbox(config, "mcp_resource");
                if (config.GetValueOrDefault("uri") is not string uri || uri.Length == 0)
                    throw new GraphException("an \"mcp_resource\" node needs config.uri");
                var to = ChannelOf(config);
                return async (_, ctx) => new Dictionary<string, object?> { [to] = await tb.ReadResourceAsync(ctx, uri).ConfigureAwait(false) };
            },
        };
    }

    private static string ChannelOf(IReadOnlyDictionary<string, object?> config)
    {
        if (!config.TryGetValue("to", out var to) || to is null) return DefaultChannel;
        if (to is not string s || s.Length == 0) throw new GraphException("config.to must be a channel name");
        return s;
    }
}
