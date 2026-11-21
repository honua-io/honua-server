---
type: guide
title: "Use the JavaScript Studio agent"
description: "@honua/sdk-js/studio-agent is experimental and matches the 2026.1 preview proxy and MCP contracts."
---
# Use the JavaScript Studio agent

`@honua/sdk-js/studio-agent` is experimental and matches the 2026.1 preview
proxy and MCP contracts. It discovers the server's Studio tools, streams a
model turn and executes tool calls in order. When a composition tool reports a
generation conflict (`failed_precondition`), the session reloads the draft with
`honua_studio_get_draft` and returns the conflict with the refreshed draft. It
never replays the rejected mutation; the caller decides what to do next.

```ts
import { createHonuaAiMapKit } from "@honua/sdk-js/agent-tools";
import { McpClient, createStudioAgentSession } from "@honua/sdk-js/studio-agent";

const auth = { getAccessToken: () => hostSession.getAccessToken() };
const kit = createHonuaAiMapKit({ runtime, policy: { allowActions: true } });
const session = createStudioAgentSession({
  baseUrl: "https://your-honua.example.com/api",
  mcpClient: new McpClient({
    baseUrl: "https://your-honua.example.com",
    auth,
    workflowView: "setup",
  }),
  auth,
  kit,
  system: () => kit.systemPrompt(),
  draft: { draftId, generation },
});

const turn = await session.chat("Add the parcels layer and zoom to it.");
if (turn.status !== "completed") console.warn(turn.errorMessage);
```

The session routes local runtime verbs to the map kit and server-classified
composition tools to MCP. It discovers classification through `tools/list`;
a name prefix alone is not authority. The package marks this entry point
experimental, so preview fields may change before 1.0.

The two planes have different roots. Studio AI chat is under
`/api/v1/studio/ai/*`, so the session's `baseUrl` ends in `/api`. MCP is at the
server root (`/mcp`), not under `/api`. Without its own `mcpClient`, the session
sends MCP calls to `${baseUrl}/mcp`, which is `/api/mcp` here and answers 404.
Pass an `McpClient` whose `baseUrl` is the server root, as above. Its
`workflowView: "setup"` lists the Studio tools.
