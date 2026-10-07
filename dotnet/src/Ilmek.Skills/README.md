# Ilmek.Skills

Agent Skills for [ilmek](https://github.com/AimTune/ilmek): read `SKILL.md`
folders and expose them to a model **progressively** — names and descriptions
first, instructions on demand, bundled files only when the instructions point
at them. No third-party dependencies; byte-identical to `@ilmek/skills`.

```csharp
using Ilmek.Skills;

var skills = await SkillCatalog.FromDirectoriesAsync("./skills");
var system = basePrompt + "\n\n" + SkillPrompt.Render(skills.List());        // level 1
var pdf = skills.Get("pdf")?.Instructions;                                    // level 2
var form = await skills.ReadResourceAsync("pdf", "references/forms.md");     // level 3
```

A skill is a folder with a `SKILL.md` at its root — YAML frontmatter (`name`,
`description`, optional `license`, `allowed-tools`, `compatibility`, `metadata`)
followed by markdown instructions. `SkillNodes.Registry(catalog)` adds `skill`
and `skills` node types so a stored graph spec can load one by name.

ilmek stays LLM-agnostic: nothing here calls a model. A host (mekik, your own
agent loop) decides how the catalog reaches the prompt and how a `load_skill`
tool hands instructions back. Docs: <https://ilmek.aimtune.dev/skills>.
