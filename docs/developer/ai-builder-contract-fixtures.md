# AI Builder Contract Fixtures

`tests/fixtures/ai-builder/spatial-query-contract-v1.json` and
`tests/fixtures/ai-builder/operations-dashboard-contract-v1.json` are the
deterministic server-side contract fixtures for the AI app builder demos. They
do not call a live model and do not authorize raw SQL generation. The fixtures
lock the structured response shapes that SDK, Portal, and MCP clients can replay
while the broader app-builder runtime continues to land behind the existing NL
query, spec, plan/apply, package, and MCP surfaces.

The spatial-query fixture covers:

- NL prompt to structured `filterPlan`, `specDraft`, and `appDraft` state.
- Metadata discovery for source selection, schema preview, field labels/domains,
  geometry columns, CRS, and spatial predicate capability states.
- Clarification candidates for ambiguous sources, units, and operations.
- Plan DAGs, warnings, cache keys, reproducibility warnings, job status, and
  artifact references.
- Packageable `MapPackage` and `AppPackage` artifact references using the MCP
  resource URI patterns.
- Starter templates for `map-only`, `map-plus-table`, `map-plus-chart`,
  `filtered-map`, and `linked-dashboard`.
- Required fixture outcomes: success, ambiguity, unsupported capability,
  auth/RBAC denied, oversized estimate, cache hit, and apply failure.

The operations-dashboard fixture is the GTM proof for this prompt:

> Build an operations dashboard for this saved map showing a map, incident list,
> incident count, incidents by type chart, and district filter.

It records `modelInvocation.mode = "disabled"` so SDK-JS and Portal demos can
prove the flow without hidden model state. Its success scenario returns:

- `draft.structuredDraft`: saved-map binding, source IDs, and the five expected
  dashboard widgets (`map`, `incident-list`, `incident-count`,
  `incidents-by-type`, and `district-filter`).
- `draft.specDraft.canonicalSpecDocument`: a reviewable app-capable canonical
  spec draft with service, report, compute, and `App` nodes. Portal should show
  this before apply instead of asking clients to infer intent from prose.
- `plan`: deterministic DAG, mutable-source/cache warnings, and the
  snapshot-token cache key that SDK-JS can replay.
- `apply.job.progress`: structured stage progress through plan validation, map
  package composition, app package composition, and app manifest emission.
- `apply.artifacts`: `MapPackage`, `AppPackage`, app manifest, and app bundle
  artifact references. The app manifest uses
  `application/vnd.honua.app-manifest+json` and
  `honua_app_manifest.v1`.
- `apply.packages.appPackage`: the SDK-JS package view, including
  `manifestArtifactId`, `manifestArtifact`, `manifestPreview`, generated files,
  asset manifest, `mapPackageId`, runtime config schema, delivery hints, and
  bound artifacts.

SDK-JS should use `manifestArtifact.resourceUri` (or the cached
`manifestPreview` in fixture-only tests) as the proof runtime input. Portal
should bind review panels from `structuredDraft`, `specDraft`, `plan.warnings`,
`apply.job.progress`, and the `packages` envelope instead of parsing artifact
labels.

The operations-dashboard edge scenarios cover:

- source, field, geometry, CRS, predicate, and aggregation clarification.
- unsupported capability (`kernelDensityAggregation`).
- RBAC denial against a restricted incident source.
- oversized estimate rejection.
- cache hit with mutable-source warning.
- apply failure while writing the app manifest artifact.

Its `capabilityDiscovery.mcpInspection.resources` groups the MCP inspection
surfaces the builder needs: services, schemas, processes, packages, artifacts,
jobs, and deployments. Schema entries reference the fixture's
`schemaPreviews`; package/job/deployment entries use the existing MCP URI
families documented in `MCP_SERVER.md`.

The narrow regression suite is
`Honua.Core.Tests.Features.AiBuilder.AiBuilderContractFixtureTests`. It verifies
that the fixtures remain complete enough for honua-sdk-js app-builder smoke
tests and Portal review flows to render reviewable drafts, package manifests,
and recovery states without hidden model state.

MCP clients should pair fixture artifacts with these existing inspection routes:

- `honua_ground_candidates` and `honua_clarify_intent` for grounding and
  clarification envelopes.
- `honua_validate_plan`, `honua_dry_run_plan`, and `honua_execute_plan` for
  plan validation, dry-run estimates, and job submission.
- `honua://jobs/{jobId}`, `honua://jobs/{jobId}/results`,
  `honua://map-packages/{packageId}`, and
  `honua://app-packages/{packageId}` for inspection.

This slice is intentionally fixture-driven. Live model-backed planning,
cloud-deployed SDK compatibility runs, and broader runtime endpoint coverage
remain follow-up work for the parent platform ticket.
