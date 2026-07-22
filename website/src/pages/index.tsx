import type { ReactNode } from "react";
import clsx from "clsx";
import Link from "@docusaurus/Link";
import useDocusaurusContext from "@docusaurus/useDocusaurusContext";
import Layout from "@theme/Layout";
import Heading from "@theme/Heading";
import CodeBlock from "@theme/CodeBlock";

import styles from "./index.module.css";

const features = [
  {
    icon: "🪡",
    title: "Durable human-in-the-loop",
    body: "A node that pauses for a human resumes without re-running the work above it. An interrupt is just a step whose value comes from a person — same replay rules, no special-casing.",
  },
  {
    icon: "📓",
    title: "The journal",
    body: "Wrap every side effect in ctx.step(...). Its recorded result replays on resume instead of running again. Charge a card once, retry the flaky call after it — no double charge.",
  },
  {
    icon: "🔁",
    title: "Safe retries",
    body: "Because completed steps are journaled, retry is free of double-effects. A node that charges then hits a flaky API retries the API alone — the charge replays from the journal.",
  },
  {
    icon: "🧩",
    title: "Graphs are data",
    body: "Every graph round-trips to a serializable spec (fromSpec / toSpec). A stored spec never carries executable text — the foundation for a drag-and-drop builder the engine knows nothing about.",
  },
  {
    icon: "📡",
    title: "One stream, many views",
    body: "A run is one canonical event stream with a monotonic seq and namespace path. Project it into values, updates, custom, messages, and debug modes — no second pass.",
  },
  {
    icon: "🌐",
    title: "Two implementations, one spec",
    body: "TypeScript (reference) and .NET, both green against the same conformance list, printing identical demo output. MODEL.md is the normative spec; the code reproduces it exactly.",
  },
];

function HomepageHeader() {
  return (
    <header className={clsx("hero", styles.heroBanner)}>
      <div className="container">
        <span className={styles.heroBadge}>Open source · MIT</span>
        <Heading as="h1" className={styles.heroTitle}>
          ilmek
        </Heading>
        <p className={styles.heroTagline}>
          An agent graph runtime — state, nodes, edges, checkpointed memory, and{" "}
          <strong>durable</strong> human-in-the-loop. A paused node resumes on top
          of its work instead of redoing it.
        </p>
        <div className={styles.heroButtons}>
          <Link
            className="button button--secondary button--lg"
            to="/getting-started"
          >
            Get started → 5 min
          </Link>
          <Link
            className="button button--outline button--lg"
            style={{ color: "white", borderColor: "white" }}
            to="/concepts"
          >
            The one idea →
          </Link>
        </div>
        <div className={styles.codeBlock}>
          <CodeBlock language="ts">
            {`import { graph, channel, START, END, run, resume, InMemoryCheckpointer } from "@ilmek/core";

const g = graph("checkout")
  .node("checkout", async (state, ctx) => {
    const order = await ctx.step("create_order", () => Orders.create());  // once, ever
    const ok = await ctx.interrupt<string>({ question: "Charge?" });
    await ctx.step("charge", () => Payments.charge(order, ok));
    return { log: ["done"] };
  })
  .edge(START, "checkout").edge("checkout", END)
  .compile();`}
          </CodeBlock>
        </div>
      </div>
    </header>
  );
}

function HomepageFeatures() {
  return (
    <section className={styles.features}>
      <div className="container">
        <Heading as="h2" style={{ textAlign: "center", marginBottom: "0.5rem" }}>
          Why this exists
        </Heading>
        <p
          style={{
            textAlign: "center",
            color: "var(--ifm-color-emphasis-700)",
            maxWidth: 720,
            margin: "0 auto 2rem",
          }}
        >
          Every graph engine that supports human-in-the-loop resumes a paused node
          by re-executing it from the top — so everything above the pause runs
          again. ilmek makes that the engine's problem, not yours.
        </p>
        <div className={styles.featureGrid}>
          {features.map((f) => (
            <div key={f.title} className={styles.featureCard}>
              <span className={styles.featureIcon}>{f.icon}</span>
              <h3>{f.title}</h3>
              <p>{f.body}</p>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

export default function Home(): ReactNode {
  const { siteConfig } = useDocusaurusContext();
  return (
    <Layout
      title={siteConfig.title}
      description="ilmek — an agent graph runtime with durable, journaled human-in-the-loop, built on a language-neutral spec."
    >
      <HomepageHeader />
      <main>
        <HomepageFeatures />
      </main>
    </Layout>
  );
}
