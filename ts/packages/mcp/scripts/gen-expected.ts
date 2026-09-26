// Regenerate conformance/mcp/expected.json from the TypeScript reference
// (conformance/mcp/README.md). Run after an intentional change to the toolbox
// naming, result normalization or prompt→skill mapping:
//
//   node scripts/gen-expected.ts          # dry run — exit 1 if it would change
//   node scripts/gen-expected.ts --write  # rewrite expected.json in place

import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { McpToolbox, mcpSkills } from "../src/index.ts";
import { conformanceDir, FakeMcpClient } from "../test/fake.ts";

const WRITE = process.argv.includes("--write");
const target = join(conformanceDir, "expected.json");

const client = new FakeMcpClient();
const toolbox = await McpToolbox.connect(client, { name: "github" });
const calls: Record<string, unknown> = {};
for (const t of toolbox.tools()) calls[t.name] = await toolbox.invoke(t.name, {});

const expected = {
    tools: toolbox.tools(),
    calls,
    resource: await toolbox.fetchResource("file:///README.md"),
    skills: await mcpSkills(toolbox),
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
