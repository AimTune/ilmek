/**
 * `@ilmek/skills` — the SKILL.md reader, the catalog, the prompt renderer and
 * the `skill` node types. The shared folders under conformance/skills are the
 * cross-language fixture: this suite and the .NET one must read them to the
 * same JSON.
 */

import test, { describe, after } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { END, fromSpec, run, START, type GraphSpec } from "@ilmek/core";

import {
    DEFAULT_SKILLS_INTRO,
    discoverSkills,
    isValidSkillName,
    loadSkill,
    parseFrontmatter,
    parseSkill,
    renderSkillsPrompt,
    SkillCatalog,
    SkillParseError,
    skillNodes,
    toMetadata,
} from "../src/index.ts";

const fixtures = join(dirname(fileURLToPath(import.meta.url)), "../../../../conformance/skills");

const workDir = mkdtempSync(join(tmpdir(), "ilmek-skills-"));
after(() => rmSync(workDir, { recursive: true, force: true }));

/** Write a throwaway skill folder and return its path. */
function skillDir(name: string, markdown: string, files: Record<string, string> = {}): string {
    const dir = join(workDir, `${Math.random().toString(36).slice(2)}`, name);
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, "SKILL.md"), markdown);
    for (const [rel, content] of Object.entries(files)) {
        mkdirSync(dirname(join(dir, rel)), { recursive: true });
        writeFileSync(join(dir, rel), content);
    }
    return dir;
}

const canonical = (v: unknown): string => JSON.stringify(sortKeys(v));
function sortKeys(v: unknown): unknown {
    if (Array.isArray(v)) return v.map(sortKeys);
    if (v !== null && typeof v === "object") {
        return Object.fromEntries(
            Object.keys(v as Record<string, unknown>)
                .sort()
                .map((k) => [k, sortKeys((v as Record<string, unknown>)[k])]),
        );
    }
    return v;
}

describe("conformance/skills (shared with .NET)", () => {
    test("the fixture folders read to expected.json", async () => {
        const expected = JSON.parse(readFileSync(join(fixtures, "expected.json"), "utf8")) as {
            skills: unknown[];
            prompt: string;
        };
        const skills = await discoverSkills(fixtures);
        const stripped = skills.map(({ path: _path, ...rest }) => rest);
        assert.equal(canonical(stripped), canonical(expected.skills));
        assert.equal(renderSkillsPrompt(skills.map(toMetadata)), expected.prompt);
    });

    test("every fixture skill records where it was loaded from", async () => {
        for (const s of await discoverSkills(fixtures)) assert.equal(s.path, join(fixtures, s.name));
    });
});

describe("frontmatter reader", () => {
    test("scalars, quoting, comments, nested mapping, block and flow sequences", () => {
        const { data, body } = parseFrontmatter(
            [
                "---",
                "name: demo",
                "plain: hello world   # a comment",
                'dq: "a \\"quoted\\" value"',
                "sq: 'it''s'",
                "url: https://example.com/#anchor",
                "flow: [a, \"b, c\", d]",
                "block:",
                "  - one",
                "  - two",
                "nested:",
                "  k1: v1",
                "  k2: 2",
                "empty:",
                "literal: |",
                "  line one",
                "  line two",
                "folded: >-",
                "  folded",
                "  together",
                "",
                "  new paragraph",
                "---",
                "",
                "# Body",
                "text",
            ].join("\n"),
        );
        assert.deepEqual(data, {
            name: "demo",
            plain: "hello world",
            dq: 'a "quoted" value',
            sq: "it's",
            url: "https://example.com/#anchor",
            flow: ["a", "b, c", "d"],
            block: ["one", "two"],
            nested: { k1: "v1", k2: "2" },
            empty: "",
            literal: "line one\nline two\n",
            folded: "folded together\nnew paragraph",
        });
        assert.equal(body, "# Body\ntext");
    });

    test("CRLF and a BOM are tolerated", () => {
        const { data, body } = parseFrontmatter("\uFEFF---\r\nname: x\r\n---\r\nbody\r\n");
        assert.deepEqual(data, { name: "x" });
        assert.equal(body, "body\n");
    });

    for (const [code, text] of [
        ["missing_frontmatter", "# no frontmatter"],
        ["unterminated_frontmatter", "---\nname: x\n"],
        ["bad_structure", "---\nnot a key\n---\n"],
        ["bad_indentation", "---\nname: x\n  stray: y\n---\n"],
        ["duplicate_key", "---\nname: x\nname: y\n---\n"],
        ["unsupported", "---\nx: {a: 1}\n---\n"],
        ["unsupported", "---\nx: &anchor v\n---\n"],
        ["unsupported", "---\nx:\n  - k: v\n---\n"],
    ] as const) {
        test(`rejects ${JSON.stringify(text)} with ${code}`, () => {
            assert.throws(() => parseFrontmatter(text), (e: unknown) => e instanceof SkillParseError && e.code === code);
        });
    }
});

describe("parseSkill", () => {
    test("reads the required and optional fields", () => {
        const s = parseSkill(
            "---\nname: a-b\ndescription: Does things.\nlicense: MIT\nallowed-tools: Read Bash(git:*)\nmetadata:\n  team: core\n---\n\nBody here.\n",
        );
        assert.deepEqual(s, {
            name: "a-b",
            description: "Does things.",
            license: "MIT",
            allowedTools: ["Read", "Bash(git:*)"],
            metadata: { team: "core" },
            instructions: "Body here.",
            resources: [],
        });
    });

    test("allowed-tools also accepts a list", () => {
        const s = parseSkill("---\nname: a\ndescription: d\nallowed-tools: [Read, Write]\n---\n");
        assert.deepEqual(s.allowedTools, ["Read", "Write"]);
        assert.equal(s.instructions, "");
    });

    test("name rules", () => {
        for (const ok of ["a", "pdf", "brand-voice", "a1-b2", "x".repeat(64)]) assert.ok(isValidSkillName(ok), ok);
        for (const bad of ["", "-a", "a-", "a--b", "Pdf", "a_b", "a b", "x".repeat(65)]) assert.ok(!isValidSkillName(bad), bad);
    });

    for (const [code, text] of [
        ["invalid_name", "---\ndescription: d\n---\n"],
        ["invalid_name", "---\nname: Not Valid\ndescription: d\n---\n"],
        ["invalid_description", "---\nname: ok\n---\n"],
        ["invalid_description", `---\nname: ok\ndescription: ${"x".repeat(1025)}\n---\n`],
        ["invalid_field", "---\nname: ok\ndescription: d\nmetadata: just-a-string\n---\n"],
        ["invalid_field", "---\nname: ok\ndescription: d\nmetadata:\n  k:\n    - nested\n---\n"],
        ["invalid_field", "---\nname: ok\ndescription: d\nlicense:\n  - MIT\n---\n"],
    ] as const) {
        test(`rejects with ${code}`, () => {
            assert.throws(() => parseSkill(text), (e: unknown) => e instanceof SkillParseError && e.code === code);
        });
    }

    test("expectName enforces the folder-name rule", () => {
        assert.throws(
            () => parseSkill("---\nname: a\ndescription: d\n---\n", { expectName: "b" }),
            (e: unknown) => e instanceof SkillParseError && e.code === "name_mismatch",
        );
    });

    test("toMetadata drops the instructions and copies the rest", () => {
        const s = parseSkill("---\nname: a\ndescription: d\nmetadata:\n  k: v\n---\nbody");
        assert.deepEqual(toMetadata(s), { name: "a", description: "d", metadata: { k: "v" } });
    });
});

describe("loader", () => {
    test("loadSkill lists bundled resources as sorted /-relative paths, skipping dot-entries", async () => {
        const dir = skillDir("demo", "---\nname: demo\ndescription: d\n---\nbody", {
            "scripts/run.sh": "echo",
            "references/b.md": "b",
            "references/a.md": "a",
            ".hidden": "no",
            "assets/.DS_Store": "no",
        });
        const s = await loadSkill(dir);
        assert.deepEqual(s.resources, ["references/a.md", "references/b.md", "scripts/run.sh"]);
        assert.equal(s.path, dir);
    });

    test("a folder whose name differs from the skill's is refused — unless strictName is off", async () => {
        const dir = skillDir("folder", "---\nname: other\ndescription: d\n---\n");
        await assert.rejects(loadSkill(dir), (e: unknown) => e instanceof SkillParseError && e.code === "name_mismatch");
        assert.equal((await loadSkill(dir, { strictName: false })).name, "other");
    });

    test("a folder without SKILL.md is not a skill", async () => {
        const dir = join(workDir, "plain");
        mkdirSync(dir, { recursive: true });
        await assert.rejects(loadSkill(dir), (e: unknown) => e instanceof SkillParseError && e.code === "not_a_skill");
    });

    test("discoverSkills: later roots override, non-skill folders are skipped, absent roots are empty", async () => {
        const rootA = join(workDir, "roots", "a");
        const rootB = join(workDir, "roots", "b");
        for (const [root, desc] of [
            [rootA, "from a"],
            [rootB, "from b"],
        ] as const) {
            mkdirSync(join(root, "shared"), { recursive: true });
            writeFileSync(join(root, "shared", "SKILL.md"), `---\nname: shared\ndescription: ${desc}\n---\n`);
        }
        mkdirSync(join(rootA, "only-a"));
        writeFileSync(join(rootA, "only-a", "SKILL.md"), "---\nname: only-a\ndescription: a\n---\n");
        mkdirSync(join(rootA, "not-a-skill"));
        writeFileSync(join(rootA, "loose-file.md"), "ignored");

        const skills = await discoverSkills([rootA, rootB, join(workDir, "missing")]);
        assert.deepEqual(
            skills.map((s) => [s.name, s.description]),
            [
                ["only-a", "a"],
                ["shared", "from b"],
            ],
        );
    });

    test("a malformed skill fails discovery loudly instead of vanishing", async () => {
        const root = join(workDir, "broken-root");
        mkdirSync(join(root, "bad"), { recursive: true });
        writeFileSync(join(root, "bad", "SKILL.md"), "no frontmatter");
        await assert.rejects(discoverSkills(root), (e: unknown) => e instanceof SkillParseError && e.code === "missing_frontmatter");
    });
});

describe("SkillCatalog", () => {
    const inline = (name: string, description = `about ${name}`, instructions = `do ${name}`) => ({
        name,
        description,
        instructions,
        resources: [],
    });

    test("lists metadata sorted by name and gets a skill by name", () => {
        const cat = new SkillCatalog([inline("zeta"), inline("alpha")]);
        assert.equal(cat.size, 2);
        assert.deepEqual(
            cat.list().map((s) => s.name),
            ["alpha", "zeta"],
        );
        assert.ok(!("instructions" in cat.list()[0]!));
        assert.equal(cat.get("zeta")?.instructions, "do zeta");
        assert.equal(cat.get("nope"), undefined);
        assert.ok(cat.has("alpha"));
    });

    test("duplicate names are refused", () => {
        assert.throws(
            () => new SkillCatalog([inline("a"), inline("a")]),
            (e: unknown) => e instanceof SkillParseError && e.code === "duplicate_skill",
        );
    });

    test("fromDirectories + readResource stay inside the skill folder", async () => {
        const cat = await SkillCatalog.fromDirectories(fixtures);
        assert.deepEqual(
            cat.list().map((s) => s.name),
            ["brand-voice", "pdf"],
        );
        assert.match(await cat.readResource("pdf", "references/forms.md"), /snake_case/);
        for (const bad of ["../brand-voice/SKILL.md", "/etc/passwd", "references/../../brand-voice/SKILL.md", "."]) {
            await assert.rejects(
                cat.readResource("pdf", bad),
                (e: unknown) => e instanceof SkillParseError && e.code === "outside_skill",
                bad,
            );
        }
        await assert.rejects(cat.readResource("nope", "x"), (e: unknown) => e instanceof SkillParseError && e.code === "unknown_skill");
        const inlineCat = new SkillCatalog([inline("mem")]);
        await assert.rejects(inlineCat.readResource("mem", "x"), (e: unknown) => e instanceof SkillParseError && e.code === "no_resources");
    });
});

describe("renderSkillsPrompt", () => {
    test("empty list renders nothing", () => {
        assert.equal(renderSkillsPrompt([]), "");
    });

    test("default intro, XML escaping, and intro overrides", () => {
        const skills = [{ name: "a", description: "x < y & z" }];
        assert.equal(
            renderSkillsPrompt(skills),
            `${DEFAULT_SKILLS_INTRO}\n\n<available_skills>\n  <skill>\n    <name>a</name>\n    <description>x &lt; y &amp; z</description>\n  </skill>\n</available_skills>`,
        );
        assert.ok(renderSkillsPrompt(skills, { intro: null }).startsWith("<available_skills>"));
        assert.ok(renderSkillsPrompt(skills, { intro: "Custom." }).startsWith("Custom.\n\n<available_skills>"));
    });
});

describe("skill node types (graphs as data)", () => {
    const source = new SkillCatalog([
        { name: "brand-voice", description: "Voice.", instructions: "Short sentences.", resources: [] },
    ]);

    test("a `skill` node writes the instructions to a channel; `skills` writes the catalog prompt", async () => {
        const spec: GraphSpec = {
            name: "briefed",
            channels: { system: {}, catalog: {} },
            nodes: [
                { id: "brief", type: "skill", config: { skill: "brand-voice", to: "system" } },
                { id: "menu", type: "skills", config: { to: "catalog", intro: null } },
            ],
            edges: [
                { from: START, to: "brief" },
                { from: "brief", to: "menu" },
                { from: "menu", to: END },
            ],
        };
        const g = fromSpec(spec, skillNodes(source)).compile();
        const result = await run(g, {}, { threadId: "t1" });
        assert.equal(result.status, "done");
        assert.equal(result.state?.system, "Short sentences.");
        assert.match(String(result.state?.catalog), /^<available_skills>/);
    });

    test("an unknown skill fails at build time", () => {
        const spec: GraphSpec = {
            name: "bad",
            channels: { instructions: {} },
            nodes: [{ id: "brief", type: "skill", config: { skill: "missing" } }],
            edges: [
                { from: START, to: "brief" },
                { from: "brief", to: END },
            ],
        };
        assert.throws(() => fromSpec(spec, skillNodes(source)), /does not hold/);
        assert.throws(
            () => fromSpec({ ...spec, nodes: [{ id: "brief", type: "skill", config: {} }] }, skillNodes(source)),
            /config\.skill/,
        );
    });
});
