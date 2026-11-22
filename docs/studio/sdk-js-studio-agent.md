---
type: guide
title: "Use the JavaScript Studio agent"
description: "@honua/sdk-js/studio-agent is experimental and matches the 2026.1 preview proxy and MCP contracts."
---
# Use the JavaScript Studio agent

`@honua/sdk-js/studio-agent` is experimental and matches the 2026.1 preview
proxy and MCP contracts. It discovers the server's Studio tools, streams a
model turn and executes tool calls in order. When a composition tool reports a
generation conflict, the session reloads the draft and returns the conflict with
the refreshed draft. It never replays the rejected change on its own.

```ts
import { createHonuaAiMapKit } from "@honua/sdk-js/agent-tools";
import { McpClient, createStudioAgentSession } from "@honua/sdk-js/studio-agent";

const auth = { getAccessToken: () => hostSession.getAccessToken() };
const kit = createHonuaAiMapKit({ runtime, policy: { allowActions: true } });
const session = createStudioAgentSession({
  baseUrl: "https://your-honua.example.com/api",
  auth,
  // MCP is served at the server root (/mcp), not under /api.
  mcpClient: new McpClient({ baseUrl: "https://your-honua.example.com", auth, workflowView: "setup" }),
  kit,
  system: () => kit.systemPrompt(),
  draft: { draftId, generation },
});

const turn = await session.chat("Add the parcels layer and zoom to it.");
if (turn.status !== "completed") console.warn(turn.errorMessage);
```

One `baseUrl` would send chat to `${baseUrl}/v1/studio/ai/chat` and MCP to
`${baseUrl}/mcp`. Honua serves Studio AI under `/api/v1/studio/ai/*` and MCP at
the root `/mcp`, so `/api/mcp` returns 404. Pass a separate `mcpClient` built on
the server root, as Honua Studio does. The `setup` workflow view lists the Studio
composition tools.

The session routes local runtime verbs to the map kit and server-classified
composition tools to `/mcp`. It discovers classification through `tools/list`;
a name prefix alone is not authority. The package marks this entry point
experimental, so preview fields may change before 1.0.
