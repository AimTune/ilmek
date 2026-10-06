# @ilmek/skills

Agent Skills for [ilmek](https://github.com/AimTune/ilmek): read `SKILL.md`
folders and expose them to a model **progressively** — names and descriptions
first, instructions on demand, bundled files only when the instructions point
at them. Zero dependencies; byte-identical to `Ilmek.Skills`.

```sh
npm install @ilmek/core @ilmek/skills
```

```ts
import { SkillCatalog, renderSkillsPrompt } from "@ilmek/skills";

const skills = await SkillCatalog.fromDirectories(["./skills"]);
const system = base + "\n\n" + renderSkillsPrompt(skills.list());       // level 1
const pdf = skills.get("pdf")?.instructions;                              // level 2
const form = await skills.readResource("pdf", "references/forms.md");     // level 3
```

A skill is a folder with a `SKILL.md` at its root — YAML frontmatter (`name`,
`description`, optional `license`, `allowed-tools`, `compatibility`, `metadata`)
followed by markdown instructions. `skillNodes(catalog)` adds `skill` and
`skills` node types so a stored graph spec can load one by name.

ilmek stays LLM-agnostic: nothing here calls a model. A host (mekik, your own
agent loop) decides how the catalog reaches the prompt and how a `load_skill`
tool hands instructions back. Docs: <https://ilmek.aimtune.dev/skills>.
