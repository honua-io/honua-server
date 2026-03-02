#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { HonuaClient } from "@honua/sdk-js";
import type { HonuaTransport } from "@honua/sdk-js";
export interface RuntimeOptions {
    baseUrl: string;
    apiKey: string | undefined;
    transport: HonuaTransport;
}
export declare function resolveRuntimeOptions(env: NodeJS.ProcessEnv): RuntimeOptions;
export declare function createClientFromEnv(env?: NodeJS.ProcessEnv): HonuaClient;
export declare function createServer(client: HonuaClient): McpServer;
//# sourceMappingURL=index.d.ts.map