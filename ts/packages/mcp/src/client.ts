// The MCP client port and the wire shapes `@ilmek/mcp` reads.
//
// The port is **duck-typed** to the official `@modelcontextprotocol/sdk`
// `Client` — `listTools`, `callTool`, `listResources`, `readResource`,
// `listPrompts`, `getPrompt` — so a real client passes as-is, a fake passes in
// tests, and this package keeps zero dependencies (the same posture as the
// checkpointers: a provider may talk to a driver, it never bundles one).

/** One tool a server advertises (`tools/list`). `inputSchema` is JSON Schema. */
export interface McpToolInfo {
    readonly name: string;
    readonly description?: string;
    readonly inputSchema: Record<string, unknown>;
}

/** One block of a tool result's `content`. Only the fields ilmek reads are typed; the rest passes through. */
export interface McpContent {
    readonly type: string;
    readonly text?: string;
    readonly data?: string;
    readonly mimeType?: string;
    readonly uri?: string;
    readonly resource?: { readonly uri: string; readonly text?: string; readonly blob?: string; readonly mimeType?: string };
    readonly [key: string]: unknown;
}

/** The raw `tools/call` result. */
export interface McpCallToolResult {
    readonly content?: readonly McpContent[];
    readonly structuredContent?: Record<string, unknown>;
    readonly isError?: boolean;
    readonly [key: string]: unknown;
}

export interface McpResourceInfo {
    readonly uri: string;
    readonly name?: string;
    readonly description?: string;
    readonly mimeType?: string;
}

export interface McpResourceContents {
    readonly uri: string;
    readonly mimeType?: string;
    readonly text?: string;
    readonly blob?: string;
}

export interface McpPromptArgument {
    readonly name: string;
    readonly description?: string;
    readonly required?: boolean;
}

export interface McpPromptInfo {
    readonly name: string;
    readonly description?: string;
    readonly arguments?: readonly McpPromptArgument[];
}

export interface McpPromptMessage {
    readonly role: string;
    readonly content: McpContent | readonly McpContent[];
}

export interface McpGetPromptResult {
    readonly description?: string;
    readonly messages: readonly McpPromptMessage[];
}

/**
 * What a connected MCP client must look like — the method shapes of the official
 * SDK's `Client`. Resources and prompts are optional: a server that does not
 * advertise them has nothing to list.
 */
export interface McpClientLike {
    listTools(): Promise<{ tools: readonly McpToolInfo[] }>;
    callTool(params: { name: string; arguments?: Record<string, unknown> }): Promise<McpCallToolResult>;
    listResources?(): Promise<{ resources: readonly McpResourceInfo[] }>;
    readResource?(params: { uri: string }): Promise<{ contents: readonly McpResourceContents[] }>;
    listPrompts?(): Promise<{ prompts: readonly McpPromptInfo[] }>;
    getPrompt?(params: { name: string; arguments?: Record<string, string> }): Promise<McpGetPromptResult>;
}

/**
 * A tool result as a node wants it: the text a model can read, the structured
 * payload when the server gave one, the error flag, and the raw content for
 * anything else (images, audio, resource links). Plain data — it goes in the
 * journal.
 */
export interface McpToolResult {
    /** Every text block (and embedded text resource) joined with newlines; `""` when there is none. */
    readonly text: string;
    /** `structuredContent`, verbatim, when the server returned one. */
    readonly structured?: Record<string, unknown>;
    /** The server flagged the result as an error (the tool ran; it failed). */
    readonly isError: boolean;
    /** The raw content blocks, for anything `text` cannot carry. */
    readonly content: readonly McpContent[];
}

/** Normalize a raw `tools/call` result into {@link McpToolResult}. Pure. */
export function normalizeToolResult(raw: McpCallToolResult): McpToolResult {
    const content = raw.content ?? [];
    const texts: string[] = [];
    for (const block of content) {
        if (block.type === "text" && typeof block.text === "string") texts.push(block.text);
        else if (block.type === "resource" && typeof block.resource?.text === "string") texts.push(block.resource.text);
    }
    const out: { -readonly [K in keyof McpToolResult]: McpToolResult[K] } = {
        text: texts.join("\n"),
        isError: raw.isError === true,
        content: [...content],
    };
    if (raw.structuredContent !== undefined) out.structured = raw.structuredContent;
    return out;
}

/** The text of a prompt's messages, one line per message as `role: text`. */
export function promptText(result: McpGetPromptResult): string {
    return result.messages
        .map((m) => {
            const blocks = Array.isArray(m.content) ? m.content : [m.content as McpContent];
            const text = blocks
                .filter((b) => b.type === "text" && typeof b.text === "string")
                .map((b) => b.text as string)
                .join("\n");
            return text;
        })
        .filter((t) => t.length > 0)
        .join("\n\n");
}
