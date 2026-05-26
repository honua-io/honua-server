# Metadata v2 Release Readiness

This checklist is derived from the Metadata v2 epic,
[honua-server#1035](https://github.com/honua-io/honua-server/issues/1035), and
its child issues. It is not authoritative. Use the GitHub issues for acceptance
criteria, status, and closure decisions.

Use this document as a release review aid after implementation work lands. It
should help reviewers confirm that the issue-level gates have coherent evidence
across schema, runtime, UI, migration, and standards projection.

## Gate 1: Canonical Schema Exists

Derived from:

- [#1035](https://github.com/honua-io/honua-server/issues/1035)
- [#1036](https://github.com/honua-io/honua-server/issues/1036)
- [#1037](https://github.com/honua-io/honua-server/issues/1037)

Release evidence:

- There is one canonical Metadata v2 schema source in the repo.
- Runtime snapshots validate against that schema without patching.
- Root metadata includes schema version, revision, environment, and
  generated time.
- Data Resources are the canonical unit; service-specific identity lives on
  publications or target-specific overrides.

## Gate 2: Secret-Safe Metadata

Derived from:

- [#1038](https://github.com/honua-io/honua-server/issues/1038)
- [#1039](https://github.com/honua-io/honua-server/issues/1039)
- [#1045](https://github.com/honua-io/honua-server/issues/1045)

Release evidence:

- Connections use references for endpoints, credentials, or connection handles.
- Production validation rejects dev-only inline credentials.
- Admin APIs and Redis snapshots do not expose resolved connection strings or
  secrets.
- Health checks resolve secrets at runtime without mutating canonical metadata.

## Gate 3: Resource Meaning Projects Without Duplication

Derived from:

- [#1040](https://github.com/honua-io/honua-server/issues/1040)
- [#1041](https://github.com/honua-io/honua-server/issues/1041)
- [#1042](https://github.com/honua-io/honua-server/issues/1042)
- [#1043](https://github.com/honua-io/honua-server/issues/1043)

Release evidence:

- Canonical resource metadata can project to OGC Records, DCAT, STAC, ISO
  19115, Esri catalog/items, GeoServices REST, OGC API, and OData.
- Standards-specific data is sparse override data, not a competing source of
  truth.
- Field roles drive target mappings before advanced target-specific bindings.
- One resource can have multiple publications with target readiness reporting.

## Gate 4: Workflow-Based Admin UI

Derived from:

- [#1035](https://github.com/honua-io/honua-server/issues/1035)
- [#1044](https://github.com/honua-io/honua-server/issues/1044)
- [#1046](https://github.com/honua-io/honua-server/issues/1046)

Release evidence:

- The admin UI can navigate Metadata v2 through workflow screens without
  requiring raw schema editing.
- Primary screens use Connections, Data Resources, Source, Fields, Metadata,
  Publish, Access, Validation, Readiness, Projection Preview, and Advanced
  Overrides.
- Access presets summarize policy behavior in human-readable language.
- Validation Center groups source, schema, metadata, publishing, security,
  standards, and cache/runtime findings.

## Gate 5: Runtime Snapshot and Projection Cache Safety

Derived from:

- [#1043](https://github.com/honua-io/honua-server/issues/1043)
- [#1045](https://github.com/honua-io/honua-server/issues/1045)

Release evidence:

- Cache keys include environment, catalog, schema version, revision,
  projection target, and projection profile version where applicable.
- Runtime snapshots exclude secrets and runtime-only handles.
- Projection caches can be rebuilt independently from the canonical snapshot.
- Schema migrations invalidate old cache keys deterministically.

## Gate 6: v1 Migration Is Diagnosable

Derived from:

- [#1047](https://github.com/honua-io/honua-server/issues/1047)

Release evidence:

- Existing metadata snapshots can convert to Metadata v2.
- Migration reports warnings, blockers, and inferred defaults.
- Service-owned layers become resource publications.
- Raw connection strings are flagged and converted to required secret
  references.
- Migration output validates against the Metadata v2 schema.

## Gate 7: Release Notes and User Risk

Derived from:

- [#1035](https://github.com/honua-io/honua-server/issues/1035)
- [Release checklist](../RELEASE_CHECKLIST.md)

Release evidence:

- Any user-visible behavior change is reflected in release notes or migration
  guidance.
- Known caveats and workarounds cover incomplete target projections,
  validation warnings, migration blockers, or admin UI limitations.
- Follow-up issues are linked for deferred Metadata v2 work.

## Console Content and RBAC Baseline (#1162)

Derived from:

- [#1162](https://github.com/honua-io/honua-server/issues/1162)
- [#1163](https://github.com/honua-io/honua-server/issues/1163) (persistent store follow-on)
- [#1164](https://github.com/honua-io/honua-server/issues/1164),
  [#1165](https://github.com/honua-io/honua-server/issues/1165) (release
  lifecycle follow-ons)

Release evidence:

- The Console content item, session bootstrap, action-check, and provenance
  endpoints under `/api/v1/console/**` are documented in
  [Console Content and RBAC (Admin API)](../../admin-api/console-content-and-rbac.md)
  and listed in `EndpointRegistry.All`.
- `ConsoleContentItem.itemType` covers `service`, `layer`, `saved-map`,
  `dashboard`, `report`, `generated-app`, and `open-data`; sidecar shapes per
  type are tracked through `ConsoleJsonContext` source-generated serializers.
- Seven Console verbs (`view`, `edit`, `publish`, `share`, `embed`, `operate`,
  `administer`) map onto the existing policy action set in
  `IConsoleActionEvaluator`; mappings and visibility rules are test-covered by
  `ConsoleActionEvaluatorTests`.
- `IConsoleContentStore` is satisfied at baseline by an in-memory store;
  persistent backing is tracked under #1163 and is not required to gate this
  baseline.

## GitOps Release Prevalidation (#1164)

Derived from:

- [#1163](https://github.com/honua-io/honua-server/issues/1163)
- [#1164](https://github.com/honua-io/honua-server/issues/1164)

Release evidence:

- `/api/v1/admin/metadata/prevalidate` is documented in
  [Metadata Prevalidation Admin API](../../admin-api/metadata-prevalidation.md)
  and listed in `EndpointRegistry.All`.
- The endpoint accepts either a persisted `releasePackageId` or an inline
  `MetadataReleasePackage`, plus a target environment and optional declared
  data-script contracts.
- Reports include deterministic `metadata.compat.*` finding codes, secret-safe
  expected/actual values, affected semantic ids, required actions, coverage
  state, affected dependents, and rollback readiness.
- `canCreatePullRequest` and `canPromote` are false for `blocked` and
  `unknown`; script-covered errors downgrade the overall status to `warning`
  while preserving the automation gates.
- Declared data scripts are never executed by prevalidation. They cover findings
  only when their before-contract matches target state and their after-contract
  satisfies the missing requirement; `exists: true` alone does not cover missing
  resources, services, publications, or storage bindings. Script-level
  `targetEnvironment` narrows coverage to the matching target environment.
- Core analysis and the admin endpoint have tests for ready, blocked, warning,
  unavailable-state, script-covered, exists-only non-coverage,
  before-contract-mismatch, explicit `dataScripts: null` and nested null
  collection rejection, omitted collection normalization, and rollback readiness
  outcomes.

## GitOps Release Operation Lifecycle and Rollback (#1165)

Derived from:

- [#1164](https://github.com/honua-io/honua-server/issues/1164)
- [#1165](https://github.com/honua-io/honua-server/issues/1165)

Release evidence:

- `/api/v1/admin/metadata/releases/{packageId}/operation` is documented in
  [Metadata Prevalidation Admin API](../../admin-api/metadata-prevalidation.md)
  and [Server Management API](../../operator/CONTROL_PLANE_API.md), and is listed
  in `EndpointRegistry.All`.
- The endpoint returns the deploy-control `DeployOperationResponse` directly
  (no `ApiResponse<T>` envelope) with `kind: "MetadataRelease"` and the release
  lifecycle context in `metadataRelease`. The same shape is served by
  `/api/v1/admin/deploy/operations/{operationId}`, so Console can read a release
  by package ID or by stable operation ID.
- Package-ID lookup returns the most recent operation for a release package;
  retried attempts remain addressable by their stable operation ID for the
  configured retention window. Records and their package-ID index entries use
  `ControlPlane:MetadataRelease:OperationRetention`, which defaults to 30 days.
- `metadataRelease` exposes Git refs (`gitOperationId`, `prUrl`, `commitSha`,
  `desiredRevision`), `targetEnvironment`, linked `deployOperationId` and
  `jobIds`, `evidenceRefs`, the fine-grained `currentStage`, `blockers`,
  `warnings`, and the precomputed `rollbackPlan`. The rollback plan is available
  before execution and retained after failure when known.
- Metadata-only rollback (`class: "MetadataOnly"`) reports
  `isDataAffecting: false` and uses the non-destructive path. All other rollback
  classes are data-affecting, report `requiresExplicitApproval: true`, and route
  through the existing operator destructive-approval gate. Rollback requests
  reuse `POST /api/v1/admin/deploy/operations/{operationId}/rollback`; on accept
  the workflow status and `metadataRelease.currentStage` move to
  `RollbackRequested`, and a rejected destructive check returns `403` without
  mutating the stored operation.
- The admin endpoint has tests for package-ID timeline state with rollback plan,
  operation-ID failure state with rollback plan, the metadata-only
  non-destructive path, data-affecting approval gating, and unknown-package
  `404`. Redis store integration tests cover the package-ID index returning the
  latest operation while a prior attempt stays addressable by operation ID, and
  the stale-index miss returning null.

## Review Output

For a Metadata v2 release candidate, capture:

- GitHub issue list reviewed.
- Schema and migration validation evidence.
- Projection readiness evidence for each claimed target.
- Admin UI workflow evidence or design sign-off.
- Known caveats, waivers, and follow-up issues.
