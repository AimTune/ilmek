using System.Text;

namespace Ilmek.Skills;

/// <summary>
/// Level 1 as text: the block a host puts in a system prompt so the model knows
/// which skills exist. Deterministic and byte-identical to the TypeScript
/// renderer (conformance/skills/expected.json pins the output).
/// </summary>
public static class SkillPrompt
{
    /// <summary>What a model is told about the block, unless the host says otherwise.</summary>
    public const string DefaultIntro =
        "You have the following skills available. Each entry gives a skill's name and what it is for. " +
        "When a task matches a skill, load that skill's full instructions by name before you act on the task.";

    /// <summary>
    /// Render the level-1 catalog for a system prompt:
    /// <code>
    /// &lt;available_skills&gt;
    ///   &lt;skill&gt;
    ///     &lt;name&gt;pdf&lt;/name&gt;
    ///     &lt;description&gt;Fill, merge and read PDF forms.&lt;/description&gt;
    ///   &lt;/skill&gt;
    /// &lt;/available_skills&gt;
    /// </code>
    /// Returns <c>""</c> for an empty list — nothing to announce, nothing in the prompt.
    /// </summary>
    /// <param name="skills">The level-1 metadata, in the order to render.</param>
    /// <param name="intro">
    /// The sentence(s) before the block. <see cref="DefaultIntro"/> unless given; pass
    /// <c>null</c> or <c>""</c> to render the block alone.
    /// </param>
    public static string Render(IEnumerable<SkillMetadata> skills, string? intro = DefaultIntro)
    {
        var list = skills as IReadOnlyList<SkillMetadata> ?? skills.ToList();
        if (list.Count == 0) return "";
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(intro))
        {
            lines.Add(intro);
            lines.Add("");
        }
        lines.Add("<available_skills>");
        foreach (var s in list)
        {
            lines.Add("  <skill>");
            lines.Add($"    <name>{Escape(s.Name)}</name>");
            lines.Add($"    <description>{Escape(s.Description)}</description>");
            lines.Add("  </skill>");
        }
        lines.Add("</available_skills>");
        return string.Join("\n", lines);
    }

    private static string Escape(string text) =>
        new StringBuilder(text).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").ToString();
}
