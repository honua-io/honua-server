# Form Package API

Honua Server owns form packages as versioned operational assets. Builders create editable drafts, validate them against target layer policy, publish immutable runtime versions, and reopen a published version by creating a new draft. Field clients submit against published packages only.

The implementation lives in the Forms vertical slice:

- Core contracts and validation: `src/Honua.Core/Features/Forms/Packages/`
- Server routes and runtime behavior: `src/Honua.Server/Features/Forms/`
- Postgres persistence: `src/Honua.Postgres/Features/Forms/`

Forms routes are registered when an `IFormPackageStore` is available. The Postgres provider registers the store and persists package versions, submission idempotency records, and per-attachment policy outcomes.

## Authorization

Admin authoring routes require admin authorization.

Runtime package, offline-policy, and submission routes also use the current admin authorization gate in this slice. Submissions then perform target service/layer data-editor authorization before applying edits, so callers must have both route access and edit permission on the target layer.

## Admin Lifecycle

| Method | Route | Success | Contract |
|---|---|---:|---|
| `GET` | `/api/v1/admin/forms/packages` | `200` | Returns `FormPackageSummary[]`; response is `Cache-Control: no-store`. |
| `POST` | `/api/v1/admin/forms/packages` | `201` | Creates draft version `1` or the next draft for the supplied `formId`; returns `FormPackageVersion` with `ETag` and `Cache-Control: no-store`. Draft saves do not run publish validation. |
| `GET` | `/api/v1/admin/forms/packages/{formId}` | `200` | Returns the current draft, falling back to the current published version; response is `no-store`. |
| `GET` | `/api/v1/admin/forms/packages/{formId}/versions` | `200` | Returns all `FormPackageVersion` rows for a package family, newest version first, or an empty array when the family has no stored versions; response is `no-store`. |
| `GET` | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}` | `200` | Returns one version with `ETag`; response is `no-store`. |
| `PUT` | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}` | `200` | Updates a draft only. Requires the exact returned `ETag` value in `If-Match`; returns updated `FormPackageVersion` with new `ETag`. Draft updates clear stored validation. |
| `POST` | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/validate` | `200` | Returns `FormPackageValidationResult`; stores validation for drafts. |
| `POST` | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/publish` | `200` | Validates and publishes a draft; returns immutable `FormPackageVersion`. |
| `POST` | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/reopen` | `200` | Creates a new draft from a published version; sets `reopenedFromVersion`. |

Expected failures:

- `400` when the package JSON is invalid or publish validation fails. Invalid JSON uses a problem response; publish validation failure returns `FormPackageValidationResult`.
- `404` when a package or version cannot be found.
- `409` when a draft update targets a non-draft, the `ETag` does not match, or a publish target is no longer draft.
- `428` when `PUT` omits `If-Match`.

Create and update draft routes persist the supplied package JSON without running publish validation. Draft updates clear the stored validation result; callers should run `validate` again before publishing. Published versions are immutable at the persistence layer. Reopen creates a new draft version with a fresh `ETag` instead of mutating the published document.

List responses return one summary per package family, ordered by most recent update:

```json
[
  {
    "formId": "inspection-form",
    "title": "Inspection Form",
    "serviceId": "test",
    "layerId": 0,
    "currentDraftVersion": 2,
    "currentPublishedVersion": 1,
    "updatedAt": "2026-05-25T00:00:00Z"
  }
]
```

## Runtime Routes

| Method | Route | Success | Contract |
|---|---|---:|---|
| `GET` | `/api/v1/forms/packages/{formId}` | `200` | Returns the current published `FormPackageVersion`; `Cache-Control: private, max-age=60, must-revalidate`. |
| `GET` | `/api/v1/forms/packages/{formId}/versions/{packageVersion}` | `200` | Returns a published version only; same private cache policy. |
| `GET` | `/api/v1/forms/packages/{formId}/offline-policy` | `200` | Returns `FormOfflinePolicyResponse`; always `Cache-Control: no-store`. |
| `POST` | `/api/v1/forms/packages/{formId}/submissions` | `200` | Submits JSON-compatible or multipart field data against a published version; returns `FormSubmissionResponse`. |

Runtime reads return `404` for missing packages and for versions that exist but are not published.

## Package Document

The package document schema version is `honua.form-package.v1`. Important top-level fields:

| Field | Notes |
|---|---|
| `formId` | Optional on create. The server generates a stable `form-{32-hex-guid}` id when omitted. |
| `target.serviceId`, `target.layerId` | Target feature service and layer for validation and submissions. |
| `sections[]` | Ordered field groupings. Section references are validated. |
| `fields[]` | Form controls keyed by `fieldId`; non-attachment fields bind to `targetField`. |
| `submitPolicy` | Allowed edit operations, geometry requirement, attachment allowance, and optional max offline age. |
| `attachmentPolicy` | Package attachment limits, package content types, per-field attachment policy metadata, and privacy transform requirements. |
| `privacyPolicy` | Private field ids, supported transformations, actor/device capture hints, and retention hint. |
| `offlinePolicy` | Whether offline use is enabled and which existing sync transports may be advertised. |
| `provenance`, `metadata` | Optional non-secret authoring metadata. |

`FormPackageVersion` responses include `formId`, `version`, `status`, `package`, optional `validation`, `contentHash`, `policyHash`, `etag`, creation/publish metadata, and `reopenedFromVersion` when applicable.

## Validation

Publish validation checks the package document against the target feature service and layer. It validates:

- Schema version, title, field ids, duplicate ids, section references, and section field references.
- Target service/layer existence, target field existence, writable target fields, compatible field types, and required state for non-null target fields.
- Submit operations against target capabilities (`Create`, `Update`, `Delete`).
- Coded-value and range domains, validation rule limits, supported conditional visibility operators (`equals`, `notEquals`, `gt`, `gte`, `lt`, `lte`, `isEmpty`, `isNotEmpty`, `in`), and visibility cycles.
- Attachment enablement, package-level limits, allowed MIME types, per-field attachment `required`/`maxCount`/MIME policy (one policy entry per attachment field, with `maxCount` between 1 and the package limit), attachment field references, and unsupported server-side attachment transform flags. Exact MIME values and subtype wildcards such as `image/*` are supported. Package and field allowlists are checked against global server attachment limits; field allowlists must be at least as restrictive as the package allowlist. EXIF stripping, face blur, and redaction must be performed before submission in this release.
- Privacy private-field references, supported privacy transformations (`none`, `auditOnly`, `minimizeAudit`), and retention bounds.
- Offline transport selection when `offlinePolicy.enabled` is `true`. `feature-server-replica` and `fieldcollection` are the supported transport identifiers. At least one transport flag must be enabled. `conflictReviewMode` accepts `defer` or `lastWriteWins`; other values produce a warning because full conflict review is deferred.

Validation responses use:

```json
{
  "isValid": false,
  "issues": [
    {
      "code": "targetFieldNotFound",
      "severity": "error",
      "fieldId": "name",
      "path": "fields",
      "message": "Target field 'missing' was not found on layer 'Inspections'."
    }
  ]
}
```

## Submissions

Submissions are accepted only for published packages. The request body may be `multipart/form-data` or a JSON-compatible content type whose media type contains `json`, such as `application/json` or `application/vnd.honua.form-submission+json`. Malformed bodies, unsupported content types, route authorization failures, and target data-editor authorization failures use the shared problem/auth response path before a `FormSubmissionResponse` can be created.

JSON submission example:

```json
{
  "idempotencyKey": "device-42-0001",
  "formVersion": 1,
  "operation": "create",
  "clientId": "device-42",
  "values": {
    "name": "Inspection A"
  },
  "geometry": {
    "x": -157.8583,
    "y": 21.3069,
    "spatialReference": { "wkid": 4326 }
  },
  "attachments": []
}
```

`formVersion` is optional. When omitted, the current published version is used. If `clientId` is omitted, the server uses `X-Honua-Client-Id` when present. JSON submissions can carry field values and geometry, but attachment bytes require `multipart/form-data`; descriptors without their referenced multipart file part are rejected before the edit is applied.

Runtime validation checks:

- Package status, allowed operation, and `targetFeatureId` for update/delete.
- Required values, read-only fields, JSON value types, domain membership, and target field compatibility.
- Point geometry shape, finite coordinates, and SRID. Geometry uses GeoServices-style `{ "x": number, "y": number, "spatialReference": { "wkid": number } }`; `x` and `y` may be finite JSON numbers or numeric strings. When supplied, `spatialReference.latestWkid` is preferred over `wkid`, and common Web Mercator aliases (`102100`, `102113`, `900913`, `3785`) normalize to `3857` before matching the target layer SRID.
- Required attachment fields for create/update submissions (a field counts as required when its field `required` flag or its per-field attachment policy `required` is set), package and per-field attachment counts, package/field/global MIME type allowlists, file part presence, unique multipart part names, file size, filename/content security, and global attachment limits. Delete submissions do not require attachment fields and cannot include attachment descriptors.

Accepted submissions are translated into the shared edit pipeline (`IEditProcessor` and `IFeatureWriter`). Non-attachment field values are mapped from form `fieldId` to target layer `targetField`. Attachment upload runs only after a successful create/update edit produces or resolves a target feature id. Failed edits and delete operations do not persist attachments.

## Multipart Attachments

Multipart submissions must include a `submission` JSON part plus file parts referenced by each attachment descriptor `partName`.

```json
{
  "idempotencyKey": "device-42-photo-0001",
  "operation": "create",
  "clientId": "device-42",
  "values": {
    "name": "Inspection with photo"
  },
  "geometry": {
    "x": -157.8583,
    "y": 21.3069,
    "spatialReference": { "wkid": 4326 }
  },
  "attachments": [
    {
      "clientAttachmentId": "photo-1",
      "fieldId": "photo",
      "partName": "photo-file",
      "filename": "photo.png",
      "contentType": "image/png",
      "sizeBytes": 12042,
      "sha256": "optional-client-checksum"
    }
  ]
}
```

The server normalizes missing descriptor `filename` and `contentType` from the uploaded file when the multipart part exists and the part name is unique. `sizeBytes` validation and persistence use the uploaded file length so clients cannot underreport total attachment size. Descriptor filenames are validated with the same upload filename and extension checks used for the multipart file. Descriptor and file MIME types must satisfy the package allowlist, any per-field allowlist, and the global server allowlist; when a package or field allowlist is empty, the next broader policy supplies the constraint. Accepted files are stored through the FeatureServer attachment store and return per-file outcomes. The optional descriptor `sha256` is retained with submitted attachment metadata when supplied, but this slice does not perform checksum enforcement. Descriptors that name missing or duplicate parts are rejected with attachment validation issues and do not run the feature edit path.

For idempotency, multipart requests hash the original `submission` JSON part. Metadata filled from file parts is used for validation and persistence, but it does not rewrite the replay hash or require clients to echo file-derived descriptor values.

## Submission Response

`FormSubmissionResponse` is returned for accepted, rejected, replayed, and failed submissions:

```json
{
  "submissionId": "3ad9b4cf-3ed2-4b0b-bf64-705f5ebdf9de",
  "status": "accepted",
  "formId": "inspection-form",
  "formVersion": 1,
  "operation": "create",
  "targetFeatureId": 123,
  "editOutcome": {
    "succeeded": true,
    "created": 1,
    "updated": 0,
    "deleted": 0
  },
  "attachmentOutcomes": [
    {
      "clientAttachmentId": "photo-1",
      "fieldId": "photo",
      "status": "accepted",
      "attachmentId": 456,
      "privacyApplied": true
    }
  ],
  "validationIssues": [],
  "idempotentReplay": false
}
```

HTTP and response status behavior:

- `200` with `status: "accepted"` when the edit path completed. If the feature edit reports errors, the response may be `200` with `status: "failed"` and a sanitized `editOutcome.error`.
- `200` with `idempotentReplay: true` when an idempotency replay matches the stored request body. The replay body is the stored terminal response and may carry `status: "accepted"`, `"rejected"`, or `"failed"`.
- `400` with `status: "rejected"` for correctable validation failures.
- `403` with `status: "rejected"` when package policy denies the operation or attachments.
- `404` when the published package or target service/layer cannot be found.
- `409` when an idempotency key is reused with a different payload or the prior submission is still pending.
- `500` with `status: "failed"` and retry guidance when the server cannot complete the submission, including when post-claim processing exceeds the server-owned time budget.

`retry` is omitted from accepted responses and included on rejected/failed responses when clients need retry guidance. Validation rejections use `retryable: false`; package-policy denials and server failures use `retryable: true` with a sanitized reason and optional `retryAfterSeconds`. A `200` response with `status: "failed"` from an unsuccessful feature edit also carries `retryable: true` guidance, and attachments are not uploaded in that case.

The submissions route does not use a form-specific response cache. Clients should treat POST responses as non-cacheable and use idempotency keys for replay; `idempotentReplay: true` responses come from the durable submission record, not from HTTP caching.

Attachment outcomes use `accepted`, `rejected`, or `failed`. A feature edit can be accepted while one or more attachment outcomes are rejected or failed after the target feature id is resolved. A submission rejected during validation (`400`/`403`) also returns `attachmentOutcomes` with `status: "rejected"` for the attachments tied to attachment validation issues, recorded before any edit runs. Rejection and failure reasons are sanitized. Each outcome is persisted with the submission id and audited as a form attachment policy event.

## Idempotency And Privacy

Idempotency is scoped by form id, package version, actor/client hash, and `idempotencyKey`. The server stores a hash of the original JSON request body. For multipart requests, that hash is based on the `submission` JSON part, before file-derived descriptor normalization. An exact replay returns the stored terminal response with `idempotentReplay: true`; a changed payload with the same key returns `409`.

When two requests race after the initial lookup, only the caller that creates the pending submission row owns the validation/edit/upload path. A caller that loses the idempotency claim re-reads the stored record: if the record is terminal, the stored response is returned as an idempotent replay; if it is still pending, the caller receives `409` and does not apply another edit.

Submission records store minimized request summaries instead of raw private field values. The summary includes operation, submitted field ids, private field ids, attachment count, optional `targetFeatureId`, client id when allowed by privacy policy, and the client submission time when provided. Field ids listed in `privacyPolicy.privateFieldIds` and fields marked with `"private": true` are treated as private. Package and policy hashes are stored on the durable submission row beside the summary. Private values are not copied into the request summary. Attachment policy outcomes are recorded per submitted attachment.

## Offline Policy

The offline policy endpoint advertises existing sync surfaces instead of creating a form-only sync protocol. The response shape is:

```json
{
  "formId": "inspection-form",
  "formVersion": 1,
  "enabled": true,
  "serviceId": "test",
  "layerId": 0,
  "availableTransports": [
    "feature-server-replica",
    "fieldcollection"
  ],
  "links": [
    {
      "rel": "create-replica",
      "href": "https://example.com/rest/services/test/FeatureServer/createReplica",
      "method": "POST"
    }
  ],
  "requiredHeaders": {
    "X-Honua-Client-Id": "Stable client or device identifier for cursor/idempotency coordination."
  }
}
```

When `offlinePolicy.enabled` is `false`, `availableTransports` and `links` are empty while target metadata and `requiredHeaders` remain present. When enabled, `replicaTransportEnabled` adds FeatureServer replica links with rels `create-replica`, `extract-changes`, `synchronize-replica`, and `unregister-replica`. `fieldCollectionTransportEnabled` adds rels `fieldcollection-generation`, `fieldcollection-sync-cursor`, `fieldcollection-ack-cursor`, `fieldcollection-changes`, and `fieldcollection-push-change`.

`offlinePolicy.preferredTransports` is validated when offline use is enabled and retained in the package document for client preference metadata. The offline-policy response advertises enabled transports in stable server order: `feature-server-replica` first, then `fieldcollection`.

Offline links are absolute and derived from the incoming request scheme, host, and path base. Clients should send `X-Honua-Client-Id` on sync and submission requests for cursor correlation and idempotency scoping.

## Operational Notes

- Forms emit OpenTelemetry activities for package listing, draft creation, offline-policy reads, and submissions with `honua.protocol=forms` and operation tags.
- Structured source-generated logs use event ids in the `1184xx` range for package lifecycle, offline-policy reads, submissions, and attachment outcomes.
- Package lifecycle actions, submission outcomes, and attachment policy outcomes are written to the shared audit log with `resourceType: "form_package"`.
- Published runtime package reads use short private caching. Admin package reads/writes and offline-policy responses use `no-store` to avoid stale authoring or sync policy state. Submission POST responses are replayed from durable idempotency records rather than a response cache.
- After the idempotency claim is won, submission validation, the feature edit, and attachment upload run under a server-owned processing budget; exceeding it terminates the submission as `failed` (`retryable: true`, HTTP `500`). Terminal responses are then persisted with a separate server-owned timeout token, so a client disconnect after edit execution does not cancel the durable response used for later idempotent replay.
- No new form-specific offline conflict review API is introduced in this slice. Full disconnected conflict review remains outside this ticket.
