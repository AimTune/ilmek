using System.Text.RegularExpressions;

namespace Ilmek.Mcp;

/// <summary>
/// A skill derived from an MCP prompt — the shape of <c>Ilmek.Skills</c>' <c>Skill</c>
/// (name, description, instructions, resources, metadata), without a package
/// reference. Mirror of TypeScript's <c>McpSkill</c>.
/// </summary>
public sealed record McpSkill
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Instructions { get; init; }
    public IReadOnlyList<string> Resources { get; init; } = [];
    /// <summary><c>server</c>, <c>prompt</c>, and — when the prompt takes arguments — <c>arguments</c> as a comma-separated list.</summary>
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }

    /// <summary>The TypeScript object shape.</summary>
    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["instructions"] = Instructions,
        ["resources"] = Resources.Cast<object?>().ToList(),
        ["metadata"] = Metadata.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
    };
}

/// <summary>
/// MCP prompts as skills. A server's prompt is a named, described piece of
/// instruction — exactly a skill's level 1 and 2 — so a toolbox can hand its
/// prompts to a skill catalog. Mirror of <c>@ilmek/mcp</c>' <c>mcpSkills</c>.
/// </summary>
public static class McpSkills
{
    private static readonly Regex NotAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Turn a toolbox's prompts into skills, sorted by name. A prompt with <b>no
    /// required arguments</b> is fetched once and its text becomes the instructions.
    /// A prompt that needs arguments cannot be fetched blind: its instructions say
    /// which arguments it takes and how to fetch it, so a model still learns it exists.
    /// </summary>
    /// <param name="toolbox">The connected server.</param>
    /// <param name="prefix">Prepended to the skill name. Default <c>"&lt;server&gt;-"</c>; <c>""</c> for bare names.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<IReadOnlyList<McpSkill>> FromPromptsAsync(McpToolbox toolbox, string? prefix = null, CancellationToken ct = default)
    {
        prefix ??= $"{toolbox.Name}-";
        var result = new List<McpSkill>();
        foreach (var p in await toolbox.PromptsAsync(ct).ConfigureAwait(false))
        {
            var name = ToSkillName(prefix + p.Name);
            var required = p.Arguments.Where(a => a.Required).ToList();
            var description = p.Description ?? $"The \"{p.Name}\" prompt of MCP server \"{toolbox.Name}\".";
            if (description.Length > 1024) description = description[..1024];
            var metadata = new Dictionary<string, string> { ["server"] = toolbox.Name, ["prompt"] = p.Name };
            if (p.Arguments.Count > 0) metadata["arguments"] = string.Join(",", p.Arguments.Select(a => a.Name));

            string instructions;
            if (required.Count == 0)
            {
                instructions = await toolbox.FetchPromptAsync(p.Name, null, ct).ConfigureAwait(false);
            }
            else
            {
                var list = string.Join("\n", p.Arguments.Select(a =>
                    $"- {a.Name}{(a.Required ? " (required)" : "")}{(a.Description is not null ? $": {a.Description}" : "")}"));
                instructions =
                    $"This skill is the MCP prompt \"{p.Name}\" on server \"{toolbox.Name}\". " +
                    $"It takes arguments, so its text is fetched per use with those arguments:\n{list}";
            }
            result.Add(new McpSkill { Name = name, Description = description, Instructions = instructions, Metadata = metadata });
        }
        return result.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Coerce any string to a valid skill name: lowercased, runs of anything outside <c>[a-z0-9]</c> become <c>-</c>, trimmed, at most 64 characters.</summary>
    public static string ToSkillName(string raw)
    {
        var slug = NotAlnum.Replace(raw.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 64) slug = slug[..64].TrimEnd('-');
        return slug.Length > 0 ? slug : "prompt";
    }
}
