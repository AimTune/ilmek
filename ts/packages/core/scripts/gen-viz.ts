// Regenerate conformance/viz/*.mmd from the TypeScript reference
// (conformance/viz/README.md). Run after an intentional change to toMermaid:
//
//   node scripts/gen-viz.ts          # dry run — exit 1 if any file would change
//   node scripts/gen-viz.ts --write  # rewrite the .mmd files in place

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { toMermaid } from "../src/index.ts";
import { caseOptions, compileCase, loadCase, vizCases, vizDir } from "../test/viz.ts";

const WRITE = process.argv.includes("--write");
let stale = 0;

for (const name of vizCases()) {
    const target = join(vizDir, `${name}.mmd`);
    const c = loadCase(name);
    const next = toMermaid(compileCase(c), caseOptions(c));
    const current = existsSync(target) ? readFileSync(target, "utf8").replace(/\r\n/g, "\n") : "";
    if (current === next) continue;

    stale++;
    if (WRITE) {
        writeFileSync(target, next);
        console.log(`wrote ${name}.mmd`);
    } else {
        console.log(`${name}.mmd is out of date`);
    }
}

if (stale === 0) console.log("conformance/viz is up to date");
else if (!WRITE) process.exit(1);
