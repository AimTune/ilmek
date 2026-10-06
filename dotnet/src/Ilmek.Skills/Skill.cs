using System.Text.RegularExpressions;

namespace Ilmek.Skills;

/// <summary>
/// Level 1 — what a model sees before choosing a skill. Small on purpose: this
/// is what <see cref="SkillPrompt.Render"/> puts in the system prompt for every skill.
/// </summary>
public record SkillMetadata
{
    /// <summary>Lowercase letters, digits and single hyphens; 1–64 characters; equals the folder name.</summary>
    public required string Name { get; init; }

    /// <summary>What the skill does and when to use it — the whole trigger surface. ≤ 1024 characters.</summary>
    public required string Description { get; init; }

    /// <summary>SPDX-style license note, if the skill carries one.</summary>
    public string? License { get; init; }

    /// <summary>Free-form environment requirements the author declared.</summary>
    public string? Compatibility { get; init; }

    /// <summary>Tools the skill pre-approves (<c>allowed-tools</c> in the frontmatter). Advisory — the host decides.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>Author-defined string→string pairs (<c>metadata:</c> in the frontmatter), sorted by key.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>The level-1 view of this skill: name, description and the small declarative fields — never the instructions.</summary>
    public SkillMetadata ToMetadata() => new()
    {
        Name = Name,
        Description = Description,
        License = License,
        Compatibility = Compatibility,
        AllowedTools = AllowedTools,
        Metadata = Metadata,
    };
}

/// <summary>Level 2 and 3 — the instructions and the bundled resources a loaded skill carries.</summary>
public sealed record Skill : SkillMetadata
{
    /// <summary>The markdown body of SKILL.md, trimmed. What a model reads once it picks the skill.</summary>
    public required string Instructions { get; init; }

    /// <summary>Bundled files, as <c>/</c>-separated paths relative to the skill folder, sorted. Never SKILL.md itself.</summary>
    public IReadOnlyList<string> Resources { get; init; } = [];

    /// <summary>The skill folder on disk, when the skill was loaded from one. Inline skills have none.</summary>
    public string? Path { get; init; }
}

/// <summary>The SKILL.md reader — the Agent Skills format, mirror of <c>@ilmek/skills</c>' <c>parseSkill</c>.</summary>
public static class SkillParser
{
    /// <summary>The maximum length of a skill name.</summary>
    public const int NameMax = 64;

    /// <summary>The maximum length of a skill description.</summary>
    public const int DescriptionMax = 1024;

    private static readonly Regex NamePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Is <paramref name="name"/> a valid skill name? Lowercase letters, digits and
    /// hyphens; no leading, trailing or doubled hyphen; 1–64 characters.
    /// </summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && name.Length <= NameMax && NamePattern.IsMatch(name);

    /// <summary>
    /// Parse the text of a SKILL.md into a <see cref="Skill"/>. Throws
    /// <see cref="SkillParseException"/> with a stable code on a malformed document:
    /// <c>missing_frontmatter</c>, <c>invalid_name</c>, <c>name_mismatch</c>,
    /// <c>invalid_description</c>, <c>invalid_field</c>, or one of the YAML reader's codes.
    /// </summary>
    /// <param name="markdown">The SKILL.md text.</param>
    /// <param name="path">Recorded on the skill as <see cref="Skill.Path"/>.</param>
    /// <param name="expectName">When set, the frontmatter <c>name</c> must equal this (the folder-name rule).</param>
    /// <param name="resources">Pre-listed bundled resources. Default: none.</param>
    public static Skill Parse(string markdown, string? path = null, string? expectName = null, IEnumerable<string>? resources = null)
    {
        var (data, body) = FrontmatterReader.Parse(markdown);

        if (data.GetValueOrDefault("name") is not string name || !IsValidName(name))
        {
            var got = data.GetValueOrDefault("name") is string s ? $"\"{s}\"" : "null";
            throw new SkillParseException("invalid_name",
                $"frontmatter \"name\" must be 1–{NameMax} lowercase letters, digits or single hyphens; got {got}");
        }
        if (expectName is not null && name != expectName)
            throw new SkillParseException("name_mismatch",
                $"frontmatter \"name\" is \"{name}\" but the skill folder is \"{expectName}\" — they must match");

        var description = (data.GetValueOrDefault("description") as string)?.Trim();
        if (string.IsNullOrEmpty(description) || description.Length > DescriptionMax)
            throw new SkillParseException("invalid_description",
                $"frontmatter \"description\" is required and must be 1–{DescriptionMax} characters");

        IReadOnlyList<string>? allowedTools = null;
        if (data.TryGetValue("allowed-tools", out var allowed))
        {
            List<string> tools;
            if (allowed is string one)
                tools = Whitespace.Split(one).Where(t => t.Length > 0).ToList();
            else if (allowed is IReadOnlyList<object?> many && many.All(v => v is string))
                tools = many.Cast<string>().SelectMany(v => Whitespace.Split(v)).Where(t => t.Length > 0).ToList();
            else
                throw new SkillParseException("invalid_field", "frontmatter \"allowed-tools\" must be a space-separated string or a list of strings");
            if (tools.Count > 0) allowedTools = tools;
        }

        IReadOnlyDictionary<string, string>? metadata = null;
        if (data.TryGetValue("metadata", out var meta) && meta is not "")
        {
            if (meta is not IReadOnlyDictionary<string, object?> map)
                throw new SkillParseException("invalid_field", "frontmatter \"metadata\" must be a mapping of string keys to string values");
            var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in map)
            {
                if (value is not string sv)
                    throw new SkillParseException("invalid_field", $"frontmatter \"metadata.{key}\" must be a string");
                sorted[key] = sv;
            }
            metadata = sorted;
        }

        return new Skill
        {
            Name = name,
            Description = description,
            License = OptionalString(data, "license"),
            Compatibility = OptionalString(data, "compatibility"),
            AllowedTools = allowedTools,
            Metadata = metadata,
            Instructions = body.Trim(),
            Resources = (resources ?? []).OrderBy(r => r, StringComparer.Ordinal).ToList(),
            Path = path,
        };
    }

    private static string? OptionalString(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var v) || v is "") return null;
        if (v is not string s) throw new SkillParseException("invalid_field", $"frontmatter \"{key}\" must be a string");
        return s.Trim();
    }
}
