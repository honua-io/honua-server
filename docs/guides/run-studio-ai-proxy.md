# Run the Studio AI proxy (Anthropic / OpenAI-compatible / Bedrock)

honua-server#3000 adds a provider-agnostic chat proxy for Honua Studio: the server holds every
model provider's credentials, and Studio clients call one streaming endpoint regardless of which
provider is configured behind it. This is the general-purpose sibling of the one-shot document
generators described in [`run-studio-ai-on-bedrock.md`](./run-studio-ai-on-bedrock.md) — that guide
covers `WorkflowGeneration` (workflow/dashboard/report/map/app/form generation, each forcing a
single structured-output tool call); this guide covers the general streaming chat surface at
`/api/v1/studio/ai/*`. The two features share provider plumbing patterns (config shape, secret
resolution, HTTP resilience) but are configured, and called, independently.

## What this is (and isn't)

- **Is**: a thin, provider-neutral proxy — one request/response contract (messages, optional tools,
  streaming deltas, stop reasons) translated to and from whichever upstream API a configured
  provider speaks.
- **Isn't**: a multi-tenant AI gateway. No response caching, no cost accounting, no per-tenant
  routing rules, no prompt templating. v0 is deliberately minimal — see Non-Goals in
  honua-server#3000.

## Adapters

| Adapter kind | Upstream API | Typical use |
|---|---|---|
| `anthropic` | Anthropic Messages API (`POST {endpoint}/v1/messages`, streaming) | Claude direct from Anthropic |
| `openai` | Any OpenAI-compatible `POST {endpoint}/chat/completions` (streaming) | OpenAI itself, **OpenRouter, LiteLLM, Ollama, vLLM, LM Studio** — all reachable through this one adapter kind by pointing `Endpoint` at the right base URL |
| `bedrock` | AWS Bedrock Converse API, via the same `IBedrockChatClientFactory` / `IChatClient` bridge the `WorkflowGeneration` Bedrock provider already uses | Claude (or any Converse-supported model) on Bedrock, AWS credential chain, no API key |

Each adapter kind can back any number of operator-named providers (a `bedrock` provider using
`us-east-1` and another using `us-west-2`; two `openai`-kind providers, one pointed at OpenRouter and
one at a local vLLM instance) — see Configuration below.

### Why Bedrock is a bridge, not a rewrite

The existing `BedrockChatClientAdapter` (`src/Honua.Ai/Features/Providers/Bedrock/`) already speaks
Bedrock's Converse streaming API as a `Microsoft.Extensions.AI.IChatClient`. The Studio AI proxy's
Bedrock adapter (`BedrockStudioAiProxyAdapter`) is a thin translation layer over that same client —
no new AWS wire code. One difference from the Anthropic/OpenAI-compatible adapters: Bedrock's
Converse API accumulates a tool call's JSON arguments internally and hands the proxy a single
complete function call at the end of the tool-use content block, rather than incremental JSON-text
fragments — so a Bedrock-backed turn's `tool_call_delta` event carries the whole arguments payload
in one chunk instead of a token-by-token stream. The other two adapters stream tool-call arguments
token-by-token, matching their upstream APIs.

## Configuration

Providers are declared under `StudioAiProxy`, keyed by an operator-chosen name (not a fixed id per
adapter — you can declare as many named providers of the same `Kind` as you want):

```jsonc
// appsettings.json (or environment-specific override)
"StudioAiProxy": {
  "Enabled": true,
  "DefaultProvider": "claude",
  "MaxPromptCharacters": 32000,
  "Providers": {
    "claude": {
      "Kind": "anthropic",
      "Endpoint": "https://api.anthropic.com",
      "Model": "claude-sonnet-4-5-20250929",
      "ApiKey": "secret://studio-ai/anthropic-key",   // or a plain value, or HONUA_STUDIOAI_CLAUDE_API_KEY
      "TimeoutSeconds": 120,
      "MaxTokens": 4096
    },
    "openrouter": {
      "Kind": "openai",
      "Endpoint": "https://openrouter.ai/api/v1",
      "Model": "anthropic/claude-sonnet-4.5",
      "ApiKey": "secret://studio-ai/openrouter-key",
      "TimeoutSeconds": 120,
      "MaxTokens": 4096
    },
    "local-vllm": {
      "Kind": "openai",
      "Endpoint": "http://localhost:8000/v1",
      "Model": "Qwen2.5-32B-Instruct",
      "TimeoutSeconds": 180,
      "MaxTokens": 8192
      // No ApiKey — a local endpoint typically needs none.
    },
    "bedrock-claude": {
      "Kind": "bedrock",
      "Model": "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
      "Region": "us-west-2"
      // No Endpoint, no ApiKey — the AWS credential chain (IAM) supplies auth.
    }
  }
}
```

Notes:

- **Credentials never reach a client.** `ApiKey` is resolved server-side only, either as a plain
  value, a secret-store reference (`ISecretProvider`, e.g. `secret://...`), or a per-provider
  environment variable fallback: `HONUA_STUDIOAI_{PROVIDERNAME}_API_KEY` (provider name upper-cased,
  e.g. `HONUA_STUDIOAI_CLAUDE_API_KEY`).
- **`anthropic` providers require HTTPS**; `openai`-kind providers may point at plain-HTTP localhost
  endpoints (Ollama/vLLM/LM Studio) — validated at startup by `StudioAiProxyConfigurationValidator`.
- **`bedrock` providers need only a `Model`** (and optionally `Region`, default `us-west-2`) — no
  `Endpoint`, no `ApiKey` required; the AWS credential chain (env vars, shared profile, IAM role,
  container/Lambda ambient credentials) supplies auth, exactly as for the `WorkflowGeneration`
  Bedrock provider.
- **`DefaultProvider`** selects the provider used when a chat/capabilities request does not name one
  explicitly. Every declared provider is independently callable by name.
- **`SupportsTools`** (default `true`) lets an operator honestly mark a provider/model that doesn't
  reliably tool-call; a chat request with `tools` against such a provider is rejected with a 400
  before any network call.

## Endpoints

Both endpoints require admin authorization (the same posture as the Studio package lifecycle
surface, `WorkflowPackageEndpoints`) pending a dedicated per-session Studio-user authorization scope.

### `GET /api/v1/studio/ai/capabilities`

Returns every declared provider's capability descriptor — REQ-003 of honua-server#3000, so Studio
clients can adapt to context length and tool support without any provider-specific code:

```jsonc
{
  "success": true,
  "data": {
    "enabled": true,
    "defaultProvider": "claude",
    "providers": [
      { "provider": "claude", "kind": "anthropic", "model": "claude-sonnet-4-5-20250929",
        "maxTokens": 4096, "toolSupport": true, "streaming": true, "isDefault": true, "configured": true },
      { "provider": "openrouter", "kind": "openai", "model": "anthropic/claude-sonnet-4.5",
        "maxTokens": 4096, "toolSupport": true, "streaming": true, "isDefault": false, "configured": true },
      { "provider": "bedrock-claude", "kind": "bedrock", "model": "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
        "maxTokens": 4096, "toolSupport": true, "streaming": true, "isDefault": false, "configured": true }
    ]
  }
}
```

### `POST /api/v1/studio/ai/chat`

Streams one chat turn as Server-Sent Events (`Content-Type: text/event-stream`). Request body:

```jsonc
{
  "provider": "claude",              // optional; falls back to StudioAiProxy:DefaultProvider
  "model": "claude-opus-4-1",        // optional per-call override
  "system": "You are a GIS analyst.",
  "messages": [
    { "role": "user", "content": "Summarize the incidents layer." }
  ],
  "tools": [
    {
      "name": "list_incidents",
      "description": "List open incidents.",
      "inputSchema": { "type": "object", "properties": { "status": { "type": "string" } } }
    }
  ],
  "toolChoice": { "mode": "auto" },  // "auto" | "none" | "required" | "specific" (+ "toolName")
  "maxTokens": 2048,
  "temperature": 0.2
}
```

After a `tool_call_stop`, replay the assistant tool call before its result so the provider can
match the result to the pending call:

```json
{
  "messages": [
    {
      "role": "assistant",
      "content": "",
      "toolCalls": [
        { "id": "call_123", "name": "list_incidents", "arguments": { "status": "open" } }
      ]
    },
    {
      "role": "tool",
      "content": "[{\"id\":1}]",
      "toolCallId": "call_123",
      "toolName": "list_incidents"
    }
  ]
}
```

A request that names an unknown or unconfigured provider, has no messages, exceeds
`MaxPromptCharacters`, or asks for tools against a provider with `SupportsTools: false` is rejected
with a normal `400` JSON problem response **before** any SSE headers are written — once the stream
starts, the call has already been validated against a real, configured provider.

The response is a sequence of named SSE events, each carrying a JSON body shaped like the
provider-neutral `StudioAiChatEvent` contract:

| SSE `event:` | Meaning |
|---|---|
| `message_start` | Turn started (emitted once a response is actually received from the provider); `model` names the model actually used. |
| `text_delta` | Incremental assistant text (`text`). |
| `tool_call_start` | A tool call began (`toolCallId`, `toolName`). |
| `tool_call_delta` | Incremental JSON-argument text for a tool call (`toolCallId`, `toolArgumentsDelta`). |
| `tool_call_stop` | A tool call is complete; `toolArguments` carries the full parsed arguments when assembly succeeded. |
| `message_stop` | Turn ended: `stopReason` (`EndTurn`/`ToolCall`/`MaxTokens`/`ContentFilter`/`Cancelled`/`Error`), and `promptTokens`/`completionTokens`/`latencyMs` when the provider reported them. |
| `error` | The call failed before a normal `message_stop` (provider HTTP error, timeout, malformed response, missing credentials, connection failure). A connection-level failure can happen before `message_start` since that event is only emitted once a response is actually received. |

**Cancellation** is via request abort: closing the client connection (browser navigation, an
`AbortController.abort()` on the `fetch`, or the HTTP client disposing the request) stops the
upstream call and ends the SSE stream. There is no separate cancel endpoint — the proxy contract
follows the same "abort the HTTP request" convention as the rest of the platform's streaming
surfaces (`FeatureStreamEndpoints`, `CloudDemoEndpoints`).

## Rate limiting and audit

- `POST /chat` carries an explicit per-endpoint rate limit (30 requests/minute per authenticated
  admin identity), consistent with the `RateLimitAttribute` precedent used for other
  sensitive/expensive admin endpoints (`AdminAuthEndpoints`). This applies **in addition to** the
  platform's opt-in subject-wide rate limiter (`RateLimiting:Enabled`, off by default, ADR-0004);
  edge enforcement remains the default rate-limiting posture.
- **Every call that reaches a known, configured provider is audited exactly once** — success or
  failure, including a client disconnect mid-stream — as an `AuditEventType.AdminAction` /
  `Action = "studio_ai.chat"` record. `ResourceId` is the provider name; `Details` is a JSON blob
  with adapter kind, model, prompt/completion token counts (when the provider reported them),
  latency, and stop reason. **Never** the prompt/response content or any credential. A request
  rejected before a provider is selected (bad body, unknown provider, oversized prompt) is *not*
  audited — there is no action to attribute yet.

## Latency overhead budget (NFR-001)

The proxy adds two costs beyond a direct call to the upstream provider: (1) translating the
provider-neutral request into the adapter's wire format and (2) re-parsing each streamed frame and
re-emitting it as a neutral SSE event. Both are pure in-process CPU work — no extra network hop, no
buffering of the whole response (headers are read with `HttpCompletionOption.ResponseHeadersRead`
and each server-sent frame is forwarded as it arrives).

**Budget:** the proxy's own processing overhead — request translation plus the full round-trip cost
of re-parsing and re-emitting a streamed response — must stay under **100ms** for a realistic
steady-state streamed turn (200 SSE frames), measured as wall-clock time with no network involved (a
canned in-process stream, JIT-warmed before the timed run). This is enforced by
`StudioAiProxyLatencyTests`
(`tests/dotnet/Honua.Ai.Tests/Source/StudioAiProxy/StudioAiProxyLatencyTests.cs`), which never talks
to a live provider — it feeds a fixture SSE stream through the OpenAI-compatible adapter's parser and
asserts total elapsed time.

This budget covers the proxy's own overhead only; it does not — and cannot — bound the upstream
provider's own time-to-first-token, which varies by provider, model, and prompt.

## Testing the adapters

Offline tests (no live provider calls; safe in CI) cover:

- **Adapter contract tests** (`AnthropicStudioAiProxyAdapterTests.cs`, `OpenAiCompatibleStudioAiProxyAdapterTests.cs`,
  `BedrockStudioAiProxyAdapterTests.cs`): each adapter against a canned HTTP response (Anthropic SSE
  frames, OpenAI-compatible chunks) or a fake `IChatClient` (Bedrock), asserting the translated
  `StudioAiChatEvent` sequence.
- **Streaming-parse tests**: text deltas, tool-call start/delta/stop assembly (including OpenAI's
  interleaved multi-tool-call-by-index case), usage/token capture, and the truncation/timeout/HTTP
  error paths.
- **Config validator tests** (`StudioAiProxyConfigurationTests.cs`): known/unknown `Kind`, per-kind
  required fields (HTTPS for `anthropic`, `Model`-only for `bedrock`), `DefaultProvider` must match a
  declared provider.
- **Endpoint authz + audit tests** (`tests/dotnet/Honua.Server.Tests/Features/StudioAi/StudioAiProxyEndpointsTests.cs`):
  anonymous → `401`; admin → `200`; a call to a known-but-unreachable provider produces exactly one
  `Outcome = Failure` audit record with the expected `ResourceId`/`Action`.

No test in this feature calls a real Anthropic/OpenAI/Bedrock endpoint.
