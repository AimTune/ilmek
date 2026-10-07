// The catalog: the loaded skills behind the three-level interface a host reads
// them through. `SkillSource` is the port — anything that can list metadata and
// hand back a skill by name is one, so a host can back it with a database, a
// remote registry or a client's declarations just as well as with folders.

import { readFile, realpath } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";

import { SkillParseError } from "./frontmatter.ts";
import { discoverSkills, type LoadSkillOptions } from "./loader.ts";
import { toMetadata, type Skill, type SkillMetadata } from "./skill.ts";

/**
 * Where skills come from, as a host sees them. Level 1 is {@link list}, level
 * 2 is {@link get}, level 3 is the optional {@link readResource}.
 */
export interface SkillSource {
    /** Every skill's level-1 metadata, sorted by name. What goes in the system prompt. */
    list(): readonly SkillMetadata[];
    /** The whole skill — instructions included — or `undefined` when no skill has that name. */
    get(name: string): Skill | undefined;
    /**
     * The text of one bundled file (`skill.resources` names them). Optional:
     * a source with nothing on disk leaves it out.
     */
    readResource?(name: string, path: string): Promise<string>;
}

/**
 * An in-memory {@link SkillSource} over loaded skills — from folders via
 * {@link SkillCatalog.fromDirectories}, or from objects for inline and test
 * skills. Names are unique; a duplicate throws at construction.
 */
export class SkillCatalog implements SkillSource {
    private readonly skills: ReadonlyMap<string, Skill>;

    constructor(skills: Iterable<Skill>) {
        const map = new Map<string, Skill>();
        for (const skill of skills) {
            if (map.has(skill.name)) {
                throw new SkillParseError("duplicate_skill", `two skills are named ${JSON.stringify(skill.name)}`);
            }
            map.set(skill.name, skill);
        }
        this.skills = new Map([...map.entries()].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
    }

    /** Discover every skill folder under the given roots (see `discoverSkills`) and catalog them. */
    static async fromDirectories(roots: string | readonly string[], opts: LoadSkillOptions = {}): Promise<SkillCatalog> {
        return new SkillCatalog(await discoverSkills(roots, opts));
    }

    /** How many skills the catalog holds. */
    get size(): number {
        return this.skills.size;
    }

    has(name: string): boolean {
        return this.skills.has(name);
    }

    list(): SkillMetadata[] {
        return [...this.skills.values()].map(toMetadata);
    }

    get(name: string): Skill | undefined {
        return this.skills.get(name);
    }

    /** Every skill, instructions included, sorted by name. */
    all(): Skill[] {
        return [...this.skills.values()];
    }

    /**
     * Read one bundled file of a skill. `path` is `/`-relative to the skill
     * folder and must stay inside it — `..` segments and absolute paths are
     * refused — so a model-supplied path can never reach outside the skill.
     */
    async readResource(name: string, path: string): Promise<string> {
        const skill = this.skills.get(name);
        if (!skill) throw new SkillParseError("unknown_skill", `no skill named ${JSON.stringify(name)}`);
        if (skill.path === undefined) {
            throw new SkillParseError("no_resources", `skill ${JSON.stringify(name)} is inline and has no files on disk`);
        }
        const root = resolve(skill.path);
        if (typeof path !== "string" || path.includes("\0")) {
            throw new SkillParseError("outside_skill", `resource path ${JSON.stringify(path)} is not a valid relative path`);
        }
        if (isAbsolute(path)) throw new SkillParseError("outside_skill", `resource paths are relative to the skill folder: ${path}`);
        const full = resolve(root, path);
        if (!isInside(root, full)) {
            throw new SkillParseError("outside_skill", `resource ${JSON.stringify(path)} is outside skill ${JSON.stringify(name)}`);
        }
        // The check above is lexical. A symlink inside the folder can still
        // point anywhere (a downloaded skill shipping `refs -> ~/.ssh`), so the
        // resolved target must stay inside the resolved skill folder too.
        const [realRoot, realFull] = await Promise.all([realpath(root), realpath(full)]);
        if (!isInside(realRoot, realFull)) {
            throw new SkillParseError("outside_skill", `resource ${JSON.stringify(path)} links outside skill ${JSON.stringify(name)}`);
        }
        return readFile(realFull, "utf8");
    }
}

/** Is `full` strictly inside `root` (not `root` itself)? Both must be resolved. */
function isInside(root: string, full: string): boolean {
    const rel = relative(root, full);
    return !(rel === "" || rel.startsWith("..") || isAbsolute(rel) || rel.split(sep).includes(".."));
}
