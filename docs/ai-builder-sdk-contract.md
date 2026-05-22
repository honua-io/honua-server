# AI Builder SDK Contract

This document maps the honua-server surfaces used by the SDK AI Spatial App
Builder sample. The deterministic fixtures live in
`tests/fixtures/ai-builder/` and use `modelInvocation.mode = "disabled"` so CI
and SDK eval harnesses can replay the flow without live model calls.

## Workflow

Text sequence:

`prompt` -> `honua_ground_candidates` -> `honua_clarify_intent` ->
`honua_plan_analysis` -> SDK review -> `honua_validate_plan` ->
`honua_dry_run_plan` -> `honua_execute_plan` ->
poll `honua://jobs/{jobId}` -> read `honua://jobs/{jobId}/results` ->
consume `honua://app-packages/{appPackageId}`.

| Step | Server surface | SDK use |
| --- | --- | --- |
| Prompt grounding | MCP tool `honua_ground_candidates` | Resolve candidate services, layers, fields, CRS, predicates, and operations. |
| Clarification | MCP tool `honua_clarify_intent` | Render selectable candidates for ambiguous sources, fields, units, CRS, filters, and operations. |
| Draft | MCP tool `honua_plan_analysis` | Render `specDraft`, `appPackage` preview, `warnings`, `capabilityState`, `cache`, and plan DAG without hidden model state. |
| Review validation | MCP tool `honua_validate_plan` | Re-run deterministic plan/spec validation before showing apply controls. |
| Estimate | MCP tool `honua_dry_run_plan` | Show cost, artifact estimates, warnings, and non-executable states. |
| Apply | MCP tool `honua_execute_plan` | Submit the reviewed plan and receive `jobId` plus `honua://jobs/{jobId}`. |
| Job progress | MCP resource `honua://jobs/{jobId}` | Poll status, percent complete, phase, warnings, and result URI. |
| Job outputs | MCP resource `honua://jobs/{jobId}/results` | Read artifact refs, workspace refs, `mapPackageId`, `appPackageId`, provenance, and errors. |
| Generated app | MCP resource `honua://app-packages/{appPackageId}` | Load package metadata and bound manifest/artifact references for the generated mini-app smoke. |

The HTTP endpoint for these MCP calls is `POST /mcp` using JSON-RPC 2.0.

## Resource Reference

| URI template | Category | Status | Required authorization |
| --- | --- | --- | --- |
| `honua://catalog/processes` | processes | Functional. Returns built-in catalog entries with `processId`, `name`, `family`, `description`, and `parameters`. | `Catalog` / `Discover` |
| `honua://workspaces/{workspaceId}` | schemas/artifacts | Functional when a workspace store and lifecycle service are registered; otherwise returns a stable `status: "degraded"` envelope. | `Workspace` / `Read` |
| `honua://jobs/{jobId}` | jobs | Functional job status resource. | Job read through shared geoprocessing service |
| `honua://jobs/{jobId}/results` | artifacts/jobs | Functional result package resource. | Job result read through shared geoprocessing service |
| `honua://jobs/{jobId}/report` | artifacts | Functional when analysis reporting is registered. | Report read through reporting service |
| `honua://published-services/{serviceId}` | services | Functional when MCP promotion surface stores are registered. | `PublishedService` / `Read` |
| `honua://deployments/{deploymentId}` | deployments | Functional when MCP promotion surface stores are registered. | `Deployment` / `Read` |
| `honua://map-packages/{packageId}` | packages | Functional when MCP promotion surface stores are registered. | `Package` / `Read` |
| `honua://app-packages/{packageId}` | packages | Functional when MCP promotion surface stores are registered. | `Package` / `Read` |
| `honua://promotion-surface` | services/deployments/packages | Functional index when MCP promotion surface stores are registered. | Store-backed read |

## Fixture Cases

Use `context.fixtureCase` with `honua_plan_analysis` to force deterministic
scenario replay.

| `fixtureCase` | Expected SDK rendering |
| --- | --- |
| `success-linked-dashboard` | Render a reviewable spec/app draft, plan DAG, warnings, apply progress, and map/app package artifacts. |
| `ambiguity-source-and-unit` | Render clarification choices for source selection and units before apply. |
| `unsupported-spatial-join` | Render unsupported capability state and disable apply for that operation. |
| `auth-denied-private-source` | Render RBAC-denied source state and require a different source or credentials. |
| `oversized-estimate` | Render estimate limits and prevent execution until scope is reduced. |
| `cache-hit-reused-packages` | Render cache hit metadata and reused package references while preserving mutable-source warnings. |
| `apply-failure-package-step` | Render failed apply status, failed step, errors, and any partial artifact references. |

The fixture contract files also include capability discovery, source/schema
metadata, starter templates (`map-only`, `map-plus-table`, `map-plus-chart`,
`filtered-map`, `linked-dashboard`), and MCP inspection URI examples.

## Capability States

| State | SDK behavior |
| --- | --- |
| `supported` | The capability can be selected and validated through the canonical plan/filter pipeline. |
| `degraded` | The capability is available with warnings or partial backend support; show the warning and keep review explicit. |
| `unsupported` | The server cannot execute the requested capability; disable apply and show alternatives when provided. |
| `auth_denied` | The capability or source exists but the caller lacks permission; show the authorization state without leaking private data. |
| `oversized` | The plan estimate exceeds configured limits; show the limit and require scope reduction before execution. |

Server fixtures and MCP tools never expose raw SQL as planner output. Natural
language output is replayed as structured draft/spec/filter state and is
validated through the existing plan, filter, and geoprocessing infrastructure
before apply.
