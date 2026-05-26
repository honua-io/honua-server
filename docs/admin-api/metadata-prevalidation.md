# Metadata Prevalidation Admin API

`POST /api/v1/admin/metadata/prevalidate` generates a server-owned compatibility report for a Metadata v2 release package against a named target environment. Console can call it before opening a Git PR, and CI can call the same endpoint after a release package is committed.

The endpoint is admin-authorized and does not execute data scripts. It compares the proposed package state with the target environment's current Metadata v2 graph snapshot, using the package source graph revision as the desired-state side of the comparison.

Related Metadata v2 release endpoints:

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/admin/metadata/environments/{environment}/inventory` | Returns revision-stamped semantic inventory for a target environment. |
| `POST /api/v1/admin/metadata/environment-bindings/query` | Returns secret-safe binding summaries for semantic ids across environments. |
| `POST /api/v1/admin/metadata/release-packages` | Creates a persisted `MetadataReleasePackage` from source and target environments. |
| `GET /api/v1/admin/metadata/release-packages/{packageId}` | Reads a persisted release package. |
| `GET /api/v1/admin/metadata/release-packages/{packageId}/gitops-manifest` | Exports a GitOps-safe JSON manifest for the package. |
| `GET /api/v1/admin/metadata/releases/{packageId}/operation` | Reads the most recent metadata release operation for a package ID, including Git refs, linked deploy/jobs, evidence, lifecycle stage, and rollback plan. |

Package-ID operation lookup is a current-attempt index. If a package is retried
under the same ID, the lookup returns the most recent operation; earlier attempts
remain readable by their stable operation ID through
`GET /api/v1/admin/deploy/operations/{operationId}` for the configured metadata
release retention window.

## Metadata Release Operation Contract

`GET /api/v1/admin/metadata/releases/{packageId}/operation` returns the same
`DeployOperationResponse` shape as
`GET /api/v1/admin/deploy/operations/{operationId}`. It is not wrapped in the
`ApiResponse<T>` admin envelope. The response has `kind: "MetadataRelease"` and
embeds release-specific state in `metadataRelease`.

The operation response includes:

| Field | Notes |
|---|---|
| `operationId` | Stable workflow operation ID. Use this for retry history and rollback requests. |
| `kind` | `MetadataRelease` for metadata release lifecycle records. |
| `status` | Workflow status such as `Planned`, `AwaitingApproval`, `Submitted`, `Reconciling`, `Succeeded`, `Failed`, `RollbackRequested`, `RolledBack`, or `ManualInterventionRequired`. |
| `warnings`, `blockingReasons` | Operation-level advisories and blockers. |
| `metadataRelease` | Metadata release lifecycle context described below. |
| `createdAt`, `updatedAt`, `completedAt` | Durable operation timestamps. |

`metadataRelease` fields:

| Field | Notes |
|---|---|
| `packageId` | Metadata release package ID used by the package lookup index. |
| `gitOperationId`, `prUrl`, `commitSha`, `desiredRevision` | Git operation and revision refs captured for the release. |
| `targetEnvironment` | Target environment label for the release. |
| `deployOperationId` | Linked deploy operation ID when service publication is part of the release. |
| `jobIds` | Linked data, backup, smoke, or publication job IDs. |
| `evidenceRefs` | Compatibility, smoke, SLO, or promotion evidence references with `kind`, `refId`, optional `uri`, and `at`. |
| `currentStage` | Fine-grained lifecycle stage for Console timelines. |
| `rollbackPlan` | Precomputed rollback plan, available before execution and retained after failure when known. |
| `warnings`, `blockers` | Release-specific advisories and blockers. |

Lifecycle stages are `Preflight`, `Backup`, `ScriptMigration`, `MetadataApply`,
`ServicePublication`, `Smoke`, `SloWatch`, `Promotion`, `Complete`, `Failed`,
and `RollbackRequested`.

Rollback plan `class` values are `MetadataOnly`, `AliasRepoint`,
`ServiceRevisionRevert`, `ScriptRollback`, `SnapshotRestore`, and
`ManualRecovery`. `MetadataOnly` plans report `isDataAffecting: false` and use
the non-destructive path unless the stored plan also sets
`requiresExplicitApproval: true`; in that case the rollback endpoint evaluates an
explicit operator approval gate without treating the plan as data-affecting. All
other rollback classes are treated as data-affecting; the response reports
`requiresExplicitApproval: true`, the rollback endpoint evaluates the destructive
approval gate, and the plan may list required evidence labels in
`evidenceRequired`.

Rollback is requested through the existing deploy-control endpoint:
`POST /api/v1/admin/deploy/operations/{operationId}/rollback`. The request body
may include `reason`. When accepted for a metadata release operation, the
operation status changes to `RollbackRequested` and
`metadataRelease.currentStage` changes to `RollbackRequested`. If approval is
required and not satisfied, the endpoint returns `403` and leaves the stored
operation unchanged.

The endpoint derives the approval decision from the rollback plan's
`isDataAffecting` and `requiresExplicitApproval` values, then re-reads the stored
operation before writing the state change. If either approval-affecting
classification changed in that window (for example, a recomputed plan moved from
`MetadataOnly` without explicit approval to either an explicit-approval or
data-affecting plan), the endpoint returns `409` and leaves the stored operation
unchanged so the operator can re-read the current plan and re-approve against the
correct gate.

## Request

Provide exactly one package source:

- `releasePackageId`: persisted `MetadataReleasePackage` identifier.
- `releasePackage`: inline `MetadataReleasePackage` payload for pre-PR drafts.

Request fields:

- `targetEnvironment`: target environment name.
- `dataScripts`: optional declared script contracts. Omit for no scripts; when present it must be an array. Explicit `null` is rejected.

Request validation:

- `releasePackageId` must not be an empty GUID.
- Exactly one of `releasePackageId` or `releasePackage` is required.
- `targetEnvironment` is trimmed and must not be blank.
- Up to 100 data scripts may be supplied.
- Each script needs a non-blank `scriptId`. A script-level
  `targetEnvironment` is optional; omitted or blank applies to every target, and
  a populated value is trimmed and matched case-insensitively against the
  requested target environment.
- `beforeContract` and `afterContract` may be omitted when not known. Script
  contract collections may be omitted and are treated as empty arrays; when
  present, they must be arrays and explicit `null` arrays or entries are
  rejected. This includes `declaredOperations`, `resources`, `fields`,
  `requiredIdentifiers`, `domains`, `indexes`, `capabilities`,
  `supportedFormats`, `semanticRoles`, and storage `capabilities`.
- A single script may declare up to 1000 contract fields.

## Response

The response is `ApiResponse<MetadataCompatibilityReport>`.

Malformed JSON or validation failures return `400` as
`application/problem+json` with a safe detail message. Missing persisted
packages, unavailable source revisions, and unavailable target snapshots are not
`404` responses from this endpoint; they return a successful admin envelope with
`status: "unknown"` and a `metadata.compat.state.unavailable` finding so
automation can gate on one report shape.

Top-level report fields:

| Field | Notes |
|---|---|
| `targetEnvironment` | Environment analyzed after normalization. |
| `releasePackageId`, `packageKey` | Package identity when known. |
| `sourceEnvironment`, `sourceRevision`, `sourceEtag` | Desired-state source package graph. |
| `targetRevision`, `targetEtag` | Current target graph snapshot used for comparison. |
| `generatedAt` | Server UTC generation timestamp. |
| `status` | `ready`, `warning`, `blocked`, or `unknown`. |
| `canCreatePullRequest`, `canPromote` | `false` for `blocked` and `unknown`; `true` for `ready` and `warning`. |
| `uncoveredErrorCount`, `coveredErrorCount`, `warningCount`, `scriptCount` | Rollup counters for automation gates. |
| `findings` | Deterministically ordered finding list. |
| `affectedDependents` | Blast-radius inventory for Console visualization. |
| `rollbackReadiness` | Rollback classification and required operator posture. |

Report status values:

- `ready`: no findings block or warn.
- `warning`: no uncovered errors, but warnings or script-covered errors remain.
- `blocked`: at least one error finding is not covered by a declared script.
- `unknown`: source package state, target graph state, or comparable declared metadata is unavailable.

`canCreatePullRequest` and `canPromote` are both status-derived. Script-covered error findings allow those gates to pass, but the report remains `warning` so callers can show the required script step.

## Findings

Each finding includes a stable `code`, `severity`, `kind`, affected semantic id/kind, safe `expected` and `actual` details, `requiredAction`, and data-script coverage state.

Finding kinds are `state`, `resource`, `field`, `identifier`, `spatial`, `temporal`, `storage`, `service`, `publication`, `projection`, `policy`, and `script`. Severities are `info`, `warning`, and `error`.

Coverage states:

- `not-applicable`: no data-script coverage is needed.
- `uncovered`: an error finding still blocks the gate.
- `covered-by-script`: a declared script can cover the finding.
- `unknown`: state is unavailable or the declared metadata is insufficient.

Stable finding codes use the `metadata.compat.*` namespace. Current codes cover unavailable state, missing or mismatched resources, fields, identifiers, spatial metadata, temporal metadata, storage bindings, services, publications, projection semantics, policy references, and script before-contract mismatches.

Data scripts may cover findings only when the `beforeContract` matches the current target state and `afterContract` satisfies the missing requirement. If a script's before-contract does not match the target state, the original finding remains uncovered and the report includes `metadata.compat.script.before_contract_mismatch`.

For missing artifacts, `exists: true` alone is not sufficient coverage. The `afterContract` must also declare the expected discriminator and details:

- Resources: `resourceType`.
- Services: `serviceType`, `route`, and any required `capabilities` matching expected enabled protocols.
- Publications: `publicationType`, `resourceId`, `serviceId`, `path`, `serviceLocalId`, `layerIndex`, and required `supportedFormats` / `capabilities`.
- Storage bindings: `storage.storageBindingId`, `storage.storageType`, and required storage `storage.capabilities`.

## Rollback Readiness

Rollback readiness is classified as:

- `metadata-only`: only Metadata v2 graph/package state needs rollback.
- `service-revision`: service or publication identity changed and rollback should use a service revision.
- `script-reversible`: covered data-impacting changes use scripts declared as reversible.
- `snapshot-required`: at least one covering script is not declared reversible.
- `manual`: state/script applicability is unknown, or uncovered data-impacting errors need operator planning.

## Example

```http
POST /api/v1/admin/metadata/prevalidate
Content-Type: application/json
X-API-Key: <admin-api-key>

{
  "releasePackageId": "33333333-3333-3333-3333-333333333333",
  "targetEnvironment": "staging",
  "dataScripts": [
    {
      "scriptId": "script.add-apn",
      "kind": "sql",
      "reversible": true,
      "declaredOperations": ["add-field"],
      "beforeContract": {
        "resources": [
          {
            "semanticId": "res.parcels",
            "semanticKind": "resource",
            "exists": true,
            "fields": [
              { "semanticId": "field.parcels.apn", "name": "apn", "exists": false }
            ]
          }
        ]
      },
      "afterContract": {
        "resources": [
          {
            "semanticId": "res.parcels",
            "semanticKind": "resource",
            "exists": true,
            "fields": [
              { "semanticId": "field.parcels.apn", "name": "apn", "exists": true, "type": "string", "nullable": false }
            ]
          }
        ]
      }
    }
  ]
}
```

Successful responses use the normal admin envelope:

```json
{
  "success": true,
  "data": {
    "targetEnvironment": "staging",
    "releasePackageId": "33333333-3333-3333-3333-333333333333",
    "status": "warning",
    "canCreatePullRequest": true,
    "canPromote": true,
    "uncoveredErrorCount": 0,
    "coveredErrorCount": 1,
    "warningCount": 0,
    "scriptCount": 1,
    "findings": [
      {
        "code": "metadata.compat.field.missing",
        "severity": "error",
        "kind": "field",
        "affectedSemanticId": "field.parcels.apn",
        "affectedSemanticKind": "field",
        "affectedParentSemanticId": "res.parcels",
        "message": "Required field is missing in the target environment.",
        "expected": { "label": "field", "value": "apn", "details": { "type": "string" } },
        "actual": { "label": "field", "value": "missing" },
        "requiredAction": "run-data-script",
        "coverageState": "covered-by-script",
        "coveringScriptId": "script.add-apn"
      }
    ],
    "affectedDependents": [],
    "rollbackReadiness": {
      "classification": "script-reversible",
      "requiresSnapshot": false,
      "requiresManualAction": false,
      "scriptIds": ["script.add-apn"]
    }
  }
}
```

## Observability

The core service emits the `honua.metadata.compatibility.prevalidate` activity with target environment, package id, source revision, script count, finding count, uncovered/covered error counts, dependent count, status, and target revision tags. Structured logs use event ids `116400` through `116402`.
