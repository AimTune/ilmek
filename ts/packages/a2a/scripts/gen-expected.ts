// Regenerate conformance/a2a/expected.json from the TypeScript reference
// (conformance/a2a/README.md):
//
//   node scripts/gen-expected.ts          # dry run — exit 1 if it would change
//   node scripts/gen-expected.ts --write  # rewrite expected.json in place

import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { A2aAgent, A2aError } from "../src/index.ts";
import { conformanceDir, FakeA2aTransport } from "../test/fake.ts";

const WRITE = process.argv.includes("--write");
const target = join(conformanceDir, "expected.json");

const transport = new FakeA2aTransport();
const agent = await A2aAgent.connect(transport, { mintId: () => "m-x" });
const results: Record<string, unknown> = {};
for (const text of Object.keys(transport.script.sends)) {
    const { task: _task, ...rest } = await agent.invoke(text);
    results[text] = rest;
}
let error: unknown;
try {
    await agent.getTask("task-nope");
} catch (err) {
    error = err instanceof A2aError ? { code: err.code, message: err.message } : String(err);
}

const next = JSON.stringify({ name: agent.name, results, error }, null, 2) + "\n";
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
