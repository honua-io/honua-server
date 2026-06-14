# Package Review API

The package review API gives consoles, SDKs, MCP clients, CI jobs, and generated
apps one deterministic response shape for validating a package before publish or
execution. It is a review step only: preview planning must not create jobs,
publish catalog entries, write artifacts, or mutate published state.

## HTTP Endpoints

Both endpoints require admin authorization. Successful HTTP responses are wrapped
as `ApiResponse<PackageReviewResponse>` with the review payload in `data`.

| Endpoint | Behavior |
|----------|----------|
| `POST /api/v1/admin/packages/validate` | Validates the package and returns findings, status, action gates, estimate, links, and resource references. |
| `POST /api/v1/admin/packages/preview` | Runs the same validation with `includePreviewPlan` forced to `true` and returns a read-only preview plan when the package can be planned. |

The MCP operator surface exposes the same response contract through:

| Tool | Behavior |
|------|----------|
| `honua_validate_package` | Validates a package and returns `PackageReviewResponse`. |
| `honua_preview_package` | Validates a package with read-only preview planning enabled and returns `PackageReviewResponse`. |

MCP tool calls require an authenticated operator principal with process read
authorization. Successful MCP responses return the same `PackageReviewResponse`
in `result.structuredContent` and as serialized JSON text in `result.content`.

## Request

```json
{
  "contractVersion": "honua.package_review.v1",
  "packageFamily": "app_package",
  "packageId": "traffic-delay-v1",
  "requestedAction": "publish",
  "format": "honua-app-package",
  "packagePayload": {},
  "includePreviewPlan": false,
  "includePassFindings": false,
  "estimate": {
    "rowCount": 1500000,
    "durationMs": 45000,
    "costWeight": 1500,
    "confidence": "high"
  },
  "requirements": {
    "dataBindings": [],
    "permissions": [],
    "schemas": [],
    "fields": [],
    "domains": [],
    "crs": [],
    "geometry": [],
    "temporal": [],
    "capabilities": [],
    "dependencies": [],
    "formats": [],
    "approvals": []
  },
  "resourceRefs": {}
}
```

Supported `packageFamily` values are `query`, `analysis_plan`, `map_package`,
`dashboard_report`, `form`, `app_package`, `workflow`, `gp`, and `etl`.
`packageFamily` is intentionally not marked required and not enum-limited in
the published HTTP (OpenAPI) and MCP schemas. A missing value returns a
`missing_package_family` blocker and an unknown value returns an
`unsupported_package_family` blocker (see [Findings](#findings)); reviewing
these inputs instead of rejecting them at the schema keeps schema-driven
clients from blocking the very payloads the review is meant to inspect. The
published HTTP (OpenAPI) and MCP schemas also allow `requirements` and
`resourceRefs` to be `null`; an explicit `null` is normalized to an empty set
so the review still returns deterministic findings.

The server normalizes `/validate` requests to `includePreviewPlan: false` and
`/preview` requests to `includePreviewPlan: true`, so clients can use separate
buttons without trusting caller-supplied flags.

`analysis_plan` and `gp` packages may include an analysis-plan payload in
`packagePayload`. That payload is parsed into the canonical geoprocessing
`AnalysisPlan` shape and validated through the same plan validator used by
`honua_validate_plan`, dry-run, execute, gRPC, and GPServer paths, with the
caller's tenant, subject, scopes, and roles propagated into the validator so
authorization-sensitive plan checks match what execution would see. A missing
payload returns a `missing_analysis_plan_payload` blocker, and an unparseable
or structurally invalid payload returns an `invalid_analysis_plan_payload`
blocker (with the validator message in evidence), so malformed analysis plans
surface as deterministic findings rather than request failures.

## Response Contract

`PackageReviewResponse` is the canonical shape across HTTP and MCP. JSON uses
camelCase and omits null properties.

```json
{
  "contractVersion": "honua.package_review.v1",
  "reviewId": "prv_...",
  "packageFamily": "app_package",
  "packageId": "traffic-delay-v1",
  "status": "blocked",
  "canExecute": false,
  "canPublish": false,
  "requiresApproval": true,
  "checkedAt": "2026-05-24T10:00:00Z",
  "findings": [
    {
      "code": "approval_required",
      "severity": "blocker",
      "category": "approval",
      "disposition": "requires_approval",
      "appliesTo": "publish",
      "message": "Package requires approval before the requested action can continue.",
      "requiredAction": {
        "actionKind": "obtain_approval",
        "targetPath": "$.requirements.approvals[0]",
        "title": "Obtain the required approval."
      },
      "affectedArtifact": {
        "kind": "approval",
        "path": "$.requirements.approvals[0]"
      },
      "evidence": [
        {
          "kind": "approval",
          "expected": "approved",
          "actual": "not_approved",
          "policyRef": "publish-review"
        }
      ]
    }
  ],
  "links": {},
  "resourceRefs": {}
}
```

Status values:

| Status | Meaning |
|--------|---------|
| `ready` | No unresolved blocker or warning findings were emitted. |
| `warning` | Non-blocking warning findings were emitted. |
| `blocked` | At least one unresolved `blocker` finding was emitted. |

Action gates:

| Field | Meaning |
|-------|---------|
| `canExecute` | `false` when an unresolved `blocker` applies to `execute` or `both`. |
| `canPublish` | `false` when an unresolved `blocker` applies to `publish` or `both`. |
| `requiresApproval` | `true` when an unresolved approval finding remains. |

Console and generated-app clients should disable execute or publish controls
from `canExecute` and `canPublish`, not by reinterpreting finding codes.
The supported action scopes are `both`, `execute`, `publish`, and `review`.
`review`-scoped blockers make the overall `status` `blocked`, but they do not
set `canExecute` or `canPublish` to `false` unless another blocker applies to
those action gates.

## Review Identifier

`reviewId` is a deterministic `prv_`-prefixed digest of the reviewed inputs and
the caller's authorization context: the contract version, package family,
package id, requested action, format, package payload, requirements, estimate,
and resource refs, plus the caller-visible tenant, actor, subject, scopes, and
roles. The same principal reviewing identical content receives a stable id, so
clients may cache or deduplicate by `reviewId`; two principals whose tenant,
subject, scope, or role context differs receive different ids for the same
package, because authorization-sensitive findings can differ between them. The
HTTP and MCP adapters populate that caller context from the authenticated
principal before invoking the shared review service.

## Findings

Findings are ordered by severity (`blocker`, `warning`, `info`, `pass`), then by
category and code. Each finding carries a stable code, severity, category,
disposition, affected action scope, optional required action, optional affected
artifact, and evidence entries that are safe to show to clients. Pass findings
are emitted only when `includePassFindings` is `true`.

Disposition values are `unresolved`, `resolved`, `auth_denied`, `unsupported`,
and `requires_approval`.

Shared requirement validation covers:

| Requirement group | Example finding categories |
|-------------------|----------------------------|
| `dataBindings` | Missing or unresolved input data bindings. |
| `permissions` | Permission and RBAC denials. |
| `schemas`, `fields`, `domains` | Missing schemas, version mismatches, missing or incompatible fields, and coded-value domain mismatches. |
| `crs` | SRID mismatches, unit mismatches, and explicit CRS or datum assumptions. |
| `geometry` | Geometry type mismatches and disallowed null geometries. |
| `temporal` | Missing required temporal field or metadata. |
| `capabilities` | Unsupported service, package, or operation capabilities. |
| `dependencies` | Missing required package dependencies. |
| `formats` | Unsupported package or artifact formats. |
| `approvals` | Required approval that has not yet been granted. |

The service also emits request-shape blockers such as
`missing_package_family`, `unsupported_package_family`, and
`unsupported_contract_version`. These default to the `both` action scope, so
they set `status` to `blocked` and clear both `canExecute` and `canPublish`.
If a supplied estimate is expensive
(`rowCount` or `featureCount` at least 1,000,000, `durationMs` at least 30,000,
`durationSeconds` at least 30, or `costWeight` at least 1,000), the response
includes an `expensive_preview_estimate` warning and echoes the estimate.

## Read-Only Preview Planning

Preview planning is informational. A preview response may include:

```json
{
  "planId": "preview:app_package:traffic-delay-v1",
  "mayMutatePublishedState": false,
  "operations": [
    {
      "operationId": "review",
      "operationKind": "app_package",
      "inputRefs": ["service:traffic"],
      "outputArtifactKinds": ["app_package"],
      "dependsOn": [],
      "mayMutatePublishedState": false,
      "requiredCapabilities": ["app.builder"]
    }
  ]
}
```

`mayMutatePublishedState` must remain `false` on the plan and on every
operation. Clients should treat any future value of `true` as non-previewable
and refuse to publish or execute from that response.

Generic package families receive a single read-only `review` operation when
preview is requested. For `analysis_plan` and `gp`, preview planning is returned
only when the canonical geoprocessing validator reports the plan is executable;
the preview operations mirror the submitted plan steps and include each step's
input refs, dependencies, and required process capability.
