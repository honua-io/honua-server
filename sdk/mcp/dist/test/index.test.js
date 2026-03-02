import { describe, it, expect } from "vitest";
import { HonuaClient } from "@honua/sdk-js";
import { createServer } from "../src/index.js";
describe("MCP server setup", () => {
    it("creates a server from a HonuaClient", () => {
        const client = new HonuaClient({ baseUrl: "http://localhost:5000" });
        const server = createServer(client);
        expect(server).toBeDefined();
    });
});
