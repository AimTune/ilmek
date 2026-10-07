/**
 * The official `@modelcontextprotocol/sdk` `Client` must pass as an
 * `McpClientLike` without a cast, under `exactOptionalPropertyTypes` — the
 * setting this workspace (and many consumers) build with. The SDK types its
 * optional fields `description?: string | undefined`; a port that said
 * `description?: string` rejected them. Type stripping does not typecheck, so
 * this test compiles the fixture in test/types/ with the TypeScript API.
 */

import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const ts = require("typescript") as typeof import("typescript");

test("the SDK Client is assignable to McpClientLike with no cast (exactOptionalPropertyTypes)", () => {
    const configPath = join(here, "..", "tsconfig.test.json");
    const parsed = ts.getParsedCommandLineOfConfigFile(configPath, {}, {
        ...ts.sys,
        onUnRecoverableConfigFileDiagnostic: (d) => assert.fail(ts.flattenDiagnosticMessageText(d.messageText, "\n")),
    });
    assert.ok(parsed, "tsconfig.test.json parses");
    assert.equal(parsed.options.exactOptionalPropertyTypes, true, "the fixture must compile under exactOptionalPropertyTypes");

    const fixture = join(here, "types", "sdk-client.ts");
    const program = ts.createProgram({ rootNames: [fixture], options: { ...parsed.options, noEmit: true } });
    const diagnostics = ts.getPreEmitDiagnostics(program).map((d) => {
        const where = d.file ? `${d.file.fileName}:${d.file.getLineAndCharacterOfPosition(d.start ?? 0).line + 1}` : "";
        return `${where} ${ts.flattenDiagnosticMessageText(d.messageText, "\n")}`;
    });
    assert.deepEqual(diagnostics, []);
});
