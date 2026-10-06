// MCP prompts as skills. A server's prompt is a named, described piece of
// instruction — exactly a skill's level 1 and 2 — so a toolbox can hand its
// prompts to a skill catalog and a model discovers them the same way it
// discovers SKILL.md folders. The shape matches `@ilmek/skills`' `Skill`
// structurally; this package does not import it.

import type { McpToolbox } from "./toolbox.ts";

/** A skill derived from an MCP prompt — the `Skill` shape of `@ilmek/skills`. */
export interface McpSkill {
    readonly name: string;
    readonly description: string;
    readonly instructions: string;
    readonly resources: readonly string[];
    /** `server`, `prompt`, and — when the prompt takes arguments — `arguments` as a comma-separated list. */
    readonly metadata: Readonly<Record<string, string>>;
}

export interface McpSkillsOptions {
    /**
     * Prepended to the skill name. Default `"<server>-"`, so prompt `review`
     * on server `github` becomes skill `github-review`. Pass `""` for bare names.
     */
    prefix?: string;
}

/**
 * Turn a toolbox's prompts into skills. A prompt with **no required arguments**
 * is fetched once and its text becomes the instructions. A prompt that needs
 * arguments cannot be fetched blind: its instructions say which arguments it
 * takes and how to fetch it (`toolbox.prompt(ctx, name, args)`), so a model
 * still learns it exists (level 1) without a half-rendered body.
 *
 * Names are coerced to the skill name rule — lowercased, anything outside
 * `[a-z0-9]` becomes `-`, runs collapsed, trimmed.
 */
export async function mcpSkills(toolbox: McpToolbox, opts: McpSkillsOptions = {}): Promise<McpSkill[]> {
    const prefix = opts.prefix ?? `${toolbox.name}-`;
    const out: McpSkill[] = [];
    for (const p of await toolbox.prompts()) {
        const name = toSkillName(`${prefix}${p.name}`);
        const args = p.arguments ?? [];
        const required = args.filter((a) => a.required === true);
        const description = (p.description ?? `The ${JSON.stringify(p.name)} prompt of MCP server ${JSON.stringify(toolbox.name)}.`).slice(0, 1024);
        const metadata: Record<string, string> = { server: toolbox.name, prompt: p.name };
        if (args.length > 0) metadata.arguments = args.map((a) => a.name).join(",");

        let instructions: string;
        if (required.length === 0) {
            instructions = await toolbox.fetchPrompt(p.name, {});
        } else {
            const list = args.map((a) => `- ${a.name}${a.required ? " (required)" : ""}${a.description ? `: ${a.description}` : ""}`).join("\n");
            instructions =
                `This skill is the MCP prompt ${JSON.stringify(p.name)} on server ${JSON.stringify(toolbox.name)}. ` +
                `It takes arguments, so its text is fetched per use with those arguments:\n${list}`;
        }
        out.push({ name, description, instructions, resources: [], metadata });
    }
    return out.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
}

/** Coerce any string to a valid skill name (see `@ilmek/skills`). */
export function toSkillName(raw: string): string {
    const slug = raw
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, "-")
        .replace(/^-+|-+$/g, "")
        .slice(0, 64)
        .replace(/-+$/g, "");
    return slug.length > 0 ? slug : "prompt";
}
