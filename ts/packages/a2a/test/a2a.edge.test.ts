/**
 * Error paths and hostile responses for `@ilmek/a2a`: JSON-RPC errors,
 * malformed responses, HTTP failures, journaling under failure, and the
 * `a2a_call` node's build-time validation.
 */

import test, { describe } from "node:test";
import assert from "node:assert/strict";

import { channel, END, fromSpec, graph, InMemoryCheckpointer, resume, run, START, type GraphSpec } from "@ilmek/core";

import {
    A2aAgent,
    A2aError,
    a2aNodes,
    dataOf,
    HttpA2aTransport,
    normalizeTask,
    textOf,
    type A2aAgentCard,
    type A2aTask,
    type A2aTransport,
    type JsonRpcRequest,
    type JsonRpcResponse,
} from "../src/index.ts";
import { FakeA2aTransport } from "./fake.ts";

const card: A2aAgentCard = { name: "Desk", url: "https://x/a2a" };

/** A transport whose every post answers with `respond(request)`. */
function scripted(respond: (r: JsonRpcRequest) => JsonRpcResponse | Promise<JsonRpcResponse>): A2aTransport & { sent: JsonRpcRequest[] } {
    const sent: JsonRpcRequest[] = [];
    return {
        sent,
        getAgentCard: async () => card,
        post: async (r) => {
            sent.push(r);
            return respond(r);
        },
    };
}

const task = (fields: Partial<A2aTask> = {}): A2aTask => ({
    kind: "task",
    id: "task-1",
    contextId: "ctx-1",
    status: { state: "completed" },
    artifacts: [{ artifactId: "a", parts: [{ kind: "text", text: "done" }] }],
    ...fields,
});

const ok = (result: unknown) => (r: JsonRpcRequest): JsonRpcResponse => ({ jsonrpc: "2.0", id: r.id, result });

describe("A2aAgent.connect", () => {
    test("a card fetch failure rejects connect", async () => {
        const t: A2aTransport = { getAgentCard: async () => Promise.reject(new Error("DNS")), post: async () => ({ jsonrpc: "2.0", id: 1 }) };
        await assert.rejects(A2aAgent.connect(t), /DNS/);
    });

    test("a card whose name slugs to nothing needs an explicit name", async () => {
        const t = { ...scripted(ok(task())), getAgentCard: async () => ({ name: "!!!", url: "u" }) };
        await assert.rejects(A2aAgent.connect(t), /needs a name/);
        assert.equal((await A2aAgent.connect(t, { name: "desk" })).name, "desk");
    });

    test("a card with no name at all needs an explicit name", async () => {
        const t = { ...scripted(ok(task())), getAgentCard: async () => ({ url: "u" }) as never };
        await assert.rejects(A2aAgent.connect(t), /needs a name/);
    });

    test("default message ids are unique per send", async () => {
        const t = scripted(ok(task()));
        const agent = await A2aAgent.connect(t);
        await agent.invoke("a");
        await agent.invoke("b");
        const ids = t.sent.map((r) => (r.params as { message: { messageId: string } }).message.messageId);
        assert.notEqual(ids[0], ids[1]);
    });

    test("JSON-RPC request ids increase per call", async () => {
        const t = scripted(ok(task()));
        const agent = await A2aAgent.connect(t);
        await agent.invoke("a");
        await agent.getTask("task-1");
        await agent.cancelTask("task-1");
        assert.deepEqual(t.sent.map((r) => [r.id, r.method]), [
            [1, "message/send"],
            [2, "tasks/get"],
            [3, "tasks/cancel"],
        ]);
    });
});

describe("A2aAgent — error paths", () => {
    test("a JSON-RPC error carries code, message and data", async () => {
        const agent = await A2aAgent.connect(scripted((r) => ({ jsonrpc: "2.0", id: r.id, error: { code: -32001, message: "task not found", data: { id: "x" } } })));
        await assert.rejects(agent.getTask("x"), (e: unknown) => e instanceof A2aError && e.code === -32001 && e.message === "task not found" && (e.data as { id: string }).id === "x");
    });

    for (const [label, response] of [
        ["no result and no error", { jsonrpc: "2.0", id: 1 }],
        ["a null result", { jsonrpc: "2.0", id: 1, result: null }],
        ["a string result", { jsonrpc: "2.0", id: 1, result: "ok" }],
        ["a null response", null],
    ] as const) {
        test(`a response with ${label} is a malformed-response A2aError, not a TypeError`, async () => {
            const agent = await A2aAgent.connect(scripted(() => response as never));
            await assert.rejects(agent.invoke("hi"), (e: unknown) => e instanceof A2aError && e.code === -32603 && /malformed JSON-RPC response/.test(e.message));
        });
    }

    test("a result that is neither a task nor a message is refused", async () => {
        const agent = await A2aAgent.connect(scripted(ok({ hello: "world" })));
        await assert.rejects(agent.invoke("hi"), (e: unknown) => e instanceof A2aError && /neither a task nor a message/.test(e.message));
    });

    test("a task without a status is refused", async () => {
        const agent = await A2aAgent.connect(scripted(ok({ id: "t", contextId: "c", kind: "task" })));
        await assert.rejects(agent.getTask("t"), (e: unknown) => e instanceof A2aError && /has no status/.test(e.message));
    });

    test("a bare message reply without a task id gets a synthetic one; without a context it uses the agent name", async () => {
        const agent = await A2aAgent.connect(scripted(ok({ kind: "message", messageId: "m9", role: "agent", parts: [{ kind: "text", text: "hi" }] })), { name: "desk" });
        const r = await agent.invoke("hello");
        assert.equal(r.taskId, "message:m9");
        assert.equal(r.contextId, "desk");
        assert.equal(r.state, "completed");
        assert.equal(r.text, "hi");
    });

    test("a message with neither text nor data is refused before any request", async () => {
        const t = scripted(ok(task()));
        const agent = await A2aAgent.connect(t);
        await assert.rejects(agent.invoke(""), /needs text or data/);
        assert.equal(t.sent.length, 0);
    });

    test("data alone is a valid message; taskId and contextId are forwarded", async () => {
        const t = scripted(ok(task()));
        const agent = await A2aAgent.connect(t);
        await agent.invoke("", { data: { answers: { a: 1 } }, taskId: "task-9", contextId: "ctx-9", messageId: "fixed" });
        assert.deepEqual((t.sent[0]!.params as { message: unknown }).message, {
            kind: "message",
            messageId: "fixed",
            role: "user",
            parts: [{ kind: "data", data: { answers: { a: 1 } } }],
            taskId: "task-9",
            contextId: "ctx-9",
        });
    });

    test("tasks/get forwards historyLength only when given", async () => {
        const t = scripted(ok(task()));
        const agent = await A2aAgent.connect(t);
        await agent.getTask("t1");
        await agent.getTask("t1", 0);
        assert.deepEqual(t.sent.map((r) => r.params), [{ id: "t1" }, { id: "t1", historyLength: 0 }]);
    });

    test("terminal failure states are reported as states, not thrown", async () => {
        for (const state of ["failed", "rejected", "canceled", "auth-required", "unknown"] as const) {
            const agent = await A2aAgent.connect(scripted(ok(task({ status: { state, message: { messageId: "m", role: "agent", parts: [{ kind: "text", text: `why: ${state}` }] } }, artifacts: [] }))));
            const r = await agent.invoke("x");
            assert.equal(r.state, state);
            assert.equal(r.needsInput, false);
            assert.equal(r.statusText, `why: ${state}`);
            assert.equal(r.text, "");
        }
    });
});

describe("journaling under failure", () => {
    const flow = (agent: A2aAgent) =>
        graph()
            .channel("out", channel.append<string>())
            .node("n", async (_s, ctx) => {
                const r = await agent.send(ctx, "where is my order?");
                const ok = await ctx.interrupt<string>();
                return { out: [r.text, ok] };
            })
            .edge(START, "n")
            .edge("n", END)
            .compile();

    test("a failed send is not journaled: the next run sends again, then never again", async () => {
        const t = new FakeA2aTransport();
        let first = true;
        const original = t.post.bind(t);
        t.post = async (r) => {
            if (first) {
                first = false;
                throw new Error("ECONNRESET");
            }
            return original(r);
        };
        const agent = await A2aAgent.connect(t, { mintId: () => "m" });
        const opts = { threadId: "t-a2a", checkpointer: new InMemoryCheckpointer() };

        assert.equal((await run(flow(agent), {}, opts)).status, "error");
        assert.equal((await run(flow(agent), {}, opts)).status, "interrupted");
        const done = await resume(flow(agent), "thanks", opts);
        assert.deepEqual(done.state?.out, ["Order ORD-42 totals 249.9.", "thanks"]);
        assert.equal(t.requests.length, 1, "one successful send; the failed one never reached the fake");
    });

    test("a JSON-RPC error inside send fails the node and is retried by a retry policy", async () => {
        let n = 0;
        const agent = await A2aAgent.connect(
            scripted((r) => (++n === 1 ? { jsonrpc: "2.0", id: r.id, error: { code: -32000, message: "busy" } } : ok(task())(r))),
        );
        const g = graph()
            .channel("out", channel.append<string>())
            .node("n", async (_s, ctx) => ({ out: [(await agent.send(ctx, "x")).text] }), { retry: { maxAttempts: 2 } })
            .edge(START, "n")
            .compile();
        const result = await run(g, {}, { threadId: "t-busy", checkpointer: new InMemoryCheckpointer() });
        assert.equal(result.status, "done");
        assert.deepEqual(result.state?.out, ["done"]);
        assert.ok(result.events.some((e) => e.type === "node_retry"));
    });
});

describe("normalizeTask, textOf, dataOf", () => {
    test("non-text parts and empty artifacts contribute no text", () => {
        const r = normalizeTask(
            task({
                artifacts: [
                    { artifactId: "1", parts: [] },
                    { artifactId: "2", parts: [{ kind: "file", file: { uri: "u" } }] },
                    { artifactId: "3", parts: [{ kind: "text", text: "a" }, { kind: "data", data: {} }, { kind: "text", text: "b" }] },
                ],
            }),
        );
        assert.equal(r.text, "a\nb");
    });

    test("textOf and dataOf tolerate undefined parts", () => {
        assert.equal(textOf(undefined), "");
        assert.equal(dataOf(undefined), undefined);
    });

    test("textOf skips a text part whose text is not a string", () => {
        assert.equal(textOf([{ kind: "text", text: 7 as never }, { kind: "text", text: "x" }]), "x");
    });

    test("dataOf returns the first data part only", () => {
        assert.deepEqual(dataOf([{ kind: "data", data: { a: 1 } }, { kind: "data", data: { b: 2 } }]), { a: 1 });
    });

    test("a task with no artifacts and no status message normalizes to empty strings", () => {
        const r = normalizeTask({ id: "t", contextId: "c", status: { state: "working" } });
        assert.equal(r.text, "");
        assert.equal(r.statusText, "");
        assert.equal(r.statusData, undefined);
        assert.equal(r.needsInput, false);
    });
});

describe("HttpA2aTransport — failures", () => {
    const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

    test("an invalid agent URL is refused at construction", () => {
        assert.throws(() => new HttpA2aTransport("not a url"));
    });

    test("a card without a url and no explicit endpoint cannot post", async () => {
        const t = new HttpA2aTransport("https://bot.example.com", { fetch: (async () => json({ name: "X" })) as typeof fetch });
        await assert.rejects(t.post({ jsonrpc: "2.0", id: 1, method: "message/send" }), /has no url and no endpoint was given/);
    });

    test("an HTTP error on post names the method and status", async () => {
        const t = new HttpA2aTransport("https://bot.example.com/a2a", { fetch: (async () => new Response("", { status: 502 })) as typeof fetch });
        await assert.rejects(t.post({ jsonrpc: "2.0", id: 1, method: "tasks/get" }), /A2A tasks\/get: HTTP 502/);
    });

    test("a non-JSON body surfaces as a parse error", async () => {
        const t = new HttpA2aTransport("https://bot.example.com/a2a", { fetch: (async () => new Response("<html>")) as typeof fetch });
        await assert.rejects(t.post({ jsonrpc: "2.0", id: 1, method: "x" }), SyntaxError);
    });

    test("headers are sent on both the card fetch and the post, and the body is the JSON-RPC request", async () => {
        const seen: Array<{ url: string; headers: Record<string, string>; body?: string }> = [];
        const t = new HttpA2aTransport("https://bot.example.com", {
            headers: { Authorization: "Bearer k" },
            fetch: (async (input: string | URL | Request, init?: RequestInit) => {
                seen.push({ url: String(input), headers: init?.headers as Record<string, string>, ...(init?.body ? { body: String(init.body) } : {}) });
                return String(input).endsWith("agent-card.json") ? json({ name: "X", url: "https://bot.example.com/rpc" }) : json({ jsonrpc: "2.0", id: 1, result: {} });
            }) as typeof fetch,
        });
        const req: JsonRpcRequest = { jsonrpc: "2.0", id: 1, method: "m", params: { q: "ü" } };
        await t.post(req);
        assert.equal(seen.length, 2);
        for (const s of seen) assert.equal(s.headers.Authorization, "Bearer k");
        assert.deepEqual(JSON.parse(seen[1]!.body!), req);
    });

    test("the card is fetched once; later posts reuse the endpoint it named", async () => {
        let cardFetches = 0;
        const t = new HttpA2aTransport("https://bot.example.com/", {
            fetch: (async (input: string | URL | Request) => {
                if (String(input).endsWith("agent-card.json")) {
                    cardFetches++;
                    return json({ name: "X", url: "https://bot.example.com/rpc" });
                }
                return json({ jsonrpc: "2.0", id: 1, result: {} });
            }) as typeof fetch,
        });
        await t.post({ jsonrpc: "2.0", id: 1, method: "a" });
        await t.post({ jsonrpc: "2.0", id: 2, method: "b" });
        assert.equal(cardFetches, 1);
    });
});

describe("a2a_call node type — build-time validation", () => {
    const spec = (config: Record<string, unknown>): GraphSpec => ({
        name: "a",
        channels: { result: {}, q: {} },
        nodes: [{ id: "n", type: "a2a_call", config }],
        edges: [
            { from: START, to: "n" },
            { from: "n", to: END },
        ],
    });

    test("an agent name that is an Object.prototype member is unknown, not a crash", async () => {
        const agent = await A2aAgent.connect(new FakeA2aTransport());
        for (const name of ["constructor", "toString", "__proto__"]) {
            assert.throws(() => fromSpec(spec({ agent: name, text: "hi" }), a2aNodes({ desk: agent })), /references A2A agent/, name);
        }
    });

    for (const [label, config, pattern] of [
        ["no agent", { text: "hi" }, /needs config\.agent/],
        ["an empty agent", { agent: "", text: "hi" }, /needs config\.agent/],
        ["neither text nor textFrom", { agent: "desk" }, /needs config\.text or config\.textFrom/],
        ["a non-string text", { agent: "desk", text: 5 }, /needs config\.text or config\.textFrom/],
        ["a bad `to`", { agent: "desk", text: "hi", to: 3 }, /config\.to must be a channel name/],
    ] as const) {
        test(`a2a_call with ${label} fails at build time`, async () => {
            const agent = await A2aAgent.connect(new FakeA2aTransport());
            assert.throws(() => fromSpec(spec(config), a2aNodes({ desk: agent })), pattern);
        });
    }

    test("an empty textFrom channel sends an empty message, which is refused at run time", async () => {
        const agent = await A2aAgent.connect(new FakeA2aTransport());
        const g = fromSpec(spec({ agent: "desk", textFrom: "q" }), a2aNodes({ desk: agent })).compile();
        const result = await run(g);
        assert.equal(result.status, "error");
        assert.match((result.errors[0]![1] as Error).message, /needs text or data/);
    });

    test("a non-string textFrom value is stringified", async () => {
        const t = new FakeA2aTransport();
        const agent = await A2aAgent.connect(t);
        const g = fromSpec(spec({ agent: "desk", textFrom: "q" }), a2aNodes({ desk: agent })).compile();
        await run(g, { q: 42 });
        assert.deepEqual((t.requests[0]!.params as { message: { parts: unknown[] } }).message.parts, [{ kind: "text", text: "42" }]);
    });
});
