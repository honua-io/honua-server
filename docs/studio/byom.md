# Bring your own model

The 2026.1 **preview** path keeps provider credentials in honua-server. Studio
calls `GET /api/v1/studio/ai/capabilities` and streams
`POST /api/v1/studio/ai/chat`; it does not call model vendors directly.

Set `StudioAiProxy:Enabled`, choose a default provider, and configure one or
more operator-named providers. Supported adapter kinds on the pinned candidate
are `anthropic`, `openai`, and `bedrock`. The `openai` kind accepts compatible
`/chat/completions` endpoints, including Ollama and gis-llm when it offers that
wire contract.

## Local Ollama

Start Ollama separately and pull a tool-capable model according to Ollama's
release documentation. From the server process, prove its endpoint is
reachable. `localhost` inside a container is the container itself, so use a
resolvable host or service name there.

Run `ollama list` and confirm that the tool-capable model you intend to configure
is present. This verifies the Ollama installation through its own CLI without
bypassing the supported client surface with a raw HTTP request.

Configure the candidate:

```bash
export StudioAiProxy__Enabled=true
export StudioAiProxy__DefaultProvider=ollama
export StudioAiProxy__Providers__ollama__Kind=openai
export StudioAiProxy__Providers__ollama__Endpoint=http://127.0.0.1:11434/v1
export StudioAiProxy__Providers__ollama__Model=qwen2.5:7b
```

No API key is required for a default local Ollama endpoint. Protect a remote
endpoint and supply its secret by environment variable or secret reference,
never browser configuration.

For Anthropic use `Kind=anthropic`, an HTTPS API base, a model, and a key or
secret reference. For hosted OpenAI-compatible services use `Kind=openai`,
their API base/model, and provider key. For Bedrock use `Kind=bedrock`, a model
ID and optional region; credentials come from the AWS credential chain.

Non-admin interactive users require
`Studio:EndUserAuthorization:Enabled=true`. API keys, client certificates, and
client-credentials tokens are not accepted as interactive Studio AI users.
The chat endpoint's application-side limit is 30 requests per minute when the
opt-in limiter is enabled with `RateLimiting__Enabled=true`; rate limiting is
off by default. If you leave it disabled, enforce an equivalent limit at your
WAF, API gateway, ingress, or load balancer.

The configuration was checked against candidate source, but no live Ollama
daemon/model was available in the candidate environment. This page therefore
does not claim a successful real-model turn. That receipt remains part of
[honua-studio#41](https://github.com/honua-io/honua-studio/issues/41).
