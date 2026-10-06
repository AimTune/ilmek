// Level 1 as text: the block a host puts in a system prompt so the model knows
// which skills exist. Deterministic and byte-identical to the .NET renderer
// (conformance/skills/expected.json pins the output).

import type { SkillMetadata } from "./skill.ts";

export interface SkillsPromptOptions {
    /**
     * The sentence(s) before the `<available_skills>` block. `null` renders the
     * block alone. Default: {@link DEFAULT_SKILLS_INTRO}.
     */
    intro?: string | null;
}

/** What a model is told about the block, unless the host says otherwise. */
export const DEFAULT_SKILLS_INTRO =
    "You have the following skills available. Each entry gives a skill's name and what it is for. " +
    "When a task matches a skill, load that skill's full instructions by name before you act on the task.";

/**
 * Render the level-1 catalog for a system prompt:
 *
 * ```xml
 * <available_skills>
 *   <skill>
 *     <name>pdf</name>
 *     <description>Fill, merge and read PDF forms.</description>
 *   </skill>
 * </available_skills>
 * ```
 *
 * Returns `""` for an empty list — nothing to announce, nothing in the prompt.
 */
export function renderSkillsPrompt(skills: readonly SkillMetadata[], opts: SkillsPromptOptions = {}): string {
    if (skills.length === 0) return "";
    const intro = opts.intro === undefined ? DEFAULT_SKILLS_INTRO : opts.intro;
    const lines: string[] = [];
    if (intro !== null && intro !== "") lines.push(intro, "");
    lines.push("<available_skills>");
    for (const s of skills) {
        lines.push("  <skill>", `    <name>${escapeXml(s.name)}</name>`, `    <description>${escapeXml(s.description)}</description>`, "  </skill>");
    }
    lines.push("</available_skills>");
    return lines.join("\n");
}

function escapeXml(text: string): string {
    return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}
