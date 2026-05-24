# Package Review API

The package review API gives consoles, SDKs, MCP clients, CI jobs, and generated
apps one deterministic response shape for validating a package before publish or
execution. It is a review step only: preview planning must not create jobs,
publish catalog entries, write artifacts, or mutate published state.

## HTTP Endpoints

Both endpoints require admin authorization and return
`ApiResponse<PackageReviewResponse>`.

| Endpoint | Behavior |
|----------|----------|
| `POST /api/v1/admin/packages/validate` | Validates the package and returns findings, status, action gates, estimate, links, and resource references. |
| `POST /api/v1/admin/packages/preview` | Runs the same validation with `includePreviewPlan` forced to `true` and returns a read-only preview plan when the package can be planned. |

The MCP operator surface exposes the same response contract through:

| Tool | Behavior |
|------|----------|
| `honua_validate_package` | Validates a package and returns `PackageReviewResponse`. |
| `honua_preview_package` | Validates a package with read-only preview planning enabled and returns `PackageReviewResponse`. |

## Request

```json
{
  "contractVersion": "honua.package_review.v1",
  "packageFamily": "analysis_plan",
  "packageId": "traffic-delay-v1",
  "requestedAction": "publish",
  "format": "mcp-plan",
  "packagePayload": {},
  "includePreviewPlan": false,
  "includePassFindings": false,
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

The server normalizes `/validate` requests to `includePreviewPlan: false` and
`/preview` requests to `includePreviewPlan: true`, so clients can use separate
buttons without trusting caller-supplied flags.

## Response Contract

`PackageReviewResponse` is the canonical shape across HTTP and MCP:

```json
{
  "contractVersion": "honua.package_review.v1",
  "reviewId": "pkgrev_...",
  "packageFamily": "analysis_plan",
  "packageId": "traffic-delay-v1",
  "status": "blocked",
  "canExecute": false,
  "canPublish": false,
  "requiresApproval": true,
  "checkedAt": "2026-05-24T10:00:00Z",
  "findings": [
    {
      "code": "APPROVAL_REQUIRED",
      "severity": "blocker",
      "category": "approval",
      "disposition": "requires_approval",
      "appliesTo": "publish",
      "message": "Package requires approval before continuation.",
      "requiredAction": {
        "actionKind": "request_approval",
        "targetPath": "$.requirements.approvals[0]",
        "title": "Request approval"
      },
      "affectedArtifact": {
        "kind": "approval",
        "path": "$.requirements.approvals[0]"
      },
      "evidence": [
        {
          "kind": "policy",
          "policyRef": "publish-review"
        }
      ]
    }
  ],
  "previewPlan": null,
  "estimate": null,
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
| `requiresApproval` | `true` when any finding has disposition `requires_approval`. |

Console and generated-app clients should disable execute or publish controls
from `canExecute` and `canPublish`, not by reinterpreting finding codes.

## Findings

Findings are ordered with unresolved blockers first, then warnings, info, and
pass findings. Each finding carries a stable code, severity, category,
disposition, affected action scope, optional required action, optional affected
artifact, and evidence entries that are safe to show to clients.

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

## Read-Only Preview Planning

Preview planning is informational. A preview response may include:

```json
{
  "planId": "preview_...",
  "mayMutatePublishedState": false,
  "operations": [
    {
      "operationId": "op_1",
      "operationKind": "validate_inputs",
      "inputRefs": ["service:traffic"],
      "outputArtifactKinds": [],
      "dependsOn": [],
      "mayMutatePublishedState": false,
      "requiredCapabilities": ["query"]
    }
  ]
}
```

`mayMutatePublishedState` must remain `false` on the plan and on every
operation. Clients should treat any future value of `true` as non-previewable
and refuse to publish or execute from that response.

