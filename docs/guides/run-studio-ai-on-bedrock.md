# Run the AI studio flows on AWS Bedrock (Claude)

The Honua AI studio generation flows — workflow generation, dashboard generation, report
generation, and the other natural-language document generators — can run on cloud AI (AWS
Bedrock / Claude) instead of a local model. This removes the need to host a local
Ollama/vLLM/Qwen endpoint: a laptop that can't run a local LLM can still drive the studio
flows against Bedrock using AWS credentials.

The Bedrock provider speaks Bedrock's **Converse API** and uses the standard **AWS credential
chain** (environment variables, shared profile, IAM role, or container/Lambda ambient
credentials). No API key is required — and none is hardcoded; everything is config-driven.

## What runs on Bedrock

Selecting the `bedrock` provider routes these flows through Bedrock:

- Workflow generation (`POST /api/v1/console/workflow-packages/generate`)
- Dashboard generation and report generation
  (`POST /api/v1/console/publications/generate` with `kind=dashboard` / `kind=report`)
- The other generation services that reuse the shared chat plumbing (map, app, form, query)

Structured output is obtained the same way the Anthropic provider does it: a single tool
(`emit_document` / `emit_workflow`) is declared with the proposal JSON schema as its input
schema and the tool choice is forced, so the model must return the proposal as the tool-call
input. The server still runs its structural validation gate on the result — the model output
is never trusted raw.

## Configuration

The studio flows share `WorkflowGeneration` configuration. Add a `bedrock` provider block and
point the default (or a per-request `provider`) at it.

```jsonc
// appsettings.json (or environment-specific override)
"WorkflowGeneration": {
  "Enabled": true,
  "DefaultProvider": "bedrock",          // "local" | "openai" | "anthropic" | "bedrock" | "deterministic"
  "MaxRepairAttempts": 1,
  "Providers": {
    "bedrock": {
      // Bedrock needs only a model id. No Endpoint, no ApiKey — the AWS credential chain (IAM)
      // supplies auth and the region targets the regional Bedrock runtime endpoint.
      "Model": "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
      "Region": "us-west-2",             // optional; defaults to us-west-2
      "TimeoutSeconds": 120,
      "MaxTokens": 4096
    }
  }
}
```

Notes:

- **`Model`** is the Bedrock model id or inference-profile id (for example a `us.anthropic.*`
  cross-region inference profile). It is never defaulted — you must supply it.
- **`Region`** is optional and defaults to `us-west-2`.
- **`Endpoint` and `ApiKey` are not used** by the Bedrock provider. (An optional Bedrock API
  key / bearer token is honored if `ApiKey` is set, but the normal path is the IAM credential
  chain.)
- The default provider is **unchanged** unless you set `DefaultProvider: "bedrock"`; existing
  `local` / `openai` / `anthropic` / `deterministic` providers keep working.

### AWS credentials

Provide credentials the standard AWS way, for example:

```bash
export AWS_REGION=us-west-2
export AWS_ACCESS_KEY_ID=...
export AWS_SECRET_ACCESS_KEY=...
# or rely on a shared profile / an attached IAM role
```

The IAM principal needs `bedrock:InvokeModel` (and `bedrock:InvokeModelWithResponseStream` if
streaming is used) on the chosen model / inference profile.

### Per-request override

A generate request may name the provider explicitly:

```jsonc
POST /api/v1/console/publications/generate
{
  "kind": "dashboard",
  "prompt": "Fleet operations dashboard with a KPI for active vehicles and incidents by region.",
  "provider": "bedrock",
  "model": "us.anthropic.claude-sonnet-4-5-20250929-v1:0"   // optional; overrides the configured model
}
```

## Testing the provider

Offline unit tests (no AWS account) cover the provider wiring — config validation, the
tool-call → proposal mapping, and the dashboard/report routing — in
`tests/dotnet/Honua.Ai.Tests/Source/BedrockStudioProviderTests.cs`.

A live end-to-end test that drives dashboard generation against **real Bedrock** lives in
`BedrockStudioLiveTests.cs`. It is gated on an environment flag so it never runs in CI:

```bash
HONUA_AI_LIVE_BEDROCK=1 \
HONUA_AI_BEDROCK_MODEL="us.anthropic.claude-sonnet-4-5-20250929-v1:0" \
HONUA_AI_BEDROCK_REGION="us-west-2" \
AWS_REGION="us-west-2" \
dotnet test tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj \
  --filter "FullyQualifiedName~BedrockStudioLiveTests"
```

The test prints the generated dashboard document; a `status=generated` turn proves a studio
flow ran end-to-end on cloud AI without a local model.
