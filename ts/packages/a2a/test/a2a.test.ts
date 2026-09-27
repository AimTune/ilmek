/**
 * `@ilmek/a2a` — the agent over a scripted transport, journaled sends, task
 * normalization, the HTTP transport's URL handling, and the `a2a_call` node
 * type. The scripted agent in conformance/a2a is the cross-language fixture.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

import { channel, END, fromSpec, graph, InMemoryCheckpointer, resume, run, START, type GraphSpec } from "@ilmek/core";

import { A2aAgent, A2aError, a2aNodes, HttpA2aTransport, normalizeTask, type A2aTask } from "../src/index.ts";
import { conformanceDir, FakeA2aTransport } from "./fake.ts";

const canonical = (v: unknown): string => JSON.stringify(sortKeys(v));
function sortKeys(v: unknown): unknown {
    if (Array.isArray(v)) return v.map(sortKeys);
    if (v !== null && typeof v === "object") {
        return Object.fromEntries(Object.keys(v as Record<string, unknown>).sort().map((k) => [k, sortKeys((v as Record<string, unknown>)[k])]));
    }
    return v;
}

const connect = (transport = new FakeA2aTransport()) => A2aAgent.connect(transport, { mintId: () => "m-x" });

describe("conformance/a2a (shared with .NET)", () => {
    test("the scripted agent reduces to expected.json", async () => {
        const expected = JSON.parse(readFileSync(join(conformanceDir, "expected.json"), "utf8")) as Record<string, unknown>;
        const transport = new FakeA2aTransport();
        const agent = await connect(transport);
        const results: Record<string, unknown> = {};
        for (const text of Object.keys(transport.script.sends)) {
            const { task: _task, ...rest } = await agent.invoke(text);
            results[text] = rest;
        }
        let error: unknown;
        try {
            await agent.getTask("task-nope");
        } catch (err) {
            error = err instanceof A2aError ? { code: err.code, message: err.message } : String(err);
        }
        assert.equal(canonical({ name: agent.name, results, error }), canonical(expected));
    });
});

describe("A2aAgent", () => {
    test("connect fetches the card once and slugs the name unless given", async () => {
        const transport = new FakeA2aTransport();
        const agent = await connect(transport);
        assert.equal(agent.name, "support-desk");
        assert.equal(agent.card.url, "https://bot.example.com/a2a");
        assert.equal(transport.cardFetches, 1);
        assert.equal((await A2aAgent.connect(new FakeA2aTransport(), { name: "desk" })).name, "desk");
    });

    test("invoke sends a user message with text (and data), and reads the task", async () => {
        const transport = new FakeA2aTransport();
        const agent = await connect(transport);
        const r = await agent.invoke("where is my order?", { contextId: "conv-1", data: { hint: 1 } });
        const sent = transport.requests[0]!.params as { message: { role: string; parts: unknown[]; contextId: string; messageId: string } };
        assert.equal(transport.requests[0]!.method, "message/send");
        assert.deepEqual(sent.message.parts, [{ kind: "text", text: "where is my order?" }, { kind: "data", data: { hint: 1 } }]);
        assert.equal(sent.message.contextId, "conv-1");
        assert.equal(sent.message.messageId, "m-x");
        assert.equal(r.state, "completed");
        assert.equal(r.text, "Order ORD-42 totals 249.9.", "file artifacts contribute no text");
        assert.equal(r.needsInput, false);
        assert.equal(r.statusText, "");
        await assert.rejects(agent.invoke(""), /needs text or data/);
    });

    test("an input-required task exposes the agent's question and data; a reply on the task continues it", async () => {
        const agent = await connect();
        const paused = await agent.invoke("refund please");
        assert.equal(paused.needsInput, true);
        assert.match(paused.statusText, /needs input before it can continue/);
        assert.deepEqual((paused.statusData as { pending: Array<{ id: string }> }).pending.map((p) => p.id), ["agent:interrupt#0"]);
        const done = await agent.invoke("Approve", { taskId: paused.taskId });
        assert.equal(done.state, "completed");
        assert.equal(done.text, "Refunded.");
    });

    test("a JSON-RPC error becomes an A2aError; a bare message becomes a completed task", async () => {
        const agent = await connect();
        await assert.rejects(agent.getTask("task-nope"), (e: unknown) => e instanceof A2aError && e.code === -32001);
        await assert.rejects(agent.invoke("unscripted"), (e: unknown) => e instanceof A2aError && e.code === -32602);
        const bare = await agent.invoke("just a message");
        assert.equal(bare.state, "completed");
        assert.equal(bare.text, "A bare message, no task.");
        assert.equal(bare.contextId, "conv-3");
        assert.equal((await agent.cancelTask("task-1")).state, "canceled");
    });

    test("send is journaled: once across an interrupt/resume; distinct keys for ask and answer", async () => {
        const transport = new FakeA2aTransport();
        const agent = await connect(transport);
        const g = graph("delegate")
            .channel("log", channel.append<string>())
            .node("ask", async (_s, ctx) => {
                const first = await agent.send(ctx, "refund please");
                const ok = await ctx.interrupt<string>({ q: first.statusText });
                const done = await agent.send(ctx, ok, { taskId: first.taskId, key: "answer" });
                return { log: [first.state, done.text] };
            })
            .edge(START, "ask")
            .edge("ask", END)
            .compile();
        const opts = { threadId: "t-1", checkpointer: new InMemoryCheckpointer() };
        const paused = await run(g, {}, opts);
        assert.equal(paused.status, "interrupted");
        assert.equal(transport.requests.length, 1);
        const done = await resume(g, "Approve", opts);
        assert.equal(done.status, "done");
        assert.deepEqual(done.state?.log, ["input-required", "Refunded."]);
        assert.equal(transport.requests.length, 2, "the first send was replayed from the journal, not re-sent");
    });
});

describe("normalizeTask", () => {
    test("joins artifact text, reads status text and data, flags input-required", () => {
        const task: A2aTask = {
            id: "t",
            contextId: "c",
            status: { state: "input-required", message: { messageId: "m", role: "agent", parts: [{ kind: "text", text: "why?" }, { kind: "data", data: { a: 1 } }] } },
            artifacts: [{ artifactId: "a", parts: [{ kind: "text", text: "one" }] }, { artifactId: "b", parts: [{ kind: "text", text: "two" }] }],
        };
        const r = normalizeTask(task);
        assert.equal(r.text, "one\ntwo");
        assert.equal(r.statusText, "why?");
        assert.deepEqual(r.statusData, { a: 1 });
        assert.equal(r.needsInput, true);
        assert.equal(normalizeTask({ id: "t", contextId: "c", status: { state: "completed" } }).text, "");
    });
});

describe("HttpA2aTransport", () => {
    test("derives the card URL from an origin and the endpoint from the card; a .json URL is the card", async () => {
        const calls: string[] = [];
        const fakeFetch = (async (input: string | URL | Request, init?: RequestInit) => {
            const url = String(input);
            calls.push(`${init?.method ?? "GET"} ${url}`);
            if (url.endsWith("agent-card.json") || url.endsWith("card.json")) {
                return new Response(JSON.stringify({ name: "X", url: "https://bot.example.com/rpc" }), { headers: { "Content-Type": "application/json" } });
            }
            return new Response(JSON.stringify({ jsonrpc: "2.0", id: 1, result: {} }), { headers: { "Content-Type": "application/json" } });
        }) as typeof fetch;

        const fromOrigin = new HttpA2aTransport("https://bot.example.com", { fetch: fakeFetch, headers: { Authorization: "Bearer t" } });
        await fromOrigin.post({ jsonrpc: "2.0", id: 1, method: "ping" });
        assert.deepEqual(calls, ["GET https://bot.example.com/.well-known/agent-card.json", "POST https://bot.example.com/rpc"]);

        calls.length = 0;
        const fromEndpoint = new HttpA2aTransport("https://bot.example.com/a2a", { fetch: fakeFetch });
        await fromEndpoint.post({ jsonrpc: "2.0", id: 1, method: "ping" });
        assert.deepEqual(calls, ["POST https://bot.example.com/a2a"], "an explicit endpoint skips the card");

        calls.length = 0;
        const fromCard = new HttpA2aTransport("https://bot.example.com/custom/card.json", { fetch: fakeFetch });
        assert.equal((await fromCard.getAgentCard()).name, "X");
        assert.deepEqual(calls, ["GET https://bot.example.com/custom/card.json"]);

        const failing = new HttpA2aTransport("https://bot.example.com", { fetch: (async () => new Response("no", { status: 503 })) as typeof fetch });
        await assert.rejects(failing.getAgentCard(), /HTTP 503/);
    });
});

describe("a2a_call node type (graphs as data)", () => {
    test("sends channel text, continues a context, answers a task, writes text or the whole result", async () => {
        const transport = new FakeA2aTransport();
        const agents = { desk: await connect(transport) };
        const spec: GraphSpec = {
            name: "delegate",
            channels: { question: {}, ctx: {}, task: {}, answer: {}, full: {} },
            nodes: [
                { id: "ask", type: "a2a_call", config: { agent: "desk", textFrom: "question", contextFrom: "ctx", to: "answer", textOnly: true } },
                { id: "approve", type: "a2a_call", config: { agent: "desk", text: "Approve", taskFrom: "task", to: "full" } },
            ],
            edges: [
                { from: START, to: "ask" },
                { from: "ask", to: "approve" },
                { from: "approve", to: END },
            ],
        };
        const g = fromSpec(spec, a2aNodes(agents)).compile();
        const result = await run(g, { question: "where is my order?", ctx: "conv-1", task: "task-2" }, { threadId: "t-2" });
        assert.equal(result.status, "done");
        assert.equal(result.state?.answer, "Order ORD-42 totals 249.9.");
        assert.equal((result.state?.full as { text: string }).text, "Refunded.");
        const [ask, approve] = transport.requests.map((r) => r.params as { message: { contextId?: string; taskId?: string } });
        assert.equal(ask!.message.contextId, "conv-1");
        assert.equal(approve!.message.taskId, "task-2");
    });

    test("bad references fail at build time", async () => {
        const registry = a2aNodes({ desk: await connect() });
        const spec = (config: Record<string, unknown>): GraphSpec => ({
            name: "bad",
            channels: { result: {} },
            nodes: [{ id: "n", type: "a2a_call", config }],
            edges: [{ from: START, to: "n" }, { from: "n", to: END }],
        });
        assert.throws(() => fromSpec(spec({ agent: "other", text: "x" }), registry), /known: \["desk"\]/);
        assert.throws(() => fromSpec(spec({ text: "x" }), registry), /config\.agent/);
        assert.throws(() => fromSpec(spec({ agent: "desk" }), registry), /config\.text or config\.textFrom/);
        assert.throws(() => fromSpec(spec({ agent: "desk", text: "x", to: 5 }), registry), /config\.to/);
    });
});
