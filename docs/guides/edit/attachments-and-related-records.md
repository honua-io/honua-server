# Add attachments and query related records

Attach files to individual features, query and download them, and traverse layer relationships — all through the GeoServices FeatureServer surface.

**Prerequisites:** A writable FeatureServer layer and at least one feature in it (see [Edit features](edit-features.md)). Writes honor the same access policy and write-role checks as feature edits, so authenticate the mutation commands if the layer is protected.

Attachments are keyed by integer object IDs (`globalIds` filters are rejected) and are size- and type-limited by `Limits__Attachments__*` (defaults: 5 MB per file, 5 per feature, 50 MB total per feature, MIME allowlist `image/*,application/pdf`).

## Steps

### 1. Add an attachment

```bash
BASE=http://localhost:8080
SERVICE=parks
LAYER=0
OBJECTID=1
curl -X POST "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/$OBJECTID/addAttachment" \
  -F "attachment=@photo.jpg" -F "keywords=site-photo"
```

The body must be `multipart/form-data` (or form-urlencoded); the response returns the new `attachmentId`.

### 2. List a feature's attachments

```bash
curl "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/$OBJECTID/attachments?f=json"
```

### 3. Download attachment content

```bash
ATTACHMENT_ID=1
curl -o photo.jpg "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/$OBJECTID/attachments/$ATTACHMENT_ID"
```

### 4. Query attachments across features

```bash
curl "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/queryAttachments?objectIds=1,2&f=json"
```

Optional facets: `attachmentTypes` (MIME list), `keywords`, `size` (inclusive `lower,upper` byte range), `definitionExpression` (SQL WHERE that narrows the parent features), and `returnUrl=true` to include download URLs.

### 5. Update or delete attachments

```bash
curl -X POST "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/$OBJECTID/updateAttachment" \
  -F "attachmentId=$ATTACHMENT_ID" -F "keywords=updated" -F "attachment=@photo-v2.jpg"
curl -X POST "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/$OBJECTID/deleteAttachments" \
  -d "attachmentIds=$ATTACHMENT_ID"
```

`updateAttachment` replaces keywords and, when a file is supplied, the content; `deleteAttachments` takes a comma-separated `attachmentIds` list.

### 6. Query related records

```bash
curl "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/queryRelatedRecords?objectIds=$OBJECTID&relationshipId=0&outFields=*&f=json"
```

Relationships are defined per layer by the publisher (`GET /rest/services/$SERVICE/FeatureServer/relationships` lists them; admins manage them via `PUT /api/v1/admin/metadata/layers/{layerId}/relationships`). The same operation is available on MapServer layers.

## Verify

```bash
curl "$BASE/rest/services/$SERVICE/FeatureServer/$LAYER/queryAttachments?objectIds=$OBJECTID&f=json"
```

```json
{ "attachmentGroups": [ { "parentObjectId": 1, "attachmentInfos": [ { "id": 1, "name": "photo.jpg", "contentType": "image/jpeg", "size": 48211, "keywords": "site-photo" } ] } ] }
```

## Troubleshoot

| Symptom | Fix |
|---|---|
| `415 Unsupported Media Type` on add/update | Send `multipart/form-data` (curl `-F`), not raw JSON. |
| Upload rejected by size or type | Raise `Limits__Attachments__MaxAttachmentSize` / `MaxAttachmentsPerFeature` / `MaxTotalAttachmentSize` or extend `Limits__Attachments__AllowedMimeTypes`. |
| `globalIds is not supported` | Honua attachments are keyed by integer object IDs only; query with `objectIds`. |
| `401` / `403` on mutation | Attachment writes require write access to the layer, same as feature edits — see [Control access](../secure/access-control.md). |
| Empty `queryRelatedRecords` result | Confirm the `relationshipId` exists on `GET .../FeatureServer/relationships` and the related layer is published. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Edit features](edit-features.md)
- [React to feature changes](react-to-changes.md)
