# @ilmek/mcp

Model Context Protocol servers as [ilmek](https://github.com/AimTune/ilmek)
tools, resources and skills — every call journaled, so a pause/resume never
re-invokes a remote tool. Zero dependencies: the client port is duck-typed to
the official `@modelcontextprotocol/sdk` `Client`, so a connected one passes
as-is.

```sh
npm install @ilmek/core @ilmek/mcp @modelcontextprotocol/sdk
```

```ts
import { McpToolbox, mcpSkills } from "@ilmek/mcp";

const github = await McpToolbox.connect(client, { name: "github" });
github.tools();                                                          // [{ name: "github__search", inputSchema, … }]
const hits = await github.call(ctx, "github__search", { q: "ilmek" });   // once across resumes
const skills = await mcpSkills(github);                                  // its prompts, as skills
```

`mcpNodes({ github })` adds `mcp_tool` and `mcp_resource` node types so a stored
graph spec can call a server by name. ilmek stays LLM-agnostic: nothing here
calls a model. Docs: <https://ilmek.aimtune.dev/mcp>.
