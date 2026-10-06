// The skill model and the SKILL.md reader (the Agent Skills format).
//
// A skill is a folder with a SKILL.md at its root: YAML frontmatter that names
// and describes it, then markdown instructions. Anything else in the folder
// (scripts/, references/, assets/) is a bundled resource. The format is
// designed for progressive disclosure — a model sees every skill's name and
// description up front (level 1), reads the instructions only when a task
// matches (level 2), and opens bundled files only when the instructions point
// at them (level 3). `@ilmek/skills` models exactly those three levels.

import { parseFrontmatter, SkillParseError, type FrontmatterValue } from "./frontmatter.ts";

/**
 * Level 1 — what a model sees before choosing a skill. Small on purpose: this
 * is what {@link renderSkillsPrompt} puts in the system prompt for every skill.
 */
export interface SkillMetadata {
    /** Lowercase letters, digits and single hyphens; 1–64 characters; equals the folder name. */
    readonly name: string;
    /** What the skill does and when to use it — the whole trigger surface. ≤ 1024 characters. */
    readonly description: string;
    /** SPDX-style license note, if the skill carries one. */
    readonly license?: string;
    /** Free-form environment requirements the author declared. */
    readonly compatibility?: string;
    /** Tools the skill pre-approves (`allowed-tools` in the frontmatter). Advisory — the host decides. */
    readonly allowedTools?: readonly string[];
    /** Author-defined string→string pairs (`metadata:` in the frontmatter). */
    readonly metadata?: Readonly<Record<string, string>>;
}

/** Level 2 and 3 — the instructions and the bundled resources a loaded skill carries. */
export interface Skill extends SkillMetadata {
    /** The markdown body of SKILL.md, trimmed. What a model reads once it picks the skill. */
    readonly instructions: string;
    /** Bundled files, as `/`-separated paths relative to the skill folder, sorted. Never SKILL.md itself. */
    readonly resources: readonly string[];
    /** The skill folder on disk, when the skill was loaded from one. Inline skills have none. */
    readonly path?: string;
}

/** The maximum lengths the format sets. */
export const SKILL_NAME_MAX = 64;
export const SKILL_DESCRIPTION_MAX = 1024;

const NAME_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/**
 * Is `name` a valid skill name? Lowercase letters, digits and hyphens; no
 * leading, trailing or doubled hyphen; 1–64 characters.
 */
export function isValidSkillName(name: string): boolean {
    return name.length > 0 && name.length <= SKILL_NAME_MAX && NAME_PATTERN.test(name);
}

export interface ParseSkillOptions {
    /** Recorded on the skill as {@link Skill.path}. */
    path?: string;
    /** When set, the frontmatter `name` must equal this (the folder-name rule). */
    expectName?: string;
    /** Pre-listed bundled resources. Default: none. */
    resources?: readonly string[];
}

type Mutable<T> = { -readonly [K in keyof T]: T[K] };

/**
 * Parse the text of a SKILL.md into a {@link Skill}. Throws
 * {@link SkillParseError} with a stable `code` on a malformed document:
 * `missing_frontmatter`, `invalid_name`, `name_mismatch`, `invalid_description`,
 * `invalid_field`, or one of the YAML reader's codes.
 */
export function parseSkill(markdown: string, opts: ParseSkillOptions = {}): Skill {
    const { data, body } = parseFrontmatter(markdown);

    const name = data.name;
    if (typeof name !== "string" || !isValidSkillName(name)) {
        throw new SkillParseError(
            "invalid_name",
            `frontmatter "name" must be 1–${SKILL_NAME_MAX} lowercase letters, digits or single hyphens; got ${JSON.stringify(name ?? null)}`,
        );
    }
    if (opts.expectName !== undefined && name !== opts.expectName) {
        throw new SkillParseError(
            "name_mismatch",
            `frontmatter "name" is ${JSON.stringify(name)} but the skill folder is ${JSON.stringify(opts.expectName)} — they must match`,
        );
    }

    const description = typeof data.description === "string" ? data.description.trim() : undefined;
    if (description === undefined || description.length === 0 || description.length > SKILL_DESCRIPTION_MAX) {
        throw new SkillParseError(
            "invalid_description",
            `frontmatter "description" is required and must be 1–${SKILL_DESCRIPTION_MAX} characters`,
        );
    }

    const skill: Mutable<Skill> = {
        name,
        description,
        instructions: body.trim(),
        resources: [...(opts.resources ?? [])].sort(),
    };

    const license = optionalString(data, "license");
    if (license !== undefined) skill.license = license;
    const compatibility = optionalString(data, "compatibility");
    if (compatibility !== undefined) skill.compatibility = compatibility;

    const allowed = data["allowed-tools"];
    if (allowed !== undefined) {
        let tools: string[];
        if (typeof allowed === "string") {
            tools = allowed.split(/\s+/).filter((t) => t !== "");
        } else if (Array.isArray(allowed) && allowed.every((v): v is string => typeof v === "string")) {
            tools = allowed.flatMap((v) => v.split(/\s+/)).filter((t) => t !== "");
        } else {
            throw new SkillParseError(
                "invalid_field",
                `frontmatter "allowed-tools" must be a space-separated string or a list of strings`,
            );
        }
        if (tools.length > 0) skill.allowedTools = tools;
    }

    const meta = data.metadata;
    if (meta !== undefined && meta !== "") {
        if (typeof meta !== "object" || Array.isArray(meta)) {
            throw new SkillParseError("invalid_field", `frontmatter "metadata" must be a mapping of string keys to string values`);
        }
        const out: Record<string, string> = {};
        for (const key of Object.keys(meta).sort()) {
            const v = meta[key];
            if (typeof v !== "string") {
                throw new SkillParseError("invalid_field", `frontmatter "metadata.${key}" must be a string`);
            }
            out[key] = v;
        }
        skill.metadata = out;
    }

    if (opts.path !== undefined) skill.path = opts.path;
    return skill;
}

function optionalString(data: Record<string, FrontmatterValue>, key: string): string | undefined {
    const v = data[key];
    if (v === undefined || v === "") return undefined;
    if (typeof v !== "string") throw new SkillParseError("invalid_field", `frontmatter "${key}" must be a string`);
    return v.trim();
}

/** The level-1 view of a skill: name, description and the small declarative fields — never the instructions. */
export function toMetadata(skill: SkillMetadata): SkillMetadata {
    const out: Mutable<SkillMetadata> = { name: skill.name, description: skill.description };
    if (skill.license !== undefined) out.license = skill.license;
    if (skill.compatibility !== undefined) out.compatibility = skill.compatibility;
    if (skill.allowedTools !== undefined) out.allowedTools = [...skill.allowedTools];
    if (skill.metadata !== undefined) out.metadata = { ...skill.metadata };
    return out;
}

export { SkillParseError };
