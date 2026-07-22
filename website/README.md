# ilmek docs site

The documentation site for [ilmek](https://github.com/AimTune/ilmek), built with
[Docusaurus](https://docusaurus.io/) and deployed to
[ilmek.aimtune.dev](https://ilmek.aimtune.dev).

This is a standalone project — it is **not** part of the `ts/` pnpm workspace, so
it installs and builds on its own.

## Local development

```bash
cd website
pnpm install
pnpm start          # dev server with hot reload at http://localhost:3000
```

## Build

```bash
pnpm build          # static site into ./build
pnpm serve          # preview the production build locally
```

## Deploy

Pushing to `main` triggers `.github/workflows/deploy.yml`, which builds this
folder and publishes `build/` to GitHub Pages. The custom domain is set by
`static/CNAME`.

## Structure

```
docs/                content — one folder per sidebar category
  model/             the execution model (state, graph, supersteps, journal, interrupts)
  control-flow/      send, command, retry
  streaming/         events, projection modes, tokens & cancellation
  checkpointers/     overview, sqlite, postgres
  reference/         spec, conformance, versioning
src/pages/index.tsx  the landing page
sidebars.ts          sidebar tree
docusaurus.config.ts site config (url, navbar, footer, theme)
```

Content is derived from the repository's [MODEL.md](../MODEL.md) (the normative
spec) and [README.md](../README.md). When the spec changes, update the matching
docs page.
