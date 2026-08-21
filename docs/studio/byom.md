# Bring your own model

Studio sends provider-neutral chat turns to
`POST /api/v1/studio/ai/chat`; model credentials stay in honua-server. Enable
the proxy and declare one or more named providers under `StudioAiProxy`:

```json
{
  "StudioAiProxy": {
    "Enabled": true,
    "DefaultProvider": "claude",
    "Providers": {
      "claude": {
        "Kind": "anthropic",
        "Endpoint": "https://api.anthropic.com",
        "Model": "claude-sonnet-4-5-20250929",
        "ApiKey": "secret://studio-ai/anthropic"
      },
      "openai": {
        "Kind": "openai",
        "Endpoint": "https://api.openai.com/v1",
        "Model": "gpt-5",
        "ApiKey": "secret://studio-ai/openai"
      },
      "bedrock": {
        "Kind": "bedrock",
        "Model": "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
        "Region": "us-west-2"
      }
    }
  }
}
```

`anthropic` uses the Anthropic Messages API. `openai` means any
OpenAI-compatible chat-completions endpoint, including OpenAI, OpenRouter,
LiteLLM, vLLM, LM Studio, and Ollama. `bedrock` uses the AWS credential chain;
do not configure an API key for it. `ApiKey` may be a server secret reference
or the provider-specific `HONUA_STUDIOAI_{PROVIDER}_API_KEY` environment value.

## Runnable Ollama example

```bash
ollama serve
ollama pull qwen2.5:7b
```

Then add:

```json
{
  "StudioAiProxy": {
    "Enabled": true,
    "DefaultProvider": "local-ollama",
    "Providers": {
      "local-ollama": {
        "Kind": "openai",
        "Endpoint": "http://host.docker.internal:11434/v1",
        "Model": "qwen2.5:7b",
        "TimeoutSeconds": 180,
        "MaxTokens": 4096
      }
    }
  }
}
```

Use `http://localhost:11434/v1` when honua-server runs directly on the host.
No Ollama API key is required. Confirm the route with
`GET /api/v1/studio/ai/capabilities` before opening Studio.

The AI routes use the same authorization policy as the lifecycle API. Admins
are always admitted. Authenticated non-admins are admitted only when
`Studio:EndUserAuthorization:Enabled=true`; otherwise they receive `403`, and
anonymous callers receive `401`. Chat retains its explicit limit of 30 requests
per minute per authenticated identity, in addition to optional platform-wide
rate limiting. Every call reaching a configured provider is attributed to the
actual caller in the audit log.

`gis-llm` is not a `StudioAiProxy` adapter kind. Run it as an external model/MCP
host and connect it to Honua's `/mcp` endpoint; its provider configuration stays
with that host. See [Drive Studio from Claude Desktop](drive-from-claude-desktop.md)
for the same external-host pattern.
