import { themes as prismThemes } from "prism-react-renderer";
import type { Config } from "@docusaurus/types";
import type * as Preset from "@docusaurus/preset-classic";

const GITHUB_REPO = "https://github.com/AimTune/ilmek";
const SITE_URL = "https://ilmek.aimtune.dev";
const BASE_URL = "/";

const config: Config = {
  title: "ilmek",
  tagline:
    "An agent graph runtime: state, nodes, edges, checkpointed memory, and durable human-in-the-loop.",
  favicon: "img/favicon.svg",

  future: {
    v4: true,
    faster: true,
  },

  url: SITE_URL,
  baseUrl: BASE_URL,

  organizationName: "AimTune",
  projectName: "ilmek",
  trailingSlash: false,

  onBrokenLinks: "warn",
  markdown: {
    // Parse .md as CommonMark unless a file opts into MDX (JSX/.mdx). Keeps
    // `<TState>`, `{ node: update }` etc. in prose/tables from being read as JSX.
    format: "detect",
    hooks: {
      onBrokenMarkdownLinks: "warn",
      onBrokenMarkdownImages: "warn",
    },
  },

  i18n: {
    defaultLocale: "en",
    locales: ["en"],
  },

  presets: [
    [
      "classic",
      {
        docs: {
          sidebarPath: "./sidebars.ts",
          routeBasePath: "/",
          editUrl: `${GITHUB_REPO}/edit/main/website/`,
        },
        blog: false,
        theme: {
          customCss: "./src/css/custom.css",
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    image: "img/ilmek-social-card.png",
    colorMode: {
      respectPrefersColorScheme: true,
    },
    navbar: {
      title: "ilmek",
      logo: {
        alt: "ilmek logo",
        src: "img/logo.svg",
      },
      items: [
        {
          type: "docSidebar",
          sidebarId: "docs",
          position: "left",
          label: "Docs",
        },
        {
          to: "/concepts",
          position: "left",
          label: "Concepts",
        },
        {
          to: "/checkpointers/overview",
          position: "left",
          label: "Checkpointers",
        },
        {
          to: "/reference/spec",
          position: "left",
          label: "Spec",
        },
        {
          href: GITHUB_REPO,
          label: "GitHub",
          position: "right",
        },
      ],
    },
    footer: {
      style: "dark",
      links: [
        {
          title: "Docs",
          items: [
            { label: "Getting started", to: "/getting-started" },
            { label: "Concepts", to: "/concepts" },
            { label: "The journal", to: "/model/journal" },
            { label: "Interrupts", to: "/model/interrupts" },
          ],
        },
        {
          title: "Reference",
          items: [
            { label: "Checkpointers", to: "/checkpointers/overview" },
            { label: "Streaming", to: "/streaming/overview" },
            { label: "Graphs as data", to: "/graphs-as-data" },
            { label: "Spec (MODEL.md)", to: "/reference/spec" },
          ],
        },
        {
          title: "More",
          items: [
            { label: "GitHub", href: GITHUB_REPO },
            { label: "Issues", href: `${GITHUB_REPO}/issues` },
            { label: "npm — @ilmek/core", href: "https://www.npmjs.com/package/@ilmek/core" },
          ],
        },
      ],
      copyright: `© ${new Date().getFullYear()} Hamza Agar — ilmek is MIT licensed.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ["bash", "json", "diff", "csharp"],
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
