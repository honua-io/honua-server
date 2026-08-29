# Run Studio AI on Amazon Bedrock

The Studio AI proxy can use Amazon Bedrock Converse without exposing AWS
credentials to Studio clients. This is an adapter configuration for the same
provider-neutral `/api/v1/studio/ai/*` surface described in
[Run the Studio AI proxy](run-studio-ai-proxy.md), not a second inference path.

> **Off by default.** The proxy remains disabled until
> `StudioAiProxy__Enabled=true` is set. Bedrock configuration does not enable
> it implicitly.

## Prerequisites

- AWS credentials available through the standard SDK chain (workload role,
  container credentials, environment, or a local shared profile).
- Permission to invoke the selected Bedrock model in the configured region.
- A model ID supported by Bedrock Converse in that account and region.

## Configure a provider

```json
{
  "StudioAiProxy": {
    "Enabled": true,
    "DefaultProvider": "bedrock_claude",
    "Providers": {
      "bedrock_claude": {
        "Kind": "bedrock",
        "Model": "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
        "Region": "us-west-2",
        "TimeoutSeconds": 120,
        "MaxTokens": 4096,
        "SupportsTools": true
      }
    }
  }
}
```

For environment-based deployment, use
`StudioAiProxy__Enabled=true`,
`StudioAiProxy__DefaultProvider=bedrock_claude`, and the corresponding nested
`StudioAiProxy__Providers__bedrock_claude__*` variables. Provider names used in
shell environment-variable assignments must contain only letters, digits, and
underscores.

By default, the AWS SDK resolves credentials through its standard chain and
uses the regional service endpoint, so no API key or endpoint setting is
required. Bedrock API keys are also supported as optional bearer tokens. Set
`StudioAiProxy__Providers__bedrock_claude__ApiKey` directly, or set it to a
secret reference supported by the configured secret provider. Keep credentials
out of checked-in configuration.

## Verify

With admin authorization, call
`GET /api/v1/studio/ai/capabilities`. The named provider should report
`kind: bedrock`, the configured model, and `configured: true`. Then send a
small turn to `POST /api/v1/studio/ai/chat` and confirm the SSE stream ends in
`message_stop`.

If capabilities reports the proxy disabled, check the explicit `Enabled`
switch. Access-denied and unavailable-model errors come from Bedrock; verify
the workload role, model access, region, and model ID. The proxy audits
provider/model/result metadata but never prompt content or credentials.

## Limits

- This is a thin Converse adapter, not a multi-tenant model gateway.
- Provider/model cost controls and model-access approval remain AWS account
  responsibilities.
- Tool argument deltas arrive as one completed payload for Bedrock rather than
  token-by-token fragments.
