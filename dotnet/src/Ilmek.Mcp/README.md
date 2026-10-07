# Ilmek.Mcp

Model Context Protocol servers as [ilmek](https://github.com/AimTune/ilmek)
tools, resources and skills — every call journaled, so a pause/resume never
re-invokes a remote tool. No SDK dependency: `IMcpClient` is a six-method
interface you implement over the official `ModelContextProtocol` client in a
few lines (see the docs), or over a fake in tests.

```csharp
using Ilmek.Mcp;

var github = await McpToolbox.ConnectAsync(client, new() { Name = "github" });
github.Tools();                                                          // github__search, …
var hits = await github.CallAsync(ctx, "github__search",
    new Dictionary<string, object?> { ["q"] = "ilmek" });                // once across resumes
var skills = await McpSkills.FromPromptsAsync(github);                   // its prompts, as skills
```

`McpNodes.Registry(toolboxes)` adds `mcp_tool` and `mcp_resource` node types so
a stored graph spec can call a server by name. ilmek stays LLM-agnostic: nothing
here calls a model. Byte-identical to `@ilmek/mcp` (shared fixture in
`conformance/mcp`). Docs: <https://ilmek.aimtune.dev/mcp>.
