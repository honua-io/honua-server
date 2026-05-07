# AI Builder Contract Fixtures

`tests/fixtures/ai-builder/spatial-query-contract-v1.json` is the deterministic
server-side contract fixture for the AI app builder and spatial query demo. It
does not call a live model and does not authorize raw SQL generation. The fixture
locks the structured response shapes that SDK and MCP clients can replay while
the broader app-builder runtime continues to land behind the existing NL query,
spec, plan/apply, package, and MCP surfaces.

The fixture covers:

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

The narrow regression suite is
`Honua.Core.Tests.Features.AiBuilder.AiBuilderContractFixtureTests`. It verifies
that the fixture remains complete enough for honua-sdk-js app-builder smoke tests
to render reviewable drafts and recovery states without hidden model state.

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
