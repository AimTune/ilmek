/**
 * `@ilmek/mcp` — the toolbox over a duck-typed MCP client, journaled calls,
 * prompts as skills, and the `mcp_tool` / `mcp_resource` node types. The
 * scripted server in conformance/mcp is the cross-language fixture: this suite
 * and the .NET one must reduce it to the same JSON.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

import { channel, END, fromSpec, graph, InMemoryCheckpointer, resume, run, START, type GraphSpec } from "@ilmek/core";

import { McpToolbox, mcpNodes, mcpSkills, normalizeToolResult, promptText, toSkillName } from "../src/index.ts";
import { conformanceDir, FakeMcpClient } from "./fake.ts";

const canonical = (v: unknown): string => JSON.stringify(sortKeys(v));
function sortKeys(v: unknown): unknown {
    if (Array.isArray(v)) return v.map(sortKeys);
    if (v !== null && typeof v === "object") {
        return Object.fromEntries(
            Object.keys(v as Record<string, unknown>)
                .sort()
                .map((k) => [k, sortKeys((v as Record<string, unknown>)[k])]),
        );
    }
    return v;
}

const connect = (client = new FakeMcpClient(), opts: { prefix?: string; allow?: string[] } = {}) =>
    McpToolbox.connect(client, { name: "github", ...opts });

describe("conformance/mcp (shared with .NET)", () => {
    test("the scripted server reduces to expected.json", async () => {
        const expected = JSON.parse(readFileSync(join(conformanceDir, "expected.json"), "utf8")) as Record<string, unknown>;
        const toolbox = await connect();
        const calls: Record<string, unknown> = {};
        for (const t of toolbox.tools()) calls[t.name] = await toolbox.invoke(t.name, {});
        const actual = {
            tools: toolbox.tools(),
            calls,
            resource: await toolbox.fetchResource("file:///README.md"),
            skills: await mcpSkills(toolbox),
        };
        assert.equal(canonical(actual), canonical(expected));
    });
});

describe("McpToolbox", () => {
    test("connect lists once; tools are prefixed and keep their remote name", async () => {
        const client = new FakeMcpClient();
        const toolbox = await connect(client);
        assert.deepEqual(
            toolbox.tools().map((t) => [t.name, t.remoteName]),
            [
                ["github__search", "search"],
                ["github__get_file", "get_file"],
                ["github__internal_admin", "internal_admin"],
            ],
        );
        assert.equal(toolbox.tool("github__search")?.description, "Search repositories.");
        assert.deepEqual(toolbox.tool("github__get_file")?.inputSchema, { type: "object", properties: { path: { type: "string" } } });
        assert.equal(toolbox.tool("search"), undefined);
    });

    test("prefix and allow options", async () => {
        const bare = await connect(new FakeMcpClient(), { prefix: "", allow: ["search", "get_file"] });
        assert.deepEqual(
            bare.tools().map((t) => t.name),
            ["search", "get_file"],
        );
        const custom = await connect(new FakeMcpClient(), { prefix: "gh_" });
        assert.equal(custom.tools()[0]!.name, "gh_search");
        await assert.rejects(McpToolbox.connect(new FakeMcpClient(), { name: "" }), /needs a server name/);
    });

    test("invoke dispatches to the remote name and normalizes the result", async () => {
        const client = new FakeMcpClient();
        const toolbox = await connect(client);
        const result = await toolbox.invoke("github__search", { q: "ilmek" });
        assert.deepEqual(client.calls, [{ name: "search", arguments: { q: "ilmek" } }]);
        assert.equal(result.text, "3 results\nilmek, mekik, chativa");
        assert.deepEqual(result.structured, { count: 3 });
        assert.equal(result.isError, false);
        assert.equal(result.content.length, 2);

        const file = await toolbox.invoke("github__get_file");
        assert.equal(file.text, "# README", "embedded text resources count as text; images do not");
        assert.equal(file.structured, undefined);

        const denied = await toolbox.invoke("github__internal_admin");
        assert.equal(denied.isError, true);
        await assert.rejects(toolbox.invoke("github__nope"), /has no tool "github__nope"/);
    });

    test("call is journaled: once across an interrupt/resume, and distinct keys per tool", async () => {
        const client = new FakeMcpClient();
        const toolbox = await connect(client);
        const g = graph("mcp")
            .channel("log", channel.append<string>())
            .node("work", async (_s, ctx) => {
                const hits = await toolbox.call(ctx, "github__search", { q: "ilmek" });
                const file = await toolbox.call(ctx, "github__get_file", { path: "README.md" });
                const ok = await ctx.interrupt<string>({ q: "proceed?" });
                return { log: [hits.text.split("\n")[0]!, file.text, ok] };
            })
            .edge(START, "work")
            .edge("work", END)
            .compile();
        const opts = { threadId: "t-1", checkpointer: new InMemoryCheckpointer() };
        const paused = await run(g, {}, opts);
        assert.equal(paused.status, "interrupted");
        const done = await resume(g, "yes", opts);
        assert.equal(done.status, "done");
        assert.deepEqual(done.state?.log, ["3 results", "# README", "yes"]);
        assert.equal(client.calls.length, 2, "the remote tools ran once each, not again on the resume pass");
        assert.deepEqual(client.calls.map((c) => c.name), ["search", "get_file"]);
    });

    test("the key option separates two calls of the same tool", async () => {
        const client = new FakeMcpClient();
        const toolbox = await connect(client);
        const g = graph("keyed")
            .channel("log", channel.append<string>())
            .node("work", async (_s, ctx) => {
                const a = await toolbox.call(ctx, "github__search", { q: "a" }, { key: "search:a" });
                const b = await toolbox.call(ctx, "github__search", { q: "b" }, { key: "search:b" });
                await ctx.interrupt({});
                return { log: [String(a.text.length + b.text.length)] };
            })
            .edge(START, "work")
            .edge("work", END)
            .compile();
        const opts = { threadId: "t-k", checkpointer: new InMemoryCheckpointer() };
        await run(g, {}, opts);
        await resume(g, "ok", opts);
        assert.deepEqual(client.calls.map((c) => c.arguments), [{ q: "a" }, { q: "b" }]);
    });

    test("resources and prompts: listed, read and fetched once", async () => {
        const client = new FakeMcpClient();
        const toolbox = await connect(client);
        assert.deepEqual((await toolbox.resources()).map((r) => r.uri), ["file:///README.md"]);
        assert.deepEqual((await toolbox.prompts()).map((p) => p.name), ["Code Review", "summarize"]);
        assert.equal(await toolbox.fetchResource("file:///README.md"), "# README\n\nHello.");
        assert.equal(await toolbox.fetchPrompt("Code Review"), "Review the diff below.\n\nFocus on correctness first.");

        let reads = 0;
        const counting = new FakeMcpClient();
        const origRead = counting.readResource.bind(counting);
        counting.readResource = async (p) => {
            reads++;
            return origRead(p);
        };
        const tb = await connect(counting);
        const g = graph("res")
            .channel("out", channel.lastWrite<string>(""))
            .node("read", async (_s, ctx) => {
                const text = await tb.readResource(ctx, "file:///README.md");
                const prompt = await tb.prompt(ctx, "Code Review");
                await ctx.interrupt({});
                return { out: `${text}|${prompt.length}` };
            })
            .edge(START, "read")
            .edge("read", END)
            .compile();
        const opts = { threadId: "t-2", checkpointer: new InMemoryCheckpointer() };
        await run(g, {}, opts);
        const done = await resume(g, "go", opts);
        assert.equal(done.state?.out, "# README\n\nHello.|51");
        assert.equal(reads, 1);
    });

    test("a client without resources or prompts reports none, and fetching throws", async () => {
        const minimal = { listTools: async () => ({ tools: [] }), callTool: async () => ({ content: [] }) };
        const toolbox = await McpToolbox.connect(minimal, { name: "min" });
        assert.deepEqual(toolbox.tools(), []);
        assert.deepEqual(await toolbox.resources(), []);
        assert.deepEqual(await toolbox.prompts(), []);
        await assert.rejects(toolbox.fetchResource("x"), /has no resources/);
        await assert.rejects(toolbox.fetchPrompt("x"), /has no prompts/);
        assert.deepEqual(await mcpSkills(toolbox), []);
    });
});

describe("normalization", () => {
    test("normalizeToolResult joins text and embedded text resources, keeps structured and isError", () => {
        const r = normalizeToolResult({
            content: [
                { type: "text", text: "a" },
                { type: "image", data: "x", mimeType: "image/png" },
                { type: "resource", resource: { uri: "u", text: "b" } },
                { type: "resource", resource: { uri: "v", blob: "..." } },
            ],
            structuredContent: { k: 1 },
            isError: true,
        });
        assert.equal(r.text, "a\nb");
        assert.deepEqual(r.structured, { k: 1 });
        assert.equal(r.isError, true);
        assert.equal(r.content.length, 4);
        assert.deepEqual(normalizeToolResult({}), { text: "", isError: false, content: [] });
    });

    test("promptText renders text blocks per message, skipping empty ones", () => {
        assert.equal(
            promptText({ messages: [{ role: "user", content: { type: "text", text: "hi" } }, { role: "user", content: { type: "image", data: "x" } }, { role: "assistant", content: [{ type: "text", text: "a" }, { type: "text", text: "b" }] }] }),
            "hi\n\na\nb",
        );
    });

    test("toSkillName coerces to the skill name rule", () => {
        assert.equal(toSkillName("github-Code Review"), "github-code-review");
        assert.equal(toSkillName("  ***  "), "prompt");
        assert.equal(toSkillName("a".repeat(70)).length, 64);
        assert.equal(toSkillName("x".repeat(63) + "-y"), "x".repeat(63));
    });
});

describe("mcpSkills", () => {
    test("argument-less prompts are fetched; prompts with required arguments get the argument note", async () => {
        const skills = await mcpSkills(await connect());
        assert.deepEqual(
            skills.map((s) => s.name),
            ["github-code-review", "github-summarize"],
        );
        const review = skills[0]!;
        assert.equal(review.description, "Review a pull request in the house style.");
        assert.equal(review.instructions, "Review the diff below.\n\nFocus on correctness first.");
        assert.deepEqual(review.metadata, { server: "github", prompt: "Code Review" });
        const summarize = skills[1]!;
        assert.equal(summarize.description, 'The "summarize" prompt of MCP server "github".');
        assert.match(summarize.instructions, /- url \(required\): What to summarize\n- tone$/);
        assert.deepEqual(summarize.metadata, { server: "github", prompt: "summarize", arguments: "url,tone" });
        assert.deepEqual(summarize.resources, []);
        assert.equal((await mcpSkills(await connect(), { prefix: "" }))[0]!.name, "code-review");
    });
});

describe("node types (graphs as data)", () => {
    test("mcp_tool merges fixed and channel arguments, writes text or the whole result; mcp_resource reads text", async () => {
        const client = new FakeMcpClient();
        const toolboxes = { github: await connect(client) };
        const spec: GraphSpec = {
            name: "mcp-graph",
            channels: { query: {}, hits: {}, file: {}, readme: {} },
            nodes: [
                { id: "search", type: "mcp_tool", config: { server: "github", tool: "github__search", arguments: { q: "default", page: 1 }, argumentsFrom: "query", to: "hits", text: true } },
                { id: "file", type: "mcp_tool", config: { server: "github", tool: "github__get_file", to: "file" } },
                { id: "readme", type: "mcp_resource", config: { server: "github", uri: "file:///README.md", to: "readme" } },
            ],
            edges: [
                { from: START, to: "search" },
                { from: "search", to: "file" },
                { from: "file", to: "readme" },
                { from: "readme", to: END },
            ],
        };
        const g = fromSpec(spec, mcpNodes(toolboxes)).compile();
        const result = await run(g, { query: { q: "ilmek" } }, { threadId: "t-3" });
        assert.equal(result.status, "done");
        assert.equal(result.state?.hits, "3 results\nilmek, mekik, chativa");
        assert.deepEqual(client.calls[0], { name: "search", arguments: { q: "ilmek", page: 1 } });
        assert.equal((result.state?.file as { text: string }).text, "# README");
        assert.equal(result.state?.readme, "# README\n\nHello.");
    });

    test("bad references fail at build time", async () => {
        const registry = mcpNodes({ github: await connect() });
        const spec = (config: Record<string, unknown>, type = "mcp_tool"): GraphSpec => ({
            name: "bad",
            channels: { result: {} },
            nodes: [{ id: "n", type, config }],
            edges: [
                { from: START, to: "n" },
                { from: "n", to: END },
            ],
        });
        assert.throws(() => fromSpec(spec({ server: "gitlab", tool: "x" }), registry), /known: \["github"\]/);
        assert.throws(() => fromSpec(spec({ server: "github", tool: "search" }), registry), /references tool "search"/);
        assert.throws(() => fromSpec(spec({ tool: "github__search" }), registry), /config\.server/);
        assert.throws(() => fromSpec(spec({ server: "github" }, "mcp_resource"), registry), /config\.uri/);
        assert.throws(() => fromSpec(spec({ server: "github", tool: "github__search", to: 5 }), registry), /config\.to/);
    });
});
