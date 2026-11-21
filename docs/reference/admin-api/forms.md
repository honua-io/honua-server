---
type: reference
title: "Forms"
description: "Reference for Forms: author Forms in XLSForm workbooks or JSON, create editable Drafts, validate them against the target Layer, go live with immutable Released Versions, export Released Versions back to XLSForm, and reopen a Released Version as a new Draft."
resource: "honua://capability/fieldops.forms"
resources:
  - "honua://capability/fieldops.offline-sync"
---
# Forms

Reference for Forms: author Forms in XLSForm workbooks or JSON. Import creates an editable Draft; validate it against the target Layer, go live with an immutable Released Version, export the Released Version back to XLSForm, and reopen a Released Version as a new Draft. Field clients fetch Released Form Versions and submit data against them.

All routes require admin authentication — see [Authentication](../../guides/secure/authentication.md). Submissions additionally require edit permission on the target Service/Layer.

Also available in Honua Console — UI guide coming soon.

## Authoring (Drafts, validation, go live)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/forms` | List Forms |
| POST | `/api/v1/admin/forms` | Create Draft Version 1, or the next Draft for an existing `formId` |
| POST | `/api/v1/admin/forms/import?serviceId={serviceId}&layerId={layerId}` | Import an XLSForm workbook as a Draft |
| GET | `/api/v1/admin/forms/{formId}/export` | Export the current Released Version as XLSForm |
| GET | `/api/v1/admin/forms/{formId}` | Get the current Draft (falls back to the current Released Version) |
| GET | `/api/v1/admin/forms/{formId}/versions` | List all Versions, newest first |
| GET | `/api/v1/admin/forms/{formId}/versions/{formVersion}` | Get one Version with `ETag` |
| PUT | `/api/v1/admin/forms/{formId}/versions/{formVersion}` | Update a Draft; requires the exact `ETag` in `If-Match` |
| POST | `/api/v1/admin/forms/{formId}/versions/{formVersion}/validate` | Validate against the target Service/Layer; stores the result for Drafts |
| POST | `/api/v1/admin/forms/{formId}/versions/{formVersion}/publish` | Validate a Draft and go live with an immutable Released Version |
| POST | `/api/v1/admin/forms/{formId}/versions/{formVersion}/reopen` | Create a new Draft from a Released Version |

### Import and export XLSForm

Upload an `.xlsx` workbook as the `file` part of a `multipart/form-data` request to the import route. Supply the target service and layer in the query string. A successful import returns `201` with the Draft Version. Import does not go live: use the existing validate and publish routes after checking the resulting Draft.

The workbook uses `survey`, `choices`, and `settings` sheets and may be at most 8 MiB (`413` above that). Each question binds to the target layer attribute of the same name: `text`, `barcode`, `select_one`, `date` and `dateTime` questions keep their meaning, and `integer` or `decimal` questions become `integer` or `number` fields by the attribute's type. `settings.form_id` becomes the Form `formId`, so importing a workbook again adds the next Draft of the same Form.

Unsupported question types (for example `select_multiple`, `geopoint`, media, or a `calculate` without a matching attribute), repeat groups, and invalid cells produce `400` with a problem body whose `diagnostics` array names each blocking cell as `sheet`, `row`, `column` and `message`. Correct the workbook and import again; an unsuccessful import creates no Draft. Empty or malformed archives also return `400`. `relevant`, `constraint` and `calculation` expressions are kept on the Draft under `expressionLanguage: "xlsform"`, but validate and publish reject them until an evaluation policy is available.

Export returns the current Released Version as an `.xlsx` download; it returns `404` when the Form has no Released Version, so publish Drafts first. Fields whose behavior XLSForm cannot carry (such as `boolean` fields, private or default values, validation or visibility rules, a `targetField` that differs from the field id, or repeatable sections) return `422` with the same `diagnostics` body, pointing at the survey row each field would occupy, rather than a workbook that silently omits behavior. Submit, attachment, privacy and offline policies are server configuration and are not part of the workbook.

Contract notes:

- Routes and operation names use Form directly. Regenerate clients after the [Forms route migration](../control-plane-migration-guide.md#forms-routes-and-operation-names).
- The persisted document schema version is `honua.form-package.v1`; `formId` is server-generated when omitted on create.
- Draft saves do not run go-live validation, and Draft updates clear the stored validation result — run `validate` again before `publish`.
- Released Versions are immutable at the persistence layer; `reopen` creates a new Draft with `reopenedFromVersion` set.
- Failures: `400` invalid Form JSON, failed go-live validation, or XLSForm import diagnostics, `404` unknown Form/Version, `409` stale `ETag` or non-draft target, `413` workbook over 8 MiB, `422` XLSForm export diagnostics, `428` missing `If-Match` on `PUT`.

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
- Responses: `200` accepted (or failed edit with retry guidance), `400` rejected validation failures, `403` policy denials, `404` missing Released Form or target Layer, `405` when the target Layer does not accept edits (for example a Layer served from its source table), with `retryable: false` when no edit was dispatched, `409` idempotency conflicts, `500` server failure with `retryable: true`.
- Accepted submissions flow into the shared edit pipeline; attachments upload only after a successful create/update edit resolves the target feature id.
- Runtime validation enforces the current target field policy. Submitted values for read-only or hidden target fields are rejected; updates also reject changes or null removals of the bound GlobalID. Going live with a Form does not freeze target field permissions.

### Offline policy

The offline-policy endpoint advertises existing sync surfaces rather than a form-specific protocol. When the Form enables offline use, `availableTransports` lists `feature-server-replica` and/or `fieldcollection`, and `links` carries absolute URLs for the corresponding replica or FieldCollection sync endpoints. Clients should send `X-Honua-Client-Id` on sync and submission requests for cursor correlation and idempotency scoping.

## Related guides

- [Edit features](../../guides/edit/edit-features.md)
- [Attachments and related records](../../guides/edit/attachments-and-related-records.md)
