---
type: guide
title: "Studio assistant on Bedrock"
description: "Enable the built-in browser assistant with Bedrock, customer authorization, disclosure approval and invocation admission."
---
# Studio assistant on Bedrock

The built-in Studio assistant uses the browser Studio SDK with the configured Server model proxy and canonical MCP tools. The supported contracts are Studio AI capabilities/chat under `/api/v1/studio/ai` and `/mcp`; draft creation is the `honua_studio_create_draft` MCP tool. Standard Server trunk does not implement `/api/v1/agent/workspaces`. No feature flag, AI provider setting, prefix or trailing slash registers that separate workspace API. Console must use an image containing the supported browser transport; an older Console image requires a rebuild.

## Opt in from the Compose quickstart

First initialize the ordinary quickstart credentials. Set these non-secret settings in your per-install `.env`:

```dotenv
HONUA_CONSOLE_IMAGE=ghcr.io/honua-io/honua-console@sha256:<updated-Console-image-digest>
HONUA_STUDIO_AI_REGION=us-east-1
HONUA_STUDIO_AI_MODEL=us.anthropic.claude-sonnet-4-6
HONUA_STUDIO_AI_CLASSIFICATION=unclassified
HONUA_AI_DEPLOYMENT_ID=<stable-installation-identity>
```

Select a rebuilt Console image containing the supported browser assistant transport; the existing quickstart image pin may predate this correction. The 2026.1 default is the tool-capable `us.anthropic.claude-sonnet-4-6` inference profile in `us-east-1`; override the model and region together if your approved configuration differs, and ensure your AWS account can invoke it. Keep the deployment ID stable across restarts and upgrades: replacing it creates a different invocation authority rather than resetting an existing ledger.

Start the optional assistant overlay and Console:

```bash
python3 scripts/docker/quickstart.py --profile console -f docker-compose.yml -f docker-compose.studio-ai.yml
```

The overlay enables the Console realtime editor, selects the supported browser conversation contract, enables Server Studio end-user authorization and the Bedrock provider, selects PostgreSQL invocation admission, and records an exact operator disclosure approval for that model, region, use case and workload classification. Using this overlay asserts that you approve sending this workload to that configured Bedrock boundary. Change the classification to your actual approved workload label; it is not automatic content classification. The default quickstart keeps paid inference disabled.

Use the Server workload's AWS role with `bedrock:InvokeModel` and `bedrock:InvokeModelWithResponseStream` permissions for the selected model/inference profile and its routed model resources. Ensure account/model access is available. Bedrock uses the AWS credential chain and needs no `StudioAiProxy` API key. The overlay does not forward host-shell AWS keys; use the Server workload IAM role or instance profile through the SDK credential chain. Console needs its normal Server binding and customer sign-in, plus `Studio__EmbedJsStudio=true` to display the realtime editor.

## Equivalent settings for a cloud installation

Set these on **Console**:

```dotenv
Studio__EmbedJsStudio=true
Honua__Agent__WorkspaceApiEnabled=false
```

Studio mode is Console's default, so the second setting can be omitted for the built-in assistant. Deployments using the separate external workspace API set `Honua__Agent__WorkspaceApiEnabled=true` and require a matching server implementation.

Put this object in your Server settings document or map the keys to double-underscore environment variables:

```json
{
  "Studio": { "EndUserAuthorization": { "Enabled": true } },
  "StudioAiProxy": {
    "Enabled": true,
    "DefaultProvider": "bedrock",
    "Providers": {
      "bedrock": {
        "Kind": "bedrock",
        "Region": "us-east-1",
        "Model": "us.anthropic.claude-sonnet-4-6",
        "SupportsTools": true
      }
    },
    "Disclosure": {
      "WorkloadDataClassification": "unclassified",
      "Approvals": [{
        "Provider": "bedrock",
        "Kind": "bedrock",
        "Destination": "aws-bedrock:us-east-1",
        "Model": "us.anthropic.claude-sonnet-4-6",
        "UseCase": "studio-chat",
        "DataClassification": "unclassified"
      }]
    }
  },
  "AiExecutionAdmission": {
    "DeploymentId": "<stable-installation-identity>",
    "Backend": "postgres"
  }
}
```

Keep all replicas on the same deployment identity and authoritative backend. Customer authentication must already be configured for Console and Server. `Studio:EndUserAuthorization:Enabled` admits authenticated Studio customers; draft ownership, resource authorization and publication approval still apply. This browser path does not require a Console agent API key, external worker registration or transcript-certification signing key.

## Initialize and resume invocation admission

Provider configuration does not authorize model spending by itself. Apply the normal Server database migrations, including migration 140, and use the canonical operator operations through Console's operation surface or the typed SDK:

1. Execute `admin.ai.admission.initialize` with the `policy` parameter containing the JSON below. Initialization creates a stopped ledger and refuses to overwrite an existing one.
2. Inspect the resulting durable generation, revision and epoch, then execute `admin.ai.admission.resume` for that same deployment through its normal authorization/approval policy. Supply the observed `expectedGeneration`, `expectedRevision` and `expectedEpoch`, or let operation preparation capture them.
3. If a ledger already exists, inspect and use its existing state. Use `stop` and `policy-update` only when intentionally changing the policy; retain its accounting history. Do not replace the deployment ID to bypass a refusal.

A bounded example `policy` value is:

```json
{
  "windowSeconds": 60,
  "maxOperationSeconds": 300,
  "deploymentAttempts": 120,
  "actorAttempts": 30,
  "cohortAttempts": 20,
  "workflowAttempts": 10,
  "deploymentConcurrency": 8,
  "actorConcurrency": 4,
  "cohortConcurrency": 2,
  "workflowConcurrency": 1
}
```

These are invocation-attempt and concurrency limits, not token or monetary budgets. Review suitable limits before resuming. Studio Bedrock does not implement the optional Reporting financial admission mode. See [AI invocation admission](../../reference/configuration/ai-execution-admission.md) for ledger controls, policy fields, backend recovery and refusal codes, and [model disclosure approvals](../../reference/configuration/ai-disclosure-policy.md) for the separate transfer policy.

## Start and diagnose

Sign in to Console, open a map or dashboard in Studio, then select **Start conversation**. The built-in transcript lives in the current browser tab across navigation; Server drafts and saved versions remain durable. Save the draft to retain your work. Separate human approval is still required for publication.

| Result | Meaning and action |
| --- | --- |
| Capabilities report `enabled: false` | Set `StudioAiProxy:Enabled` and configure the provider. Starting a conversation displays an actionable configuration message. |
| Chat returns HTTP 503 with `code: studio_ai_not_configured` | The proxy is disabled. Enable it with the configuration above; no provider request was made. |
| HTTP 403 | Check customer sign-in, Studio end-user authorization and canonical resource permissions. |
| HTTP 503 with an admission `code` | Check stable deployment identity, configured backend, migrations, ledger initialization and resume state. Do not retry by changing identities. |
| HTTP 429 | Invocation/concurrency capacity is exhausted; respect `Retry-After` and inspect the configured policy. |
| Stream error `studio_ai/disclosure_denied` | The configured model/region/use case/classification does not match an exact disclosure approval. |
| `POST /api/v1/agent/workspaces/` returns 404 | Console is using the separate, absent workspace contract. Rebuild/update Console to the supported browser assistant transport. Bedrock settings alone cannot repair that image. |

Capabilities indicate provider configuration, not a live AWS or admission check. No Bedrock inference or AWS resource creation occurs merely by opening Studio or starting the conversation; submitting a prompt can invoke the provider after authorization, disclosure and admission succeed.
