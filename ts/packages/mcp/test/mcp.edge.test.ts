/**
 * Error paths and hostile responses for `@ilmek/mcp`: a server that throws,
 * reports a tool error, or answers with a malformed payload; journaling under
 * failure and retry; and the node types' build-time validation.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import { channel, END, fromSpec, graph, InMemoryCheckpointer, resume, run, START, type GraphSpec } from "@ilmek/core";

import {
    McpToolbox,
    mcpNodes,
    mcpSkills,
    normalizeToolResult,
    promptText,
    toSkillName,
    type McpCallToolResult,
    type McpClientLike,
    type McpContent,
} from "../src/index.ts";
import { FakeMcpClient } from "./fake.ts";

/** A minimal client whose answers each test scripts by hand. */
function client(over: Partial<McpClientLike> & { calls?: number[] } = {}): McpClientLike & { invoked: string[] } {
    const invoked: string[] = [];
    return {
        invoked,
        listTools: async () => ({ tools: [{ name: "t", inputSchema: { type: "object" } }] }),
        callTool: async (p) => {
            invoked.push(p.name);
            return { content: [{ type: "text", text: "ok" }] };
        },
        ...over,
    } as McpClientLike & { invoked: string[] };
}

describe("McpToolbox — connect", () => {
    test("a toolbox needs a server name", async () => {
        await assert.rejects(McpToolbox.connect(client(), { name: "" }), /needs a server name/);
    });

    test("a failing tools/list rejects connect with the server's error", async () => {
        const c = client({ listTools: async () => Promise.reject(new Error("connection refused")) });
        await assert.rejects(McpToolbox.connect(c, { name: "s" }), /connection refused/);
    });

    test("a tools/list answer without a list advertises no tools", async () => {
        const c = client({ listTools: async () => ({}) as never });
        assert.deepEqual((await McpToolbox.connect(c, { name: "s" })).tools(), []);
    });

    test("a tool without an inputSchema gets an empty object schema", async () => {
        const c = client({ listTools: async () => ({ tools: [{ name: "bare" } as never] }) });
        assert.deepEqual((await McpToolbox.connect(c, { name: "s" })).tool("s__bare")?.inputSchema, { type: "object" });
    });

    test("an allow list naming tools the server does not have exposes only the real ones", async () => {
        const tb = await McpToolbox.connect(new FakeMcpClient(), { name: "gh", allow: ["search", "ghost"] });
        assert.deepEqual(tb.tools().map((t) => t.name), ["gh__search"]);
    });

    test("an empty allow list exposes nothing", async () => {
        const tb = await McpToolbox.connect(new FakeMcpClient(), { name: "gh", allow: [] });
        assert.deepEqual(tb.tools(), []);
    });

    test("a duplicated tool name from the server keeps the last advertisement", async () => {
        const c = client({
            listTools: async () => ({
                tools: [
                    { name: "dup", description: "first", inputSchema: {} },
                    { name: "dup", description: "second", inputSchema: {} },
                ],
            }),
        });
        const tb = await McpToolbox.connect(c, { name: "s" });
        assert.equal(tb.tools().length, 1);
        assert.equal(tb.tool("s__dup")?.description, "second");
    });

    test("tool lookup is by exposed name only: the raw remote name is not callable", async () => {
        const tb = await McpToolbox.connect(new FakeMcpClient(), { name: "gh" });
        assert.equal(tb.tool("search"), undefined);
        await assert.rejects(tb.invoke("search"), /has no tool "search"/);
    });
});

describe("McpToolbox — call failures", () => {
    const g = (tb: McpToolbox, name = "s__t") =>
        graph()
            .channel("out", channel.append<unknown>())
            .node("n", async (_s, ctx) => {
                const r = await tb.call(ctx, name, { q: 1 });
                const answer = await ctx.interrupt<string>();
                return { out: [r.text, r.isError, answer] };
            })
            .edge(START, "n")
            .edge("n", END)
            .compile();

    test("calling an unknown tool rejects (it does not throw synchronously)", async () => {
        const tb = await McpToolbox.connect(client(), { name: "s" });
        const fakeCtx = { step: async () => undefined } as never;
        let threwSync = false;
        let promise: Promise<unknown> | undefined;
        try {
            promise = tb.call(fakeCtx, "s__nope");
        } catch {
            threwSync = true;
        }
        assert.equal(threwSync, false);
        await assert.rejects(promise!, /has no tool "s__nope"/);
    });

    test("a tool that reports isError is journaled like any result: not re-invoked on resume", async () => {
        const c = client({
            callTool: async (p) => {
                c.invoked.push(p.name);
                return { content: [{ type: "text", text: "permission denied" }], isError: true };
            },
        });
        const tb = await McpToolbox.connect(c, { name: "s" });
        const opts = { threadId: "t-iserror", checkpointer: new InMemoryCheckpointer() };

        await run(g(tb), {}, opts);
        const done = await resume(g(tb), "go", opts);
        assert.deepEqual(done.state?.out, ["permission denied", true, "go"]);
        assert.deepEqual(c.invoked, ["t"]);
    });

    test("a transport failure is not journaled: the next run calls the server again", async () => {
        let attempt = 0;
        const c = client({
            callTool: async (p) => {
                c.invoked.push(p.name);
                if (++attempt === 1) throw new Error("ECONNRESET");
                return { content: [{ type: "text", text: "second time lucky" }] };
            },
        });
        const tb = await McpToolbox.connect(c, { name: "s" });
        const opts = { threadId: "t-transport", checkpointer: new InMemoryCheckpointer() };

        const failed = await run(g(tb), {}, opts);
        assert.equal(failed.status, "error");
        assert.match((failed.errors[0]![1] as Error).message, /ECONNRESET/);

        assert.equal((await run(g(tb), {}, opts)).status, "interrupted");
        const done = await resume(g(tb), "ok", opts);
        assert.deepEqual(done.state?.out, ["second time lucky", false, "ok"]);
        assert.deepEqual(c.invoked, ["t", "t"]);
    });

    test("a node retry policy re-calls a failed tool but never one that succeeded", async () => {
        const c = new FakeMcpClient();
        let flaky = 0;
        const original = c.callTool.bind(c);
        c.callTool = async (p) => {
            if (p.name === "get_file" && ++flaky === 1) throw new Error("timeout");
            return original(p);
        };
        const tb = await McpToolbox.connect(c, { name: "gh" });
        const retried = graph()
            .channel("out", channel.append<string>())
            .node(
                "n",
                async (_s, ctx) => {
                    const a = await tb.call(ctx, "gh__search", { q: "x" });
                    const b = await tb.call(ctx, "gh__get_file", { path: "README.md" });
                    return { out: [a.text, b.text] };
                },
                { retry: { maxAttempts: 2 } },
            )
            .edge(START, "n")
            .compile();

        const done = await run(retried, {}, { threadId: "t-retry", checkpointer: new InMemoryCheckpointer() });
        assert.equal(done.status, "done");
        assert.deepEqual(c.calls.map((x) => x.name), ["search", "get_file"]);
    });

    test("args are passed through verbatim, including unicode and nested values", async () => {
        const c = new FakeMcpClient();
        const tb = await McpToolbox.connect(c, { name: "gh" });
        const args = { q: "örnek 🧶", nested: { list: [1, null, "x"] } };
        await tb.invoke("gh__search", args);
        assert.deepEqual(c.calls[0]!.arguments, args);
    });
});

describe("resources and prompts — sloppy servers", () => {
    test("a resources/read answer without contents reads as empty text", async () => {
        const tb = await McpToolbox.connect(client({ readResource: async () => ({}) as never }), { name: "s" });
        assert.equal(await tb.fetchResource("file:///x"), "");
    });

    test("binary and null resource contents contribute no text", async () => {
        const tb = await McpToolbox.connect(
            client({
                readResource: async () => ({
                    contents: [null as never, { uri: "a", blob: "AAEC" }, { uri: "b", text: 42 as never }, { uri: "c", text: "real" }],
                }),
            }),
            { name: "s" },
        );
        assert.equal(await tb.fetchResource("file:///x"), "real");
    });

    test("a failing resource read rejects with the server's error and is not journaled", async () => {
        let n = 0;
        const tb = await McpToolbox.connect(
            client({
                readResource: async () => {
                    if (++n === 1) throw new Error("not found");
                    return { contents: [{ uri: "x", text: "found" }] };
                },
            }),
            { name: "s" },
        );
        const g = graph()
            .channel("doc", channel.lastWrite<string>(""))
            .node("n", async (_s, ctx) => ({ doc: await tb.readResource(ctx, "file:///x") }))
            .edge(START, "n")
            .compile();
        const opts = { threadId: "t-res", checkpointer: new InMemoryCheckpointer() };
        assert.equal((await run(g, {}, opts)).status, "error");
        assert.equal((await run(g, {}, opts)).state?.doc, "found");
    });

    test("listing resources or prompts propagates a server failure", async () => {
        const tb = await McpToolbox.connect(
            client({
                listResources: async () => Promise.reject(new Error("boom-r")),
                listPrompts: async () => Promise.reject(new Error("boom-p")),
            }),
            { name: "s" },
        );
        await assert.rejects(tb.resources(), /boom-r/);
        await assert.rejects(tb.prompts(), /boom-p/);
    });

    test("promptText survives missing messages, missing content and null blocks", () => {
        assert.equal(promptText({} as never), "");
        assert.equal(
            promptText({
                messages: [
                    { role: "user" } as never,
                    { role: "user", content: null as never },
                    { role: "user", content: [null as never, { type: "text", text: "kept" }] },
                    { role: "assistant", content: { type: "image", data: "x" } },
                ],
            }),
            "kept",
        );
    });

    test("prompt() is journaled under its own key and fetched once across a resume", async () => {
        const c = new FakeMcpClient();
        let fetches = 0;
        const original = c.getPrompt.bind(c);
        c.getPrompt = async (p) => (fetches++, original(p));
        const tb = await McpToolbox.connect(c, { name: "gh" });
        const name = c.script.prompts.find((p) => !(p.arguments ?? []).some((a) => a.required))!.name;
        const g = graph()
            .channel("out", channel.append<string>())
            .node("n", async (_s, ctx) => {
                const text = await tb.prompt(ctx, name);
                return { out: [text, await ctx.interrupt<string>()] };
            })
            .edge(START, "n")
            .compile();
        const opts = { threadId: "t-prompt", checkpointer: new InMemoryCheckpointer() };
        await run(g, {}, opts);
        await resume(g, "ok", opts);
        assert.equal(fetches, 1);
    });
});

describe("normalizeToolResult — hostile shapes", () => {
    test("no content at all is an empty, non-error result", () => {
        assert.deepEqual(normalizeToolResult({}), { text: "", isError: false, content: [] });
    });

    test("null blocks are dropped; non-string text is ignored", () => {
        const raw: McpCallToolResult = { content: [null as unknown as McpContent, { type: "text", text: 5 as never }, { type: "text", text: "a" }] };
        const r = normalizeToolResult(raw);
        assert.equal(r.text, "a");
        assert.equal(r.content.length, 2);
    });

    test("isError must be literally true", () => {
        assert.equal(normalizeToolResult({ isError: "yes" as never }).isError, false);
        assert.equal(normalizeToolResult({ isError: true }).isError, true);
    });

    test("an embedded resource without text adds nothing; with text it joins in order", () => {
        const r = normalizeToolResult({
            content: [
                { type: "resource", resource: { uri: "a", blob: "AA==" } },
                { type: "text", text: "one" },
                { type: "resource", resource: { uri: "b", text: "two" } },
            ],
        });
        assert.equal(r.text, "one\ntwo");
    });

    test("the result is a copy: mutating it leaves the server's object alone", () => {
        const raw = { content: [{ type: "text", text: "x" }] };
        const r = normalizeToolResult(raw);
        (r.content as McpContent[]).push({ type: "text", text: "y" });
        assert.equal(raw.content.length, 1);
    });

    test("a structuredContent of null is kept as given", () => {
        assert.equal(normalizeToolResult({ structuredContent: null as never }).structured, null);
    });
});

describe("mcpSkills and toSkillName", () => {
    test("a name with nothing usable becomes `prompt`", () => {
        assert.equal(toSkillName("!!!"), "prompt");
        assert.equal(toSkillName(""), "prompt");
    });

    test("names are cut to 64 characters without a trailing hyphen", () => {
        const name = toSkillName(`${"a".repeat(63)}-bbbb`);
        assert.ok(name.length <= 64);
        assert.ok(!name.endsWith("-"));
    });

    test("unicode letters are not skill-name characters", () => {
        assert.equal(toSkillName("Résumé Tool"), "r-sum-tool");
    });

    test("a prompt description over 1024 characters is truncated", async () => {
        const long = "d".repeat(2000);
        const tb = await McpToolbox.connect(
            client({
                listPrompts: async () => ({ prompts: [{ name: "p", description: long }] }),
                getPrompt: async () => ({ messages: [] }),
            }),
            { name: "s" },
        );
        const [skill] = await mcpSkills(tb);
        assert.equal(skill!.description.length, 1024);
    });

    test("a prompt with only optional arguments is fetched; its argument names are recorded", async () => {
        const tb = await McpToolbox.connect(
            client({
                listPrompts: async () => ({ prompts: [{ name: "p", arguments: [{ name: "tone" }, { name: "len", required: false }] }] }),
                getPrompt: async () => ({ messages: [{ role: "user", content: { type: "text", text: "body" } }] }),
            }),
            { name: "s" },
        );
        const [skill] = await mcpSkills(tb, { prefix: "" });
        assert.equal(skill!.name, "p");
        assert.equal(skill!.instructions, "body");
        assert.equal(skill!.metadata.arguments, "tone,len");
    });

    test("a server without prompts yields no skills", async () => {
        assert.deepEqual(await mcpSkills(await McpToolbox.connect(client(), { name: "s" })), []);
    });

    test("a failing prompt fetch fails the whole conversion loudly", async () => {
        const tb = await McpToolbox.connect(
            client({
                listPrompts: async () => ({ prompts: [{ name: "p" }] }),
                getPrompt: async () => Promise.reject(new Error("prompt gone")),
            }),
            { name: "s" },
        );
        await assert.rejects(mcpSkills(tb), /prompt gone/);
    });
});

describe("mcp node types — build-time validation", () => {
    const spec = (config: Record<string, unknown>, type = "mcp_tool"): GraphSpec => ({
        name: "m",
        channels: { result: {}, args: {} },
        nodes: [{ id: "n", type, config }],
        edges: [
            { from: START, to: "n" },
            { from: "n", to: END },
        ],
    });

    test("a server name that is an Object.prototype member is unknown, not a crash", async () => {
        const tb = await McpToolbox.connect(new FakeMcpClient(), { name: "gh" });
        for (const server of ["constructor", "toString", "__proto__"]) {
            assert.throws(() => fromSpec(spec({ server, tool: "gh__search" }), mcpNodes({ gh: tb })), /references MCP server/, server);
        }
    });

    for (const [label, config, pattern] of [
        ["a missing server", { tool: "gh__search" }, /needs config\.server/],
        ["a non-string server", { server: 1, tool: "gh__search" }, /needs config\.server/],
        ["a missing tool", { server: "gh" }, /references tool undefined/],
        ["a raw (unprefixed) tool name", { server: "gh", tool: "search" }, /references tool "search"/],
        ["an empty `to`", { server: "gh", tool: "gh__search", to: "" }, /config\.to must be a channel name/],
    ] as const) {
        test(`mcp_tool with ${label} fails at build time`, async () => {
            const tb = await McpToolbox.connect(new FakeMcpClient(), { name: "gh" });
            assert.throws(() => fromSpec(spec(config), mcpNodes({ gh: tb })), pattern);
        });
    }

    test("mcp_resource needs a uri", async () => {
        const tb = await McpToolbox.connect(new FakeMcpClient(), { name: "gh" });
        assert.throws(() => fromSpec(spec({ server: "gh" }, "mcp_resource"), mcpNodes({ gh: tb })), /needs config\.uri/);
        assert.throws(() => fromSpec(spec({ server: "gh", uri: "" }, "mcp_resource"), mcpNodes({ gh: tb })), /needs config\.uri/);
    });

    test("argumentsFrom pointing at a non-object channel value contributes no arguments", async () => {
        const c = new FakeMcpClient();
        const tb = await McpToolbox.connect(c, { name: "gh" });
        const g = fromSpec(spec({ server: "gh", tool: "gh__search", arguments: { q: "fixed" }, argumentsFrom: "args" }), mcpNodes({ gh: tb })).compile();
        await run(g, { args: ["not", "an", "object"] });
        assert.deepEqual(c.calls[0]!.arguments, { q: "fixed" });
    });

    test("channel arguments override fixed ones key by key", async () => {
        const c = new FakeMcpClient();
        const tb = await McpToolbox.connect(c, { name: "gh" });
        const g = fromSpec(spec({ server: "gh", tool: "gh__search", arguments: { q: "fixed", limit: 5 }, argumentsFrom: "args" }), mcpNodes({ gh: tb })).compile();
        await run(g, { args: { q: "dynamic" } });
        assert.deepEqual(c.calls[0]!.arguments, { q: "dynamic", limit: 5 });
    });

    test("a non-object fixed `arguments` is ignored rather than spread", async () => {
        const c = new FakeMcpClient();
        const tb = await McpToolbox.connect(c, { name: "gh" });
        const g = fromSpec(spec({ server: "gh", tool: "gh__search", arguments: "q=1" }), mcpNodes({ gh: tb })).compile();
        await run(g);
        assert.deepEqual(c.calls[0]!.arguments, {});
    });

    test("a tool node whose server throws ends the run in error, attributed to the node", async () => {
        const tb = await McpToolbox.connect(client({ callTool: async () => Promise.reject(new Error("503")) }), { name: "s" });
        const g = fromSpec(spec({ server: "s", tool: "s__t" }), mcpNodes({ s: tb })).compile();
        const result = await run(g);
        assert.equal(result.status, "error");
        assert.equal(result.errors[0]![0], "n");
        assert.match((result.errors[0]![1] as Error).message, /503/);
    });
});
