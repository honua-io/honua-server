---
type: reference
title: "Forms"
description: "Author forms in XLSForm workbooks. Import creates an editable form-package draft; validate it against the target layer, publish an immutable runtime version, and export the published version back to XLSForm."
resource: "honua://capability/fieldops.forms"
resources:
  - "honua://capability/fieldops.offline-sync"
---
# Forms

Author forms in XLSForm workbooks. Import creates an editable form-package draft; validate it against the target layer, publish an immutable runtime version, and export the published version back to XLSForm. Field clients fetch published packages and submit data against them.

All routes require admin authentication — see [Authentication](../../guides/secure/authentication.md). Submissions additionally require edit permission on the target service/layer.

Also available in Honua Console — UI guide coming soon.

## Authoring (drafts, validation, publish)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/forms/packages` | List package summaries (one per package family) |
| POST | `/api/v1/admin/forms/packages/import?serviceId={serviceId}&layerId={layerId}` | Import an XLSForm workbook as a draft |
| GET | `/api/v1/admin/forms/packages/{formId}/export` | Export the current published version as XLSForm |
| POST | `/api/v1/admin/forms/packages` | Create draft version 1, or the next draft for an existing `formId` |
| GET | `/api/v1/admin/forms/packages/{formId}` | Get the current draft (falls back to the current published version) |
| GET | `/api/v1/admin/forms/packages/{formId}/versions` | List all versions, newest first |
| GET | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}` | Get one version with `ETag` |
| PUT | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}` | Update a draft; requires the exact `ETag` in `If-Match` |
| POST | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/validate` | Validate against the target service/layer; stores the result for drafts |
| POST | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/publish` | Validate and publish a draft as an immutable version |
| POST | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/reopen` | Create a new draft from a published version |

### Import and export XLSForm

Upload an `.xlsx` workbook as the `file` part of a `multipart/form-data` request to the import route. Supply the target service and layer in the query string. A successful import returns `201` with the draft package version. Import does not publish: use the existing validate and publish routes after checking the resulting draft.

The workbook uses `survey`, `choices`, and `settings` sheets and may be at most 8 MiB (`413` above that). Each question binds to the target layer attribute of the same name: `text`, `barcode`, `select_one`, `date` and `dateTime` questions keep their meaning, and `integer` or `decimal` questions become `integer` or `number` fields by the attribute's type. `settings.form_id` becomes the package `formId`, so importing a workbook again adds the next draft of the same package.

Unsupported question types (for example `select_multiple`, `geopoint`, media, or a `calculate` without a matching attribute), repeat groups, and invalid cells produce `400` with a problem body whose `diagnostics` array names each blocking cell as `sheet`, `row`, `column` and `message`. Correct the workbook and import again; an unsuccessful import creates no draft. Empty or malformed archives also return `400`. `relevant`, `constraint` and `calculation` expressions are kept on the draft under `expressionLanguage: "xlsform"`, but validate and publish reject them until an evaluation policy is available.

Export returns the current published package as an `.xlsx` download; it returns `404` when the package has no published version, so publish drafts first. Fields whose behavior XLSForm cannot carry (such as `boolean` fields, private or default values, validation or visibility rules, a `targetField` that differs from the field id, or repeatable sections) return `422` with the same `diagnostics` body, pointing at the survey row each field would occupy, rather than a workbook that silently omits behavior. Submit, attachment, privacy and offline policies are server configuration and are not part of the workbook.

Contract notes:

- The package document schema version is `honua.form-package.v1`; `formId` is server-generated when omitted on create.
- Draft saves do not run publish validation, and draft updates clear the stored validation result — run `validate` again before `publish`.
- Published versions are immutable at the persistence layer; `reopen` creates a new draft with `reopenedFromVersion` set.
- Failures: `400` invalid package JSON, failed publish validation, or XLSForm import diagnostics, `404` unknown package/version, `409` stale `ETag` or non-draft target, `413` workbook over 8 MiB, `422` XLSForm export diagnostics, `428` missing `If-Match` on `PUT`.

In the authorized [API explorer](../openapi-and-explorer.md), run `POST /api/v1/admin/forms/packages/{formId}/versions/1/publish`, substituting `inspection-form`.

## Runtime (published packages and submissions)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/forms/packages/{formId}` | Get the current published version |
| GET | `/api/v1/forms/packages/{formId}/versions/{packageVersion}` | Get a specific published version (`404` for unpublished versions) |
| GET | `/api/v1/forms/packages/{formId}/offline-policy` | Discover the offline sync transports advertised for the package |
| GET | `/api/v1/forms/packages/{formId}/compatibility` | Get the offline compatibility and migration manifest for a published version (optional `?clientVersion=`) |
| POST | `/api/v1/forms/packages/{formId}/submissions` | Submit field data against a published version |

Run `POST /api/v1/forms/packages/{formId}/submissions` with this body:

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
- `formVersion` is optional and defaults to the current published version. `clientId` falls back to the `X-Honua-Client-Id` header.
- Idempotency is scoped by form id, package version, actor/client hash, and `idempotencyKey`. Exact replays return the stored terminal response with `idempotentReplay: true`; a changed payload with the same key returns `409`.
- Responses: `200` accepted (or failed edit with retry guidance), `400` rejected validation failures, `403` policy denials, `404` missing published package or target layer, `409` idempotency conflicts, `500` server failure with `retryable: true`.
- Accepted submissions flow into the shared edit pipeline; attachments upload only after a successful create/update edit resolves the target feature id.
- Runtime validation enforces the current target field policy. Submitted values for read-only or hidden target fields are rejected; updates also reject changes or null removals of the bound GlobalID. Publishing a package does not freeze target field permissions.

### Offline policy

The offline-policy endpoint advertises existing sync surfaces rather than a form-specific protocol. When the package enables offline use, `availableTransports` lists `feature-server-replica` and/or `fieldcollection`, and `links` carries absolute URLs for the corresponding replica or FieldCollection sync endpoints. Clients should send `X-Honua-Client-Id` on sync and submission requests for cursor correlation and idempotency scoping.

## Related guides

- [Edit features](../../guides/edit/edit-features.md)
- [Attachments and related records](../../guides/edit/attachments-and-related-records.md)
