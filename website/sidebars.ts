import type { SidebarsConfig } from "@docusaurus/plugin-content-docs";

const sidebars: SidebarsConfig = {
  docs: [
    "intro",
    "getting-started",
    "concepts",
    {
      type: "category",
      label: "Execution model",
      collapsed: false,
      items: [
        "model/state-and-channels",
        "model/graph",
        "model/supersteps",
        "model/journal",
        "model/interrupts",
      ],
    },
    {
      type: "category",
      label: "Control flow",
      link: { type: "doc", id: "control-flow/overview" },
      items: [
        "control-flow/send",
        "control-flow/command",
        "control-flow/retry",
      ],
    },
    {
      type: "category",
      label: "Streaming",
      link: { type: "doc", id: "streaming/overview" },
      items: [
        "streaming/projection-modes",
        "streaming/tokens-and-cancellation",
      ],
    },
    {
      type: "category",
      label: "Checkpointers",
      link: { type: "doc", id: "checkpointers/overview" },
      items: ["checkpointers/sqlite", "checkpointers/postgres"],
    },
    "graphs-as-data",
    {
      type: "category",
      label: "Reference",
      items: ["reference/spec", "reference/conformance", "reference/versioning"],
    },
  ],
};

export default sidebars;
