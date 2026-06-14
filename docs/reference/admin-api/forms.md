# Forms

Reference for form packages: builders create editable drafts, validate them against the target layer, publish immutable runtime versions, and reopen a published version as a new draft. Field clients fetch published packages and submit data against them.

All routes require admin authentication — see [Authentication](../../guides/secure/authentication.md). Submissions additionally require edit permission on the target service/layer.

Also available in Honua Console — UI guide coming soon.

## Authoring (drafts, validation, publish)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/forms/packages` | List package summaries (one per package family) |
| POST | `/api/v1/admin/forms/packages` | Create draft version 1, or the next draft for an existing `formId` |
| POST | `/api/v1/admin/forms/packages/generate` | Generate or refine a form package from a natural-language prompt |
| GET | `/api/v1/admin/forms/packages/{formId}` | Get the current draft (falls back to the current published version) |
| GET | `/api/v1/admin/forms/packages/{formId}/versions` | List all versions, newest first |
| GET | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}` | Get one version with `ETag` |
| PUT | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}` | Update a draft; requires the exact `ETag` in `If-Match` |
| POST | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/validate` | Validate against the target service/layer; stores the result for drafts |
| POST | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/publish` | Validate and publish a draft as an immutable version |
| POST | `/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/reopen` | Create a new draft from a published version |

Contract notes:

- The package document schema version is `honua.form-package.v1`; `formId` is server-generated when omitted on create.
- Draft saves do not run publish validation, and draft updates clear the stored validation result — run `validate` again before `publish`.
- Published versions are immutable at the persistence layer; `reopen` creates a new draft with `reopenedFromVersion` set.
- Failures: `400` invalid package JSON or failed publish validation, `404` unknown package/version, `409` stale `ETag` or non-draft target, `428` missing `If-Match` on `PUT`.

```bash
HONUA_URL=https://honua.example.com
API_KEY=your-admin-key
FORM_ID=inspection-form
curl -X POST "$HONUA_URL/api/v1/admin/forms/packages/$FORM_ID/versions/1/publish" \
  -H "X-API-Key: $API_KEY"
```

## Runtime (published packages and submissions)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/forms/packages/{formId}` | Get the current published version |
| GET | `/api/v1/forms/packages/{formId}/versions/{packageVersion}` | Get a specific published version (`404` for unpublished versions) |
| GET | `/api/v1/forms/packages/{formId}/offline-policy` | Discover the offline sync transports advertised for the package |
| GET | `/api/v1/forms/packages/{formId}/compatibility` | Get the offline compatibility and migration manifest for a published version (optional `?clientVersion=`) |
| POST | `/api/v1/forms/packages/{formId}/submissions` | Submit field data against a published version |

```bash
curl -X POST "$HONUA_URL/api/v1/forms/packages/$FORM_ID/submissions" \
  -H "X-API-Key: $API_KEY" -H "Content-Type: application/json" \
  -d '{"idempotencyKey":"device-42-0001","operation":"create","clientId":"device-42","values":{"name":"Inspection A"},"geometry":{"x":-157.8583,"y":21.3069,"spatialReference":{"wkid":4326}}}'
```

### Submissions

- Bodies may be JSON (`application/json` or any `*json` media type) or `multipart/form-data`. Attachment bytes require multipart: a `submission` JSON part plus one file part per attachment descriptor `partName`.
- `formVersion` is optional and defaults to the current published version. `clientId` falls back to the `X-Honua-Client-Id` header.
- Idempotency is scoped by form id, package version, actor/client hash, and `idempotencyKey`. Exact replays return the stored terminal response with `idempotentReplay: true`; a changed payload with the same key returns `409`.
- Responses: `200` accepted (or failed edit with retry guidance), `400` rejected validation failures, `403` policy denials, `404` missing published package or target layer, `409` idempotency conflicts, `500` server failure with `retryable: true`.
- Accepted submissions flow into the shared edit pipeline; attachments upload only after a successful create/update edit resolves the target feature id.

### Offline policy

The offline-policy endpoint advertises existing sync surfaces rather than a form-specific protocol. When the package enables offline use, `availableTransports` lists `feature-server-replica` and/or `fieldcollection`, and `links` carries absolute URLs for the corresponding replica or FieldCollection sync endpoints. Clients should send `X-Honua-Client-Id` on sync and submission requests for cursor correlation and idempotency scoping.

## Related guides

- [Edit features](../../guides/edit/edit-features.md)
- [Attachments and related records](../../guides/edit/attachments-and-related-records.md)
