# MCP conformance

[`server.json`](server.json) scripts an MCP server — what `tools/list`,
`tools/call`, `resources/*` and `prompts/*` return. `@ilmek/mcp` and
`Ilmek.Mcp` each build a fake client from it, connect a toolbox named `github`
with the default prefix, and must reduce it to [`expected.json`](expected.json)
as canonical JSON:

| field | pins |
|---|---|
| `tools` | the exposed tool list: `<server>__<tool>` names, `remoteName`, descriptions, schemas passed through |
| `calls` | every tool's **normalized** result — `text` joined from text blocks and embedded text resources, `structured`, `isError`, raw `content` |
| `resource` | the text read from `file:///README.md` |
| `skills` | the prompts as skills: coerced names, descriptions, fetched instructions for argument-less prompts and the argument note for the others, `metadata` |

`expected.json` is generated once by the TypeScript reference
(`node ts/packages/mcp/scripts/gen-expected.ts --write`), hand-reviewed and
committed; the .NET suite reruns it unchanged. Journaling (a `call` runs once
across an interrupt/resume) and the `mcp_tool` / `mcp_resource` node types are
asserted inline in each language, since they exercise code shapes, not data.
