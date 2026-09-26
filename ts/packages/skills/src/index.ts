/**
 * `@ilmek/skills` — Agent Skills for ilmek.
 *
 * A skill is a folder with a `SKILL.md`: YAML frontmatter (`name`,
 * `description`, …) and markdown instructions, plus any bundled files. This
 * package reads that format and exposes it the way a model should consume it —
 * **progressively**: names and descriptions first, instructions on demand,
 * bundled files only when the instructions point at them.
 *
 * ```ts
 * import { SkillCatalog, renderSkillsPrompt } from "@ilmek/skills";
 *
 * const skills = await SkillCatalog.fromDirectories(["./skills"]);
 * const system = base + "\n\n" + renderSkillsPrompt(skills.list());   // level 1
 * const pdf = skills.get("pdf")?.instructions;                          // level 2
 * const form = await skills.readResource("pdf", "references/forms.md"); // level 3
 * ```
 *
 * ilmek itself stays LLM-agnostic: nothing here calls a model. A host (mekik,
 * your own agent loop) decides how the catalog reaches the prompt and how a
 * `load_skill` tool hands instructions back.
 */

export { parseFrontmatter, SkillParseError } from "./frontmatter.ts";
export type { Frontmatter, FrontmatterValue } from "./frontmatter.ts";

export { isValidSkillName, parseSkill, toMetadata, SKILL_DESCRIPTION_MAX, SKILL_NAME_MAX } from "./skill.ts";
export type { ParseSkillOptions, Skill, SkillMetadata } from "./skill.ts";

export { discoverSkills, loadSkill, SKILL_FILE } from "./loader.ts";
export type { LoadSkillOptions } from "./loader.ts";

export { SkillCatalog } from "./catalog.ts";
export type { SkillSource } from "./catalog.ts";

export { DEFAULT_SKILLS_INTRO, renderSkillsPrompt } from "./prompt.ts";
export type { SkillsPromptOptions } from "./prompt.ts";

export { DEFAULT_SKILL_CHANNEL, skillNodes } from "./nodes.ts";
