# Skills conformance

The skill folders here are the shared input for `@ilmek/skills` and
`Ilmek.Skills`. Each implementation discovers them, parses every `SKILL.md`,
renders the level-1 prompt, and compares the result — as canonical JSON — with
[`expected.json`](expected.json). A divergence between the two readers shows up
as a red test in one language, not as a model reading two different catalogs.

| folder | exercises |
|---|---|
| `pdf/` | folded (`>-`) description, `allowed-tools` as a space-separated string, `license`, `compatibility`, a `metadata` mapping with a quoted value and a trailing comment, nested bundled resources |
| `brand-voice/` | a double-quoted description with `&`, `<` and `>` (XML-escaped in the prompt), no optional fields, no resources |

`expected.json` is generated once by the TypeScript reference, hand-reviewed,
and committed; regenerate it only for an intentional format change
(`node ts/packages/skills/scripts/gen-expected.ts --write`).

Invalid documents (missing frontmatter, a bad `name`, a folder whose name does
not match) are asserted inline in each language's suite — they exercise error
codes, not data.
