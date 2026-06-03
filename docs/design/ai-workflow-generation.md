# AI Workflow Generation — Backend Scoping & Handoff

**Status:** Proposed · scoping for implementation handoff
**Owner (UI side):** honua-console `Studio · Workflow · New from prompt` (`StudioWorkflowAiPage`)
**Audience:** the engineer/agent implementing the honua-server side
**Goal:** turn a natural-language prompt into a real, validated `workflow.package`
draft, using a pluggable LLM provider — a **local GIS-tuned model as the default**,
with **Claude** and **OpenAI/GPT** as selectable alternatives.

---

## 1. TL;DR — what this is and is not

This is **not** a greenfield "build an LLM stack" task. honua-server already has:

- A complete **AI Builder pipeline** (prompt → ground → clarify → plan-analysis →
  preview/validate package → dry-run → execute → job), documented in
  `docs/ai-builder-sdk-contract.md`.
- A pluggable planner seam, `IPlanAnalysisService`
  (`src/Honua.Ai/Features/AiBuilder/Planning/IPlanAnalysisService.cs`) — **fixture-only
  today**, explicitly designed for a host to replace with a live planner.
- A reference **live LLM client**: `OpenAiNlQueryPlanProvider`
  (`src/Honua.Ai/Features/NlQuery/OpenAiNlQueryPlanProvider.cs`) — OpenAI-compatible
  `/v1/chat/completions`, **structured JSON-schema output**, config + secret/env API key
  via `NlQueryConfiguration`. This already covers a **local** model (point the endpoint at
  Ollama/vLLM/LiteLLM) and **GPT** (point it at OpenAI).
- A declared workflow package family: `PackageReviewFamilies.Workflow` already exists
  (`src/Honua.Core/Features/PackageReview/Domain/`), with `IPackageFamilyReviewAdapter`
  as the extension seam — **no workflow adapter is wired yet.**
- The authoring substrate: `IWorkflowNodeRegistry` (the node palette),
  `WorkflowPackageGraphValidator` (acyclic + sink + known-type + required-param checks),
  immutable versions, dry-run, publish, and the Redis-backed jobs system.

The **work** is therefore four well-bounded pieces:

1. A **live, provider-pluggable planner** that emits a `workflow.package` graph,
   grounded in the node registry (mirrors the NlQuery provider pattern; adds a Claude
   adapter).
2. A **workflow projection + validation gate** so LLM output is never trusted raw — it
   passes through `WorkflowPackageGraphValidator` before it reaches the client.
3. A **console-facing HTTP endpoint** (`POST /api/v1/console/workflow-packages/generate`)
   — the console speaks admin HTTP, **not MCP**. This is the contract the console UI is
   already built against (see §5, the wire contract is frozen).
4. **Config, secrets, provider selection, and a deterministic fixture mode** for CI.

The console UI for this is **already implemented** against the §5 contract using the
repo's standard missing-binding pattern: until this endpoint exists, the page shows an
honest "AI generation isn't available on this server yet" state — it is not a mock.

---

## 2. Existing pieces to reuse (do not reinvent)

| Concern | Existing type / file | Reuse as |
| --- | --- | --- |
| Planner seam | `IPlanAnalysisService` · `Honua.Ai/Features/AiBuilder/Planning` | Implement a **live** impl; keep `FixturePlanAnalysisService` as the deterministic fixture mode |
| LLM client (reference) | `OpenAiNlQueryPlanProvider` · `Honua.Ai/Features/NlQuery` | Template for the OpenAI-compatible provider (local + GPT); copy the structured-output + telemetry + timeout/cap shape |
| LLM config + secret | `NlQueryConfiguration` (`Provider`/`Endpoint`/`Model`/`ApiKey`/env `HONUA_*`) | Template for `WorkflowGenerationConfiguration` |
| Grounding | `IGroundingService` · `Honua.Core/Features/Grounding` | Resolve real datasets/layers/connections/templates the prompt names; surface ambiguities |
| Clarification protocol | `McpClarificationEnvelope` / `McpClarificationQuestionView` / `McpClarificationOptionView` | Map 1:1 onto the console clarification cards (§5.3) |
| Capability states | `supported` / `degraded` / `unsupported` / `auth_denied` / `oversized` (string literals, AI-builder contract) | Reuse verbatim in the generate response |
| Node palette | `IWorkflowNodeRegistry.GetSnapshotAsync()` → `WorkflowNodeRegistrySnapshot` | Ground the system prompt; validate generated node types |
| Graph validation | `WorkflowPackageGraphValidator.Validate(graph, nodeDefs, hash?)` | **Mandatory gate** on every generated graph |
| Package review seam | `IPackageFamilyReviewAdapter` + `PackageReviewFamilies.Workflow` | Optional: a `WorkflowPackageFamilyReviewAdapter` for the MCP `honua_*_package` surface |
| Jobs | `Honua.Jobs` control plane | If generation should be async for big prompts (optional; see §6) |

---

## 3. Architecture

```
                         POST /api/v1/console/workflow-packages/generate   (admin HTTP — console)
                                              │
                                              ▼
                         WorkflowGenerationService  (new, Honua.Server or Honua.Ai)
                    ┌─────────────┬────────────────┬──────────────────────────┐
                    ▼             ▼                ▼                          ▼
            IGroundingService  IWorkflowNodeRegistry   IWorkflowGenerationProvider   WorkflowPackageGraphValidator
            (resolve real      (palette → system        (LLM: local | claude | gpt)   (acyclic, sink, known types,
             layers/conns,      prompt grounding +       structured-output graph        required params) — HARD GATE
             ambiguities)       node-type whitelist)     proposal                       on the produced graph
                    │                                          │                          │
                    └───────────── clarifications ◄────────────┘                          │
                                              │                                            │
                                              ▼                                            ▼
                                   WorkflowGenerationResult  (graph + status + clarifications + validation + provider/model + capabilityState)
```

The same provider seam can also back the MCP `honua_plan_analysis` workflow path and a
`WorkflowPackageFamilyReviewAdapter`, but the **console endpoint is the priority** — it is
what unblocks the shipped UI.

### 3.1 Provider seam

Mirror `INlQueryPlanProvider`:

```csharp
namespace Honua.Core.Features.WorkflowPackages.Generation.Abstractions;

public interface IWorkflowGenerationProvider
{
    /// <summary>Stable id: "local" | "anthropic" | "openai".</summary>
    string ProviderId { get; }

    /// <summary>True when configured + reachable (endpoint/key present, model set).</summary>
    bool IsConfigured { get; }

    Task<WorkflowGenerationProposal> GenerateAsync(
        WorkflowGenerationProviderRequest request,   // prompt, prior turns, answered clarifications,
                                                     // current graph (refine), grounded node whitelist,
                                                     // grounded candidates
        CancellationToken cancellationToken = default);
}
```

Concrete implementations:

- **`OpenAiCompatibleWorkflowGenerationProvider`** — covers **local** (`ProviderId="local"`,
  endpoint → Ollama/vLLM/LiteLLM, the GIS-tuned model) **and** **GPT**
  (`ProviderId="openai"`, endpoint → `api.openai.com`). Same code, different config block.
  Copy the structured-output approach from `OpenAiNlQueryPlanProvider`: a strict
  `json_schema` response format whose schema **is the workflow graph** (nodes with
  `nodeTypeId` constrained to an `enum` of the grounded registry node-type ids, edges,
  schedule, ports). Temperature 0. Cap prompt chars (the NlQuery provider caps at 8 000).
- **`AnthropicWorkflowGenerationProvider`** — `ProviderId="anthropic"` (Claude). New adapter:
  Anthropic Messages API differs from OpenAI (`/v1/messages`, `x-api-key` + `anthropic-version`
  headers, `system` top-level, tool-use / `tool_choice` for structured output instead of
  `response_format`). Same `WorkflowGenerationProposal` output. No SDK dependency required —
  hand-rolled `HttpClient` + source-gen JSON, consistent with the NlQuery provider.
- **`DeterministicWorkflowGenerationProvider`** — `ProviderId="deterministic"`, fixture replay
  for CI/eval (mirror `DeterministicNlQueryPlanProvider` + the AI-builder fixtures). No network.

### 3.2 The GIS-tuned local model

The "GIS-tuned" model is a deployment/ops concern, not a code concern: it is any
OpenAI-compatible server (Ollama `ollama serve`, vLLM, LiteLLM) hosting a fine-tuned model,
selected by config (`Provider="local"`, `Endpoint="http://localhost:11434/v1"`,
`Model="honua-gis-..."`). Code path is identical to GPT. **Fine-tuning the model itself,
the training corpus, and the eval set are out of scope for this code task** — but the
generation system prompt + node-registry grounding + the deterministic eval fixtures (§7)
are the substrate that makes a tuned model useful and measurable, so build those well.

### 3.3 Grounding (make node selection real, not hallucinated)

Two grounding inputs feed the provider's system prompt so the model proposes only things
that exist:

1. **Node whitelist** — `IWorkflowNodeRegistry.GetSnapshotAsync()`; pass `nodeTypeId`,
   `title`, `description`, `category`, `parameterSchemas`, port schemas. The structured-output
   schema constrains `nodeTypeId` to this enum so the model **cannot invent node types**.
2. **Data candidates** — `IGroundingService` resolves the layers/connections/services the
   prompt names ("parcels_2024", "the SFTP connection", "public-works FeatureServer"). When a
   reference is ambiguous or missing, emit a **clarification** (§3.4) instead of guessing.

> Today grounding resolves datasets/processes/templates/styles/deployments but does **not**
> consult `IWorkflowNodeRegistry`. Extending grounding to include node types is the cleanest
> home for the whitelist, but a first cut can read the registry directly in
> `WorkflowGenerationService`.

### 3.4 Clarifications

When grounding or the model is unsure (which of two layers, which target FeatureServer,
cron vs manual, retention window), return `status = "needs-clarification"` with structured
questions rather than a graph. The console renders these as selectable cards and re-calls
`generate` with the answers. Reuse the `McpClarificationEnvelope` question/option shape; the
console contract (§5.3) is a thin projection of it.

### 3.5 Validation is a hard gate

**Never return an LLM graph the validator rejects as if it were ready.** After the provider
returns a proposal:

1. Resolve/repair node ids, ports, layout (server owns layout defaults).
2. Run `WorkflowPackageGraphValidator.Validate(graph, registrySnapshot.Nodes, hash)`.
3. If invalid: either (a) one bounded **repair re-prompt** feeding the validator failures
   back to the model, then re-validate; or (b) return `status="needs-clarification"`/`"error"`
   with the failures as `validation.failures`. Do **not** silently drop nodes.
4. Attach the validator result to the response (`validation`) regardless — the console shows
   warnings non-blockingly and blocks publish on errors (existing behavior).

---

## 4. Config & secrets

New options class `WorkflowGenerationConfiguration` (section `WorkflowGeneration`), modeled on
`NlQueryConfiguration` + its `ConfigurationValidator`:

```jsonc
"WorkflowGeneration": {
  "Enabled": false,                 // feature flag; off → endpoint returns "unsupported"
  "DefaultProvider": "local",       // "local" | "anthropic" | "openai" | "deterministic"
  "MaxRepairAttempts": 1,
  "Providers": {
    "local":   { "Endpoint": "http://localhost:11434/v1", "Model": "honua-gis-7b", "ApiKey": "", "TimeoutSeconds": 60, "MaxTokens": 4096 },
    "openai":  { "Endpoint": "https://api.openai.com/v1",  "Model": "gpt-4o",       "ApiKey": "", "TimeoutSeconds": 30, "MaxTokens": 4096 },
    "anthropic": { "Endpoint": "https://api.anthropic.com", "Model": "claude-...",  "ApiKey": "", "TimeoutSeconds": 30, "MaxTokens": 4096 }
  }
}
```

- **API keys are secret references**, resolved via the existing `ISecretProvider` /
  `IConnectionSecretResolver` (the secret-reference connection feature already added). Support
  env fallbacks per provider (e.g. `HONUA_WORKFLOWGEN_OPENAI_API_KEY`,
  `HONUA_WORKFLOWGEN_ANTHROPIC_API_KEY`) exactly like `HONUA_NLQUERY_API_KEY`.
- `ValidateOnStart`: only validate a provider's endpoint/model/key when it is the default or
  explicitly enabled (NlQuery's validator already shows this conditional pattern — don't force
  throwaway URLs for unused providers).
- `local` should **not** require HTTPS (it's localhost); GPT/Anthropic should.

`GET /api/v1/console/workflow-generation/providers` (admin) returns the providers that are
`Enabled && IsConfigured`, with their kind + label + the server default, so the UI selector
only offers usable providers and disables the rest with a reason.

---

## 5. The console wire contract (FROZEN — already built against)

The honua-console shim (`Honua.Console.Contracts/StudioWorkflowShims.cs`) calls these. JSON is
camelCase; enums are PascalCase strings; the envelope is the existing `WorkflowApiResponse<T>`
(`success`/`data`/`message`/`timestamp`). Errors follow the existing status→issue mapping
(400 Validation failed, 403 Missing permission, 404 Unsupported, 409 Conflict).

### 5.1 `GET /api/v1/console/workflow-generation/providers`

```jsonc
{ "success": true, "data": {
  "enabled": true,
  "defaultProvider": "local",
  "providers": [
    { "id": "local",     "label": "Local GIS model", "kind": "local",     "available": true,  "detail": "honua-gis-7b @ localhost" },
    { "id": "anthropic", "label": "Claude",           "kind": "anthropic", "available": true,  "detail": "claude-..." },
    { "id": "openai",    "label": "GPT",              "kind": "openai",    "available": false, "detail": "No API key configured" }
  ]
}}
```

When `Enabled=false` or no provider is configured, return `enabled:false` with an empty/parked
provider list. (The console treats this as the honest "AI generation unavailable" state — it
does **not** fabricate a workflow.)

### 5.2 `POST /api/v1/console/workflow-packages/generate`

Request:

```jsonc
{
  "prompt": "Nightly: pull assessor CSV from SFTP, validate against parcels_2024, geocode missing coords, append new parcels, publish to FeatureServer + OGC API, notify #gis-ops on failure.",
  "provider": "local",                 // optional; null → server default
  "model": null,                       // optional per-call model override
  "graph": { /* current WorkflowGraph */ } | null,   // present on a REFINE turn; null = fresh generation
  "conversation": [                    // prior turns for context (optional)
    { "role": "user", "content": "..." }, { "role": "assistant", "content": "..." }
  ],
  "answers": [                         // answers to a prior needs-clarification turn (optional)
    { "questionId": "target-fs", "optionId": "public-works-fs" }
  ]
}
```

Response (`data` is `WorkflowGenerationResult`):

```jsonc
{
  "status": "generated",   // "generated" | "needs-clarification" | "unsupported" | "refused" | "error"
  "graph": { /* WorkflowGraph: nodes, edges, schedule, workerProfile, editorMetadata — same shape PUT /workflow-packages/{id} accepts */ } | null,
  "rationale": "Proposed a 7-step nightly pipeline: SFTP pull → validate → geocode → append → publish (FS + OGC) with a failure notification.",
  "clarifications": [      // present iff status == needs-clarification (see 5.3)
    { "id": "target-fs", "kind": "source", "prompt": "Which FeatureServer should Publish target?",
      "reason": "Two candidates matched 'public works'.",
      "choices": [ { "id": "public-works-fs", "label": "public-works-fs", "effect": "Publish layer 0" },
                   { "id": "pw-staging-fs",   "label": "pw-staging-fs",   "effect": "Publish to staging" } ] }
  ],
  "validation": { "isValid": true, "failures": [], "warnings": [ { "code": "...", "message": "..." } ] },
  "unmappedRequests": [ "send a carrier pigeon" ],   // asked-for steps with no matching node type (grounding gaps)
  "capabilityState": { "name": "geocode", "state": "degraded", "reason": "Geocoder quota limited" } | null,
  "provider": "local", "model": "honua-gis-7b", "registryVersion": "sha256:...",
  "usage": { "promptTokens": 1234, "completionTokens": 567, "latencyMs": 4200 } | null
}
```

Status semantics (map directly from the plan-analysis statuses):

| `status` | plan-analysis analogue | console behavior |
| --- | --- | --- |
| `generated` | `planned` | apply `graph` to the draft; show `rationale` as a Honua turn; show `validation.warnings` + `unmappedRequests` |
| `needs-clarification` | `clarification_required` | render `clarifications` as cards; user answers → re-`POST` with `answers` |
| `unsupported` | `unsupported` | Honua turn explaining the unsupported capability; no graph change |
| `refused` | `rejected` | Honua turn with the refusal reason (e.g. prompt unrelated to GIS workflows) |
| `error` | — | transport/provider error; surfaced as an issue, retryable |

`graph` MUST be the **same `WorkflowGraph` shape** the existing
`PUT /api/v1/console/workflow-packages/{packageId}` accepts (nodes/edges/schedule/workerProfile/
`editorMetadata`), so the console maps it through its existing graph→draft projection unchanged.
The server owns sensible **layout** (`console.column`/`console.row` in node metadata) so the DAG
preview renders without the client inventing positions.

### 5.3 Clarification shape

A flattened projection of `McpClarificationEnvelope`:

```
clarification.id      ← questionId
clarification.kind    ← kind  ("source"|"unit"|"field"|"schedule"|"target"|...)
clarification.prompt  ← prompt
clarification.reason  ← (reasonCodes joined / human reason)
clarification.choices ← options[]  { id ← option.id, label ← option.label, effect? ← optional human effect }
```

The console posts back `answers: [{ questionId, optionId }]`. Multiple open questions may be
returned in one turn and answered together.

---

## 6. Async vs sync

Start **synchronous** — the endpoint blocks for the provider call (bounded by per-provider
timeout, default 30–60s) and returns the result. This matches the NlQuery provider and keeps
the UI simple. If large prompts or slow local models push latency past ~60s, promote generation
to a **job** (`Honua.Jobs`) and have the endpoint return a `jobId` the console polls — but only
if measured latency demands it; do not pre-optimize. (Streaming token-by-token is a later nicety;
the console contract returns a complete result.)

---

## 7. Safety, determinism, eval

- **Validation gate** (§3.5) is the core safety property: the client can only ever receive a
  graph the server's own validator accepts (or an explicit non-ready status).
- **Node whitelist** via structured-output enum prevents invented node types.
- **Prompt cap** (≤8 000 chars, like NlQuery) bounds cost/blast radius.
- **Grounding + auth**: a generated step that targets a source the caller can't access must
  surface `capabilityState: "auth_denied"` (never leak private source existence).
- **Deterministic fixtures**: ship a `DeterministicWorkflowGenerationProvider` + fixture cases
  (mirror `tests/fixtures/ai-builder/` and the NlQuery deterministic provider) so CI/eval runs
  the full grounding→generate→validate path with **no live model**. Suggested cases:
  `nightly-etl-publish` (the canonical 7-step happy path), `ambiguous-target-featureserver`
  (clarification), `unsupported-step` (capabilityState=unsupported + unmappedRequests),
  `auth-denied-source`, `refine-add-notify` (graph-in → graph-out). These double as the
  eval set for judging a tuned local model.

---

## 8. Build order (suggested)

1. `WorkflowGenerationConfiguration` + validator + DI + secret/env key resolution.
2. `IWorkflowGenerationProvider` + `DeterministicWorkflowGenerationProvider` (fixtures) + the
   `generate` endpoint + `WorkflowPackageGraphValidator` gate. **This alone turns the console
   UI green** end-to-end with deterministic output and no live model.
3. `OpenAiCompatibleWorkflowGenerationProvider` (local + GPT) with node-registry-grounded
   structured output; wire `GET .../workflow-generation/providers`.
4. `AnthropicWorkflowGenerationProvider` (Claude).
5. Grounding integration (real layer/connection resolution + clarifications).
6. Optional: `WorkflowPackageFamilyReviewAdapter` for the MCP `honua_*_package` surface; async
   job promotion if latency requires.

Steps 1–2 are the unblock; 3–6 raise fidelity.

---

## 9. Endpoints to register (admin, `RequireAdminAuthorization()`, api-version 1.0)

| Method | Route | Returns |
| --- | --- | --- |
| `POST` | `/api/v{version}/console/workflow-packages/generate` | `WorkflowApiResponse<WorkflowGenerationResult>` |
| `GET`  | `/api/v{version}/console/workflow-generation/providers` | `WorkflowApiResponse<WorkflowGenerationProviders>` |

Register in `WorkflowPackageEndpoints.cs` (alongside the existing workflow-package routes), add
to `EndpointRegistry.cs` (coverage), and add the new DTOs to the workflow JSON source-gen
context. Telemetry: reuse the `honua.nlquery.*` activity/log shape under `honua.workflowgen.*`.

---

## 10. Cross-repo

- **honua-console** — UI is implemented against §5 (page `StudioWorkflowAiPage`, client
  `IStudioWorkflowPackageClient.GenerateAsync` + `GetGenerationCapabilityAsync`, shim
  `IWorkflowPackageApiClient.GenerateWorkflowAsync` + `ListGenerationProvidersAsync`). Until this
  endpoint ships, the page shows the missing-binding "AI generation unavailable" state. No
  console change is required when the endpoint lands — it will simply bind.
- **honua-server** — this document.

Keep the §5 wire contract stable; if it must change, change it here and ping the console side.
