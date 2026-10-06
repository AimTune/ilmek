namespace Ilmek.Skills;

/// <summary>
/// Skills as graph data (MODEL.md §9): two node types a stored spec can name. A
/// spec never carries executable text, so a skill is referenced by name and
/// resolved through the registry at build time — exactly how every other node
/// type works. Mirror of <c>@ilmek/skills</c>' <c>skillNodes</c>.
/// </summary>
public static class SkillNodes
{
    /// <summary>The default channel the skill node types write to.</summary>
    public const string DefaultChannel = "instructions";

    /// <summary>
    /// A node registry with two types:
    /// <list type="bullet">
    ///   <item><c>skill</c> — config <c>{ skill, to? }</c>. Writes the named skill's
    ///   instructions to channel <c>to</c> (default <c>"instructions"</c>), so a downstream
    ///   node — an LLM call — reads them from state. An unknown skill name fails at build
    ///   time, not mid-run.</item>
    ///   <item><c>skills</c> — config <c>{ to?, intro? }</c>. Writes the level-1 catalog
    ///   prompt (see <see cref="SkillPrompt.Render"/>) to channel <c>to</c>.</item>
    /// </list>
    /// </summary>
    /// <example><code>
    /// var registry = SkillNodes.Registry(catalog);
    /// var g = Spec.FromSpec(spec, registry).Compile();
    /// </code></example>
    public static Dictionary<string, NodeBuilder> Registry(ISkillSource source) => new()
    {
        ["skill"] = config =>
        {
            if (config.GetValueOrDefault("skill") is not string name || name.Length == 0)
                throw new GraphException("a \"skill\" node needs config.skill: the skill's name");
            var skill = source.Get(name)
                ?? throw new GraphException(
                    $"\"skill\" node references \"{name}\", which the skill source does not hold. " +
                    $"Known: [{string.Join(", ", source.List().Select(s => $"\"{s.Name}\""))}]");
            var to = ChannelOf(config);
            return (_, _) => new ValueTask<object?>(new Dictionary<string, object?> { [to] = skill.Instructions });
        },
        ["skills"] = config =>
        {
            var to = ChannelOf(config);
            var intro = config.TryGetValue("intro", out var i) ? i?.ToString() : SkillPrompt.DefaultIntro;
            return (_, _) => new ValueTask<object?>(new Dictionary<string, object?> { [to] = SkillPrompt.Render(source.List(), intro) });
        },
    };

    private static string ChannelOf(IReadOnlyDictionary<string, object?> config)
    {
        if (!config.TryGetValue("to", out var to) || to is null) return DefaultChannel;
        if (to is not string s || s.Length == 0) throw new GraphException("config.to must be a channel name");
        return s;
    }
}
