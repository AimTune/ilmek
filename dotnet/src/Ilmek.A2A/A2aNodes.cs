namespace Ilmek.A2A;

/// <summary>
/// A2A as graph data (MODEL.md §9): a node type a stored spec can name. Mirror of
/// <c>@ilmek/a2a</c>' <c>a2aNodes</c>.
/// </summary>
public static class A2aNodes
{
    /// <summary>The default channel the <c>a2a_call</c> node writes to.</summary>
    public const string DefaultChannel = "result";

    /// <summary>
    /// A node registry with one type, over agents keyed by name:
    /// <c>a2a_call</c> — config <c>{ agent, text?, textFrom?, contextFrom?, taskFrom?, to?, textOnly? }</c>.
    /// Sends <c>text</c> (or the string in channel <c>textFrom</c>) to <c>agent</c>, continuing the
    /// remote conversation in channel <c>contextFrom</c> and answering the task in channel
    /// <c>taskFrom</c> when those are set, journaled; writes the result to channel <c>to</c>
    /// (default <c>"result"</c>): the whole result as a dictionary, or just its text when
    /// <c>textOnly: true</c>. An unknown agent fails at build time.
    /// </summary>
    public static Dictionary<string, NodeBuilder> Registry(IReadOnlyDictionary<string, A2aAgent> agents) => new()
    {
        ["a2a_call"] = config =>
        {
            if (config.GetValueOrDefault("agent") is not string name || name.Length == 0)
                throw new GraphException("an \"a2a_call\" node needs config.agent");
            var agent = agents.GetValueOrDefault(name)
                ?? throw new GraphException($"\"a2a_call\" node references A2A agent \"{name}\"; known: [{string.Join(", ", agents.Keys.Select(k => $"\"{k}\""))}]");
            var fixedText = config.GetValueOrDefault("text") as string;
            var textFrom = config.GetValueOrDefault("textFrom") as string;
            if (fixedText is null && textFrom is null) throw new GraphException("an \"a2a_call\" node needs config.text or config.textFrom");
            var contextFrom = config.GetValueOrDefault("contextFrom") as string;
            var taskFrom = config.GetValueOrDefault("taskFrom") as string;
            var to = ChannelOf(config);
            var textOnly = config.GetValueOrDefault("text_only") is true || config.GetValueOrDefault("textOnly") is true;
            return async (state, ctx) =>
            {
                var text = textFrom is not null ? state[textFrom]?.ToString() ?? "" : fixedText!;
                var options = new SendOptions
                {
                    ContextId = contextFrom is not null ? state[contextFrom] as string : null,
                    TaskId = taskFrom is not null ? state[taskFrom] as string : null,
                };
                var result = await agent.SendAsync(ctx, text, options).ConfigureAwait(false);
                return new Dictionary<string, object?> { [to] = textOnly ? result.Text : result.ToDictionary() };
            };
        },
    };

    private static string ChannelOf(IReadOnlyDictionary<string, object?> config)
    {
        if (!config.TryGetValue("to", out var to) || to is null) return DefaultChannel;
        if (to is not string s || s.Length == 0) throw new GraphException("config.to must be a channel name");
        return s;
    }
}
