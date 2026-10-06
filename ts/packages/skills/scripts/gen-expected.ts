// Regenerate conformance/skills/expected.json from the TypeScript reference
// reader (conformance/skills/README.md). Run after an intentional change to
// the SKILL.md format or the prompt renderer:
//
//   node scripts/gen-expected.ts          # dry run — exit 1 if it would change
//   node scripts/gen-expected.ts --write  # rewrite expected.json in place
//
// The .NET suite then reruns the committed file unchanged; if this script
// changes it, that IS the cross-language change under review.

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { discoverSkills } from "../src/loader.ts";
import { renderSkillsPrompt } from "../src/prompt.ts";
import { toMetadata } from "../src/skill.ts";

const WRITE = process.argv.includes("--write");
const root = join(dirname(fileURLToPath(import.meta.url)), "../../../../conformance/skills");
const target = join(root, "expected.json");

const skills = await discoverSkills(root);
const expected = {
    skills: skills.map(({ path: _path, ...rest }) => rest),
    prompt: renderSkillsPrompt(skills.map(toMetadata)),
};

const next = JSON.stringify(expected, null, 2) + "\n";
let current = "";
try {
    current = readFileSync(target, "utf8");
} catch {
    // first generation
}

if (current === next) {
    console.log("expected.json up to date");
} else if (WRITE) {
    writeFileSync(target, next);
    console.log("expected.json rewritten");
} else {
    console.log("expected.json would change — rerun with --write");
    process.exit(1);
}
