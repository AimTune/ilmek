// Type-level fixture, compiled by sdk-client.test.ts under the workspace's
// strict settings (exactOptionalPropertyTypes on). It never runs: it only has
// to typecheck. Every line here is a claim the docs make — "a connected SDK
// client passes as-is" — so a cast anywhere below would be a regression.

import { Client } from "@modelcontextprotocol/sdk/client/index.js";

import { McpToolbox, type McpClientLike } from "../../src/index.ts";

declare const client: Client;

// The official Client is assignable to the port, no cast.
export const port: McpClientLike = client;

// And every entry point that takes a client accepts it directly.
export const connect = (): Promise<McpToolbox> => McpToolbox.connect(client, { name: "github" });
