# Use StudioAgentSession from JavaScript

`@honua/sdk-js/studio-agent` combines the Studio AI SSE transport, local map
tools, and server composition tools into one bounded conversation loop.

```ts
import { createHonuaAiMapKit } from "@honua/sdk-js/agent-tools";
import { createStudioAgentSession } from "@honua/sdk-js/studio-agent";

const kit = createHonuaAiMapKit({
  runtime,
  policy: { allowActions: true },
});

const session = createStudioAgentSession({
  baseUrl: "/api",
  auth: async () => oidc.getAccessToken(),
  kit,
  system: () => kit.systemPrompt(),
  provider: "local-ollama",
  draft: { draftId, generation },
  onEvent: event => console.debug(event),
});

const turn = await session.chat("Add parcels, color them by zoning, and fit the view.");
if (turn.status !== "completed") {
  console.warn(turn.errorMessage);
}
```

`baseUrl` defaults to `/api`; the session calls `/v1/studio/ai/*` and `/mcp`
below it. `chat()` returns `completed`, `cancelled`, `error`, or `refused` and
does not throw for a mid-stream provider failure. The session automatically
tracks the generation returned by composition tools and performs one safe
reload/retry on a generation conflict.

An agent session may propose publication but cannot approve it. Keep the
publish confirmation and resulting request polling in a human-driven UI.
