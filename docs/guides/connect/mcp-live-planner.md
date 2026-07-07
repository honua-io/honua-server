# Turn on the live MCP planner (Honua-brings-LLM)

By default the `honua_plan_analysis` MCP tool runs in **fixture (demo) mode**: it replays a
fixed set of canned plans for a handful of demo intents and returns a structured `rejected`
turn for anything else. Every fixture response is flagged with `engine: "fixture"`, and the
tool description says so, so a client (or a cold client LLM) never mistakes a demo template for
a plan compiled from its intent.

This guide is the **supported configuration profile** that turns the planner live, so
`honua_plan_analysis` compiles an arbitrary natural-language GIS intent into an executable
analysis plan on the server ("Honua brings the LLM"), rather than replaying fixtures.

> The planner reuses the [studio `WorkflowGeneration` provider plumbing](../run-studio-ai-on-bedrock.md).
> Configure a provider there once; the planner selects among the same providers.

## Edition / tier policy

Honua ships **no model credentials for any edition** — the model is always brought by the
operator (a local endpoint, an Anthropic key, or the AWS credential chain for Bedrock). The
live planner is therefore activated the same way on every edition — by configuring a provider —
but the intended posture differs by edition:

| Edition | Planner posture | How |
|---|---|---|
| **Community** | Bring-your-own-model. Fixture (demo) mode until you configure a provider. | Set the config profile below with your own provider. |
| **Pro / Enterprise** | Live planner is the supported default. | Apply the config profile below; the same provider that runs the studio flows drives the planner. |

The planner is **not** silently defaulted on by edition. Because Honua provisions no keys, an
edition-based auto-on would fail closed at request time (no credentials → provider error). The
supported activation is always an explicit provider configuration — this profile — which is why
it is documented per edition rather than flipped by a license bit. The mutating lanes
(`honua_execute_plan`) remain governed by the edition entitlement ladder
(`ai.spec-apply` / `ai.agent-operations` / `ai.approval-workflows`) independently of which
planner engine is running.

## Configuration profile

Add the two sections below. `WorkflowGeneration` owns the provider credentials (endpoints,
models, regions, keys); `PlanAnalysis` selects among them for the MCP plan lane.

```jsonc
// appsettings.json (or an environment-specific override)
{
  // Provider plumbing — shared with the studio generation flows.
  "WorkflowGeneration": {
    "Enabled": true,
    "DefaultProvider": "bedrock",       // "local" | "openai" | "anthropic" | "bedrock" | "deterministic"
    "Providers": {
      "bedrock": {
        "Model": "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
        "Region": "us-west-2",
        "TimeoutSeconds": 120,
        "MaxTokens": 4096
      }
    }
  },

  // MCP plan lane — the dedicated planner gate (optional; see precedence below).
  "PlanAnalysis": {
    "Enabled": true,                    // force the live planner on for honua_plan_analysis
    "Provider": "bedrock"               // optional: override which WorkflowGeneration provider the plan lane uses
  }
}
```

### Precedence (how the engine is selected)

The planner picks live vs. fixture as follows (see `ShouldUseLivePlanner`):

1. **`PlanAnalysis:Enabled`** is authoritative when present — `true` forces the live planner on
   for the MCP plan lane, `false` pins it to fixtures even when the studio flows are live.
2. When `PlanAnalysis:Enabled` is unset, the planner **inherits `WorkflowGeneration:Enabled`**
   (back-compat: the planner rode entirely on the studio gate before the dedicated seam).
3. The **effective provider** must be a *live* provider — `PlanAnalysis:Provider` when set, else
   `WorkflowGeneration:DefaultProvider`. If the effective provider is `deterministic`, the
   deterministic fixture replay is selected regardless of the gate.

So the minimal "planner on" profile is either:

- `PlanAnalysis:Enabled=true` + `PlanAnalysis:Provider=<live provider>`, **or**
- `WorkflowGeneration:Enabled=true` + `WorkflowGeneration:DefaultProvider=<live provider>`
  (and leave `PlanAnalysis` unset).

`PlanAnalysis:Provider` must reference a provider id registered under
`WorkflowGeneration:Providers` (`local`, `openai`, `anthropic`, `bedrock`, or `deterministic`);
an unknown id fails startup validation. `PlanAnalysis` carries no connection block of its own —
endpoints, models, regions, and keys stay owned by `WorkflowGeneration:Providers`.

### Credentials

Provider credentials are configured exactly as for the studio flows — see
[Run the AI studio flows on AWS Bedrock](../run-studio-ai-on-bedrock.md) for the Bedrock/IAM
setup, or set an `ApiKey` on an `anthropic`/`openai` provider block. The IAM principal for
Bedrock needs `bedrock:InvokeModel` on the chosen model / inference profile.

## Verify

With the profile applied and credentials present, call the tool with a novel intent that is not
in the fixture set:

```bash
BASE=http://localhost:8080
curl -s -X POST "$BASE/mcp" -H "Content-Type: application/json" -H "X-API-Key: $HONUA_API_KEY" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
        "name":"honua_plan_analysis",
        "arguments":{"intent":"Buffer the Maui flood-hazard layer by 500 m and select intersecting parcels."}}}'
```

- **Live planner on:** the result's `structuredContent.engine` is `"live"` and `status` is
  `planned` (or `clarification_required` with real reasoning), compiled from your intent.
- **Still in fixture mode:** `engine` is `"fixture"` and an unmatched novel intent returns
  `status: "rejected"` whose `reason` explains fixture mode. Re-check the precedence rules above
  (most often the effective provider resolved to `deterministic`, or the gate is off).

A `planned` turn feeds straight into `honua_validate_plan`, `honua_dry_run_plan`, and
`honua_execute_plan` unchanged — the live planner emits the same canonical plan shape the
fixture path does.

## Testing the live lane

The live plan lane is covered on two levels:

- **Deterministic (runs in CI, no credentials).** The live planner path is exercised with a
  fake provider so the live code path is proven without any AI call, including that the live
  planner's compiled plan flows **beyond `plan_analysis`** — it converts to a domain plan and
  passes through the same validator the `honua_validate_plan` lane uses —
  `tests/dotnet/Honua.Ai.Tests/Source/LivePlanAnalysisServiceTests.cs`.
- **Real model transport (gated).** The Bedrock/Converse seam the planner reuses is driven
  end-to-end against **real** AWS Bedrock by a gated live test that never runs in CI without
  credentials — `tests/dotnet/Honua.Ai.Tests/Source/BedrockStudioLiveTests.cs`:

  ```bash
  HONUA_AI_LIVE_BEDROCK=1 \
  HONUA_AI_BEDROCK_MODEL="us.anthropic.claude-sonnet-4-5-20250929-v1:0" \
  HONUA_AI_BEDROCK_REGION="us-west-2" \
  AWS_REGION="us-west-2" \
  dotnet test tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj \
    --filter "FullyQualifiedName~BedrockStudioLiveTests"
  ```

  To run the gated lane in CI, provide the same provider credentials as a CI secret (the Bedrock
  IAM role or an `anthropic` API key) and set `HONUA_AI_LIVE_BEDROCK=1` (or the matching
  `HONUA_AI_LIVE_*` flag for your provider).

The downstream lanes (`honua_validate_plan`, `honua_dry_run_plan`, `honua_execute_plan`) are
protocol-shared and deterministic: they consume the live planner's plan object unchanged, so no
separate "live" engine is needed for them — the live lane is the planner, and the rest of the
pipeline is the same code path the fixture plans already flow through.

## Next steps

- [Connect AI agents to Honua over MCP](ai-agents-mcp.md)
- [Run the AI studio flows on AWS Bedrock](../run-studio-ai-on-bedrock.md)
- [Editions and licensing](../../concepts/editions-and-licensing.md)
