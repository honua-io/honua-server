# Back-Office Field Workflows API

Server- and admin-owned back-office workflows for mobile field collection (Fulcrum / Survey123 parity). These surfaces are owned by `honua-server`; the admin UI lives in `honua-console` and mobile consumes the contracts through published `Honua.Sdk.*` packages. Mobile and console must **not** define provider-neutral DTOs locally — the canonical contracts below are the single source of truth.

Covers:

- **#1158** — published-form offline compatibility & migration signals (layered on the [Form Package API](./form-package-api.md)).
- **#1159** — field data review & QA over mobile-submitted records.

Related slices: [Form Package API](./form-package-api.md), [FieldCollection Mobile Sync API](./fieldcollection-mobile-sync-api.md).

## Offline compatibility & migration signals (#1158)

Published form metadata already includes a monotonic `version`, a `contentHash`, and a `policyHash` (see [Form Package API](./form-package-api.md)). The compatibility endpoint turns those into explicit, machine-readable signals an offline client uses to reconcile pending offline edits with the server-current published version.

| Method | Route | Success | Contract |
|---|---|---:|---|
| `GET` | `/api/v1/forms/packages/{formId}/compatibility?clientVersion={n}` | `200` | Returns `FormCompatibilityManifest`; `Cache-Control: no-store`. |

- `clientVersion` is the version the offline client currently has cached. When omitted (or equal to the current published version) the manifest is `current`.
- `404` when the form has no published version.

`FormCompatibilityManifest` (contract in `Honua.Core.Features.Forms.Packages`):

| Field | Meaning |
|---|---|
| `compatibility` | `current` \| `compatible` \| `breaking` \| `unknown`. |
| `offlineEditsSubmittable` | `true` when offline edits captured against `clientVersion` may be submitted against the current published version without migration. |
| `refreshRecommended` | `true` when the client should refresh its cached package. |
| `migrationRequired` | `true` when pending edits must be migrated/re-validated before submitting. |
| `clientVersion` / `currentPublishedVersion` | The compared versions. |
| `clientContentHash` / `currentContentHash` / `clientPolicyHash` / `currentPolicyHash` | Hashes for client-side diffing. |
| `migrationSignals[]` | Ordered `{ code, severity, message }` signals: `targetChanged` (breaking), `policyChanged` (breaking), `contentChanged` (info), `versionUnknown` (breaking). |

Classification rules: a change to the submission **target** (service/layer) or to **submit/attachment/privacy/offline policy** (the `policyHash`) is `breaking` (offline edits may be rejected). A pure field/layout change (different `contentHash` only) is `compatible` (refresh recommended, edits still submittable). An unknown/purged `clientVersion` is `unknown` and requires re-provisioning.

## Field data review & QA (#1159)

Back-office supervisors inspect, filter, assign, comment on, and approve/reject mobile-submitted field records. Review state is **server-owned** and layered over the durable `honua.form_submissions` table written by the runtime submission path — reviewing a record never mutates the submission itself. Every review state transition is **audited** (`AuditEventType.AdminAction`, `resourceType = field_submission`) and gated by **admin authorization**.

Routes are registered when an `IFieldReviewStore` is available (Postgres provider).

| Method | Route | Success | Contract |
|---|---|---:|---|
| `GET` | `/api/v1/admin/field-workflows/submissions` | `200` | Returns `FieldSubmissionListResult` (`items`, `total`, `limit`, `offset`); `no-store`. |
| `GET` | `/api/v1/admin/field-workflows/submissions/{submissionId}` | `200` | Returns `FieldSubmissionDetail` (record + comments + attachment metadata). |
| `POST` | `/api/v1/admin/field-workflows/submissions/{submissionId}/assignment` | `200` | Assign/unassign a reviewer. Body `FieldReviewAssignmentRequest`; returns `FieldReviewState`. Assigning a `pending` record moves it to `in_review`. |
| `POST` | `/api/v1/admin/field-workflows/submissions/{submissionId}/decision` | `200` | Approve / reject / request changes. Body `FieldReviewDecisionRequest` (`status` ∈ `approved`, `rejected`, `changes_requested`). Honors `If-Match` for optimistic concurrency; returns `FieldReviewState` with `ETag`. |
| `POST` | `/api/v1/admin/field-workflows/submissions/{submissionId}/comments` | `201` | Add a reviewer comment or correction request. Body `FieldReviewCommentRequest`; a `correctionRequest=true` comment transitions the record to `changes_requested`. Returns `FieldReviewComment`. |

### Filters (`GET /submissions`)

`formId`, `serviceId`, `layerId`, `reviewStatus`, `assignedTo`, `syncStatus`, `hasConflict`, `submittedFrom`, `submittedTo` (ISO-8601), `limit` (1–500, default 50), `offset`.

### Contracts (`Honua.Core.Features.FieldWorkflows.Review`)

- `FieldSubmissionRecord` — read projection of a submission: geometry/attachment counts, validation state, submitter hash, device id, `syncStatus`, conflict indicator, and the embedded `FieldReviewState`.
- `FieldReviewState` — `status` (`pending` \| `in_review` \| `changes_requested` \| `approved` \| `rejected`), `assignedTo`, `decidedBy`, `decidedAt`, `decisionNote`, `updatedAt`, `etag`.
- `FieldReviewComment`, `FieldSubmissionDetail`, `FieldSubmissionAttachmentInfo`, request DTOs, and `FieldSubmissionListResult`.

Expected failures: `400` (invalid body, invalid decision status, bad filter value), `404` (unknown submission), `409` (stale `If-Match` on decision).

> **Sync status note:** `syncStatus` is derived from the underlying submission lifecycle (`synced` / `rejected` / `failed` / `pending`) so reviewers never read provider-internal status values. Submitted geometry is applied to the target feature at submit time and is not duplicated in the review projection (`geometry` is null); use the target FeatureServer/OGC read surfaces for current geometry.

## SDK / mobile dependency surface

Mobile and console consume these contracts via published packages only:

- **.NET:** `Honua.Sdk.*` (consume `Honua.Core.Features.Forms.Packages.FormCompatibilityManifest` and `Honua.Core.Features.FieldWorkflows.Review.*` through the SDK; do not add SDK source or sibling project references to this repo).
- **JS/TS:** `honua-sdk-js`.
- **Python:** `honua-sdk-python`.

Mobile-side follow-ups are limited to UX/adapters that call these routes and bind the published DTOs. No provider-neutral admin/review/export DTOs should be defined in `honua-mobile`. Cross-repo tracking: honua-io/honua-mobile#219.

## Source map

- Core contracts: `src/Honua.Core/Features/Forms/Packages/FormCompatibilityContracts.cs`, `src/Honua.Core/Features/FieldWorkflows/Review/`.
- Server routes/services: `src/Honua.Server/Features/Forms/` (compatibility), `src/Honua.Server/Features/FieldWorkflows/Review/`.
- Postgres persistence: `src/Honua.Postgres/Features/FieldWorkflows/PostgresFieldReviewStore.cs` (migration `050_CreateFieldReview.sql`).
