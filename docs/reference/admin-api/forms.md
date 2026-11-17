---
type: reference
title: "Forms"
description: "Reference for Forms: builders create editable Drafts, validate them against the target Layer, go live with immutable Released Versions, and reopen a Released Version as a new Draft."
resource: "honua://capability/fieldops.forms"
resources:
  - "honua://capability/fieldops.offline-sync"
---
# Forms

Reference for Forms: builders create editable Drafts, validate them against the target Layer, go live with immutable Released Versions, and reopen a Released Version as a new Draft. Field clients fetch Released Form Versions and submit data against them.

All routes require admin authentication — see [Authentication](../../guides/secure/authentication.md). Submissions additionally require edit permission on the target Service/Layer.

Also available in Honua Console — UI guide coming soon.

## Authoring (Drafts, validation, go live)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/forms` | List Forms |
| POST | `/api/v1/admin/forms` | Create Draft Version 1, or the next Draft for an existing `formId` |
| GET | `/api/v1/admin/forms/{formId}` | Get the current Draft (falls back to the current Released Version) |
| GET | `/api/v1/admin/forms/{formId}/versions` | List all Versions, newest first |
| GET | `/api/v1/admin/forms/{formId}/versions/{formVersion}` | Get one Version with `ETag` |
| PUT | `/api/v1/admin/forms/{formId}/versions/{formVersion}` | Update a Draft; requires the exact `ETag` in `If-Match` |
| POST | `/api/v1/admin/forms/{formId}/versions/{formVersion}/validate` | Validate against the target Service/Layer; stores the result for Drafts |
| POST | `/api/v1/admin/forms/{formId}/versions/{formVersion}/publish` | Validate a Draft and go live with an immutable Released Version |
| POST | `/api/v1/admin/forms/{formId}/versions/{formVersion}/reopen` | Create a new Draft from a Released Version |

Contract notes:

- Routes and operation names use Form directly. Regenerate clients after the [Forms route migration](../control-plane-migration-guide.md#forms-routes-and-operation-names).

- The persisted document schema version is `honua.form-package.v1`; `formId` is server-generated when omitted on create.
- Draft saves do not run go-live validation, and Draft updates clear the stored validation result — run `validate` again before `publish`.
- Released Versions are immutable at the persistence layer; `reopen` creates a new Draft with `reopenedFromVersion` set.
- Failures: `400` invalid Form JSON or failed go-live validation, `404` unknown Form/Version, `409` stale `ETag` or non-draft target, `428` missing `If-Match` on `PUT`.

In the authorized [API explorer](../openapi-and-explorer.md), run `POST /api/v1/admin/forms/{formId}/versions/1/publish`, substituting `inspection-form`.

## Runtime (Released Versions and submissions)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/forms/{formId}` | Get the current Released Version |
| GET | `/api/v1/forms/{formId}/versions/{formVersion}` | Get a specific Released Version (`404` for Drafts) |
| GET | `/api/v1/forms/{formId}/offline-policy` | Discover the offline sync transports advertised for the Form |
| GET | `/api/v1/forms/{formId}/compatibility` | Get the offline compatibility and migration manifest for a Released Version (optional `?clientVersion=`) |
| POST | `/api/v1/forms/{formId}/submissions` | Submit field data against a Released Version |

Run `POST /api/v1/forms/{formId}/submissions` with this body:

```json
{
  "idempotencyKey": "device-42-0001",
  "operation": "create",
  "clientId": "device-42",
  "values": { "name": "Inspection A" },
  "geometry": {
    "x": -157.8583,
    "y": 21.3069,
    "spatialReference": { "wkid": 4326 }
  }
}
```

### Submissions

- Bodies may be JSON (`application/json` or any `*json` media type) or `multipart/form-data`. Attachment bytes require multipart: a `submission` JSON part plus one file part per attachment descriptor `partName`.
- `formVersion` is optional and defaults to the current Released Version. `clientId` falls back to the `X-Honua-Client-Id` header.
- Idempotency is scoped by Form ID, Form Version, actor/client hash, and `idempotencyKey`. Exact replays return the stored terminal response with `idempotentReplay: true`; a changed payload with the same key returns `409`.
- Responses: `200` accepted (or failed edit with retry guidance), `400` rejected validation failures, `403` policy denials, `404` missing Released Form or target Layer, `409` idempotency conflicts, `500` server failure with `retryable: true`.
- Accepted submissions flow into the shared edit pipeline; attachments upload only after a successful create/update edit resolves the target feature id.
- Runtime validation enforces the current target field policy. Submitted values for read-only or hidden target fields are rejected; updates also reject changes or null removals of the bound GlobalID. Going live with a Form does not freeze target field permissions.

### Offline policy

The offline-policy endpoint advertises existing sync surfaces rather than a form-specific protocol. When the Form enables offline use, `availableTransports` lists `feature-server-replica` and/or `fieldcollection`, and `links` carries absolute URLs for the corresponding replica or FieldCollection sync endpoints. Clients should send `X-Honua-Client-Id` on sync and submission requests for cursor correlation and idempotency scoping.

## Related guides

- [Edit features](../../guides/edit/edit-features.md)
- [Attachments and related records](../../guides/edit/attachments-and-related-records.md)
