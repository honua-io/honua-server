---
type: reference
title: "AI model disclosure approvals"
description: "Operator approval configuration, migration and limits for AI model data transfers."
---
# AI model disclosure approvals

Remote Studio chat, report narrative enrichment and imagery inference require an explicit operator approval for the effective provider, adapter kind, destination, model, use case and workload data classification. Missing or mismatched approvals deny transfer before invoking a provider. There are no wildcard approvals. The Studio capabilities `Configured` flag continues to describe credential/model readiness; it does not mean external disclosure has been approved. Administrators' chat model overrides and imagery jobs' model inputs must match too.

`Disclosure` is nested under `StudioAiProxy`, `Reporting:Narrative` or `Geoprocessing:ImageryInference`. The same shape applies to each:

```json
{
  "StudioAiProxy": {
    "Disclosure": {
      "WorkloadDataClassification": "unclassified",
      "Approvals": [{
        "Provider": "primary",
        "Kind": "openai",
        "Destination": "https://models.example/v1",
        "Model": "approved-model",
        "UseCase": "studio-chat",
        "DataClassification": "unclassified"
      }]
    }
  }
}
```

The equivalent deployment environment variables follow ADR-0008:

```bash
StudioAiProxy__Disclosure__WorkloadDataClassification=unclassified
StudioAiProxy__Disclosure__Approvals__0__Provider=primary
StudioAiProxy__Disclosure__Approvals__0__Kind=openai
StudioAiProxy__Disclosure__Approvals__0__Destination=https://models.example/v1
StudioAiProxy__Disclosure__Approvals__0__Model=approved-model
StudioAiProxy__Disclosure__Approvals__0__UseCase=studio-chat
StudioAiProxy__Disclosure__Approvals__0__DataClassification=unclassified
```

For reporting, use the prefix `Reporting__Narrative__Disclosure`; for imagery use `Geoprocessing__ImageryInference__Disclosure`.

Use your configured Studio provider key for `Provider`. Reporting uses its configured provider (`openai`) with kind `openai` and use case `reporting-narrative`. Imagery uses its configured provider (`http`) with kind `http` and use case `imagery-inference`. Match the configured endpoint base for Studio/reporting, and the invocation URL for imagery. HTTPS is required for remote destinations. URI normalization preserves the path, including trailing slashes and uses normal URI host/port normalization; query strings, fragments and embedded credentials are rejected. Model, use-case and classification comparisons are case sensitive; provider and kind are case insensitive.

For Bedrock, use kind `bedrock` and destination `aws-bedrock:<region>`, such as `aws-bedrock:us-east-1`. An omitted region uses the Studio default `us-east-1`. The actual SDK client ignores ambient environment/profile endpoint URL overrides, refuses redirects and uses its configured AWS regional endpoint. Standard AWS regional endpoints, including private DNS resolving those endpoints, continue to work. This does not establish data residency: cross-region inference profiles, provider terms, retention and downstream model routing still require operator review.

`WorkloadDataClassification` is an **operator assertion about all inputs that workload may submit**. It is not a content detector, content sanitization, trusted per-dataset label or caller attestation. The default is `unclassified`; sending that data remotely requires an approval explicitly naming `unclassified`. Before assigning a more specific label, operators must enforce the corresponding restrictions on all prompts, system text, tool schemas/results, report titles/descriptions/slot text and imagery admitted to that workload. This implementation cannot prove arbitrary caller text or images fit the asserted class. Do not classify a workload as public merely to enable a provider.

The local compatibility exception applies only to OpenAI-compatible or imagery HTTP adapters using `localhost` or literal loopback IP addresses, with redirects disabled. Private network addresses, local-looking hostnames and Bedrock have no implicit approval. A local model gateway may itself forward data; operators remain responsible for its downstream routing. Redirects are never approval grants.

## Migration and behavior

Disabled AI features and deterministic reports continue to work. Existing remote AI deployments must add explicit approvals before upgrading. Empty approval lists deny remote chat with a validation error (or a terminal `studio_ai/disclosure_denied` event for direct service calls), leave report narratives deterministic and fail imagery jobs with a sanitized disclosure error. The narrative provider evaluates approval before its response-cache lookup. Previously generated reports follow their existing report retention and authorization rules. No database migration or request contract change is required, and no caller classification field is introduced or trusted.

Studio and reporting use startup options snapshots. Imagery captures one options snapshot at job start; updated configuration applies to subsequent jobs. For immediate withdrawal, cancel or drain in-flight work and restart with the reviewed policy. Review approvals after changing providers, models, endpoints, workload inputs or provider terms. Forbidden, missing or stale qualification evidence remains unresolved; these deterministic gates alone do not establish framework alignment or installed deployment qualification. Installed deployment and live revocation qualification remain pending.

Implementation: [shared evaluator](../../../src/Honua.Core/Features/Security/AiDisclosurePolicy.cs), [Studio dispatch](../../../src/Honua.Ai/Features/StudioAiProxy/StudioAiProxyService.cs), [imagery transfer](../../../src/Honua.Geoprocessing/Features/Geoprocessing/Inference/HttpImageryInferenceClient.cs), [report narrative](../../../src/Honua.Server/Features/Reporting/OpenAiNarrativeProvider.cs).


Model calls also require [AI invocation admission](ai-execution-admission.md). Disclosure approval
does not initialize a budget ledger or authorize a stopped generation.
