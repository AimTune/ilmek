// Skills as graph data (MODEL.md §9): two node types a stored spec can name.
// A spec never carries executable text, so a skill is referenced by name and
// resolved through the registry at build time — exactly how every other node
// type works.

import { GraphError, type NodeRegistry } from "@ilmek/core";

import type { SkillSource } from "./catalog.ts";
import { renderSkillsPrompt } from "./prompt.ts";

/** The default channel the skill node types write to. */
export const DEFAULT_SKILL_CHANNEL = "instructions";

/**
 * A node registry with two types:
 *
 * - `skill` — config `{ skill: string, to?: string }`. Writes the named skill's
 *   instructions to channel `to` (default `"instructions"`), so a downstream
 *   node — an LLM call — reads them from state. An unknown skill name fails at
 *   build time, not mid-run.
 * - `skills` — config `{ to?: string, intro?: string | null }`. Writes the
 *   level-1 catalog prompt (see `renderSkillsPrompt`) to channel `to`.
 *
 * ```json
 * { "id": "brief", "type": "skill", "config": { "skill": "brand-voice", "to": "system" } }
 * ```
 */
export function skillNodes(source: SkillSource): NodeRegistry {
    return {
        skill: (config) => {
            const name = config.skill;
            if (typeof name !== "string" || name.length === 0) {
                throw new GraphError(`a "skill" node needs config.skill: the skill's name`);
            }
            const skill = source.get(name);
            if (!skill) {
                throw new GraphError(
                    `"skill" node references ${JSON.stringify(name)}, which the skill source does not hold. ` +
                        `Known: ${JSON.stringify(source.list().map((s) => s.name))}`,
                );
            }
            const to = channelOf(config);
            return () => ({ [to]: skill.instructions });
        },
        skills: (config) => {
            const to = channelOf(config);
            const intro = config.intro === undefined ? undefined : config.intro === null ? null : String(config.intro);
            return () => ({ [to]: renderSkillsPrompt(source.list(), intro === undefined ? {} : { intro }) });
        },
    };
}

function channelOf(config: Record<string, unknown>): string {
    const to = config.to;
    if (to === undefined) return DEFAULT_SKILL_CHANNEL;
    if (typeof to !== "string" || to.length === 0) throw new GraphError(`config.to must be a channel name`);
    return to;
}
