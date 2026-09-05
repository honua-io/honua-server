# Use the JavaScript Studio agent

`@honua/sdk-js/studio-agent` is experimental and matches the 2026.1 preview
proxy and MCP contracts. It discovers the server's Studio tools, streams a
model turn, executes tool calls in order, and retries one generation conflict.

```ts
import { createHonuaAiMapKit } from "@honua/sdk-js/agent-tools";
import { createStudioAgentSession } from "@honua/sdk-js/studio-agent";

const kit = createHonuaAiMapKit({ runtime, policy: { allowActions: true } });
const session = createStudioAgentSession({
  baseUrl: "https://your-honua.example.com/api",
  auth: { getAccessToken: () => hostSession.getAccessToken() },
  kit,
  system: () => kit.systemPrompt(),
  draft: { draftId, generation },
});

const turn = await session.chat("Add the parcels layer and zoom to it.");
if (turn.status !== "completed") console.warn(turn.errorMessage);
```

The session routes local runtime verbs to the map kit and server-classified
composition tools to `/mcp`. It discovers classification through `tools/list`;
a name prefix alone is not authority. The package marks this entry point
experimental, so preview fields may change before 1.0.
