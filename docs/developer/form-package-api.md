# Form Package API

Honua Server owns form packages as versioned operational assets. Builders create editable drafts under the admin API, publish immutable versions after validation, and field clients submit against the current published runtime package.

## Lifecycle

Admin routes require admin authorization:

- `POST /api/v1/admin/forms/packages` creates a draft package.
- `GET /api/v1/admin/forms/packages` lists package families.
- `GET /api/v1/admin/forms/packages/{formId}` reads the current draft, or the current published version if no draft exists.
- `GET /api/v1/admin/forms/packages/{formId}/versions` lists all versions.
- `GET /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}` reads one version.
- `PUT /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}` updates a draft and requires `If-Match`.
- `POST /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/validate` validates schema and policy.
- `POST /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/publish` validates and publishes an immutable version.
- `POST /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/reopen` creates a new draft from a published version.

Published runtime routes are protected by the current server auth/RBAC policy:

- `GET /api/v1/forms/packages/{formId}` reads the current published package.
- `GET /api/v1/forms/packages/{formId}/versions/{packageVersion}` reads a published version.
- `GET /api/v1/forms/packages/{formId}/offline-policy` returns sync transport policy and links.
- `POST /api/v1/forms/packages/{formId}/submissions` submits JSON or multipart form data.

## Validation

Publish validation checks the package document against the target feature service and layer. It validates field ids, section references, target field existence and compatible types, required state against non-null target fields, coded-value and range domains, validation rule limits, conditional visibility dependencies and cycles, allowed submit operations, attachment policy limits and MIME types, privacy transformations, and offline transport choices.

Submissions are accepted only for published versions. Runtime validation checks the package operation policy, required values, read-only fields, value types, domain membership, point geometry and SRID, required attachments, attachment counts, content types, and file upload security.

## Submission Shape

JSON submission example:

```json
{
  "idempotencyKey": "device-42-0001",
  "operation": "create",
  "clientId": "device-42",
  "values": {
    "name": "Inspection A"
  },
  "geometry": {
    "x": -157.8583,
    "y": 21.3069,
    "spatialReference": { "wkid": 4326 }
  }
}
```

Multipart submissions use a `submission` JSON part plus file parts named by each attachment descriptor `partName`. Idempotency is scoped by form id, version, actor/client hash, and `idempotencyKey`; an exact replay returns the stored response with `idempotentReplay: true`, while a changed payload with the same key returns `409`.

Submission records store minimized request summaries, not raw private field values. Attachment policy outcomes are recorded per submitted attachment and persisted with the submission id.

## Offline Policy

The offline policy response advertises existing sync surfaces instead of creating a separate form-only sync protocol. When enabled, it can include FeatureServer replica links (`create-replica`, `extract-changes`, `synchronize-replica`, `unregister-replica`) and FieldCollection links (`fieldcollection-generation`, cursor, pull, and push routes). Field clients should send `X-Honua-Client-Id` for sync correlation.
