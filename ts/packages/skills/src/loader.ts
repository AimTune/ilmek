// Loading skills from disk: one folder → one Skill, a root → every skill folder
// under it. The only place in the package that touches the filesystem.

import { readdir, readFile, realpath, stat } from "node:fs/promises";
import { basename, isAbsolute, join, relative, resolve, sep } from "node:path";

import { SkillParseError } from "./frontmatter.ts";
import { parseSkill, type Skill } from "./skill.ts";

/** The file every skill folder must contain at its root. */
export const SKILL_FILE = "SKILL.md";

export interface LoadSkillOptions {
    /**
     * Require the frontmatter `name` to equal the folder name — the format's
     * rule, and what makes a skill addressable by the path it was found at.
     * Default `true`; turn it off to load a folder whose name is not the skill's.
     */
    strictName?: boolean;
}

/**
 * Load the skill in `dir`: read its SKILL.md, list its bundled resources.
 * Throws {@link SkillParseError} on a malformed SKILL.md (`code: "not_a_skill"`
 * when the folder has none).
 */
export async function loadSkill(dir: string, opts: LoadSkillOptions = {}): Promise<Skill> {
    const path = resolve(dir);
    const file = join(path, SKILL_FILE);
    let markdown: string;
    try {
        markdown = await readFile(file, "utf8");
    } catch (err) {
        if ((err as NodeJS.ErrnoException).code === "ENOENT") {
            throw new SkillParseError("not_a_skill", `${path} has no ${SKILL_FILE}`);
        }
        throw err;
    }
    const resources = await listResources(path);
    const expectName = opts.strictName === false ? undefined : basename(path);
    return parseSkill(markdown, { path, resources, ...(expectName !== undefined ? { expectName } : {}) });
}

/**
 * Load every skill folder directly under each root — a folder counts when it
 * holds a SKILL.md; anything else is skipped silently. A malformed SKILL.md
 * still throws: a broken skill should fail loudly, not vanish from the catalog.
 *
 * Roots are read in order and a later root's skill **replaces** an earlier one
 * of the same name (project skills over shared ones). The result is sorted by
 * name.
 */
export async function discoverSkills(roots: string | readonly string[], opts: LoadSkillOptions = {}): Promise<Skill[]> {
    const byName = new Map<string, Skill>();
    for (const root of typeof roots === "string" ? [roots] : roots) {
        const rootPath = resolve(root);
        let entries: string[];
        try {
            entries = await readdir(rootPath);
        } catch (err) {
            if ((err as NodeJS.ErrnoException).code === "ENOENT") continue; // an absent root holds no skills
            throw err;
        }
        for (const entry of entries.sort()) {
            const dir = join(rootPath, entry);
            if (!(await isDirectory(dir))) continue;
            if (!(await exists(join(dir, SKILL_FILE)))) continue;
            const skill = await loadSkill(dir, opts);
            byName.set(skill.name, skill);
        }
    }
    return [...byName.values()].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
}

/**
 * Every file under `dir` except SKILL.md and dot-entries, as sorted `/`-relative
 * paths. Links are followed only when their real target stays strictly inside
 * the skill folder — a link out would advertise a resource `readResource`
 * refuses — and each real directory is walked once, so a link back to the
 * folder or an ancestor cannot recurse forever.
 */
async function listResources(dir: string): Promise<string[]> {
    const out: string[] = [];
    const realRoot = await realpath(dir);
    const seen = new Set<string>([realRoot]);
    const walk = async (current: string): Promise<void> => {
        // Real entries before links, so a directory reachable both ways is
        // listed under its real path.
        const entries = (await readdir(current, { withFileTypes: true })).sort(
            (x, y) => Number(x.isSymbolicLink()) - Number(y.isSymbolicLink()),
        );
        for (const entry of entries) {
            if (entry.name.startsWith(".")) continue;
            const full = join(current, entry.name);
            if (entry.isSymbolicLink()) {
                const target = await realpath(full).catch(() => null);
                if (target === null || !isStrictlyInside(realRoot, target)) continue;
                if (await isDirectory(full)) {
                    if (seen.has(target)) continue;
                    seen.add(target);
                    await walk(full);
                    continue;
                }
            } else if (entry.isDirectory()) {
                const real = await realpath(full);
                if (seen.has(real)) continue;
                seen.add(real);
                await walk(full);
                continue;
            }
            if (current === dir && entry.name === SKILL_FILE) continue;
            out.push(relative(dir, full).split(sep).join("/"));
        }
    };
    await walk(dir);
    return out.sort();
}

function isStrictlyInside(root: string, target: string): boolean {
    const rel = relative(root, target);
    return rel !== "" && !rel.startsWith("..") && !isAbsolute(rel);
}

async function isDirectory(path: string): Promise<boolean> {
    try {
        return (await stat(path)).isDirectory();
    } catch {
        return false;
    }
}

async function exists(path: string): Promise<boolean> {
    try {
        await stat(path);
        return true;
    } catch {
        return false;
    }
}
