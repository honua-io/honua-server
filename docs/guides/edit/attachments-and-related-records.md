---
type: guide
title: "Add attachments and query related records"
description: "Attach files to individual features, query and download them, and traverse layer relationships — all through the GeoServices FeatureServer surface."
resource: "honua://capability/editing.featureserver-edits"
---
# Add attachments and query related records

Attach files to individual features, query and download them, and traverse layer relationships — all through the GeoServices FeatureServer surface.

**Prerequisites:** A writable FeatureServer layer and at least one feature in it (see [Edit features](edit-features.md)). The layer must also be published with attachment support: set `supportsAttachments` to `true` in the publish request (it defaults to `false`, and it does not enable editing by itself). The layer's metadata then reports `hasAttachments: true`; without the flag, attachment requests return "Attachments are not supported for this layer." Writes honor the same access policy and write-role checks as feature edits, so authenticate the mutation commands if the layer is protected.

Attachments are keyed by integer object IDs (`globalIds` filters are rejected) and are size- and type-limited by `Limits__Attachments__*` (defaults: 5 MB per file, 5 per feature, 50 MB total per feature, MIME allowlist `image/*,application/pdf,text/plain,text/csv,application/json`).

## Steps

### 1. Add an attachment

Use a client that supports the FeatureServer `addAttachment` operation. Supply the target
`objectId`, file content, file name, content type, and optional `keywords` value.

The body must be `multipart/form-data` (or form-urlencoded); the response returns the new `attachmentId`.

### 2. List a feature's attachments

Open `/rest/services/{service}/FeatureServer/{layerId}/{objectId}/attachments?f=json` at the deployment origin in a browser.

### 3. Download attachment content

Open `/rest/services/{service}/FeatureServer/{layerId}/{objectId}/attachments/{attachmentId}` at the deployment origin; the browser downloads the file.

### 4. Query attachments across features

Open `/rest/services/{service}/FeatureServer/{layerId}/queryAttachments?objectIds=1,2&f=json` at the deployment origin.

Optional facets: `attachmentTypes` (MIME list), `keywords`, `size` (inclusive `lower,upper` byte range), `definitionExpression` (SQL WHERE that narrows the parent features), and `returnUrl=true` to include download URLs.

### 5. Update or delete attachments

To replace it, run `POST /rest/services/{service}/FeatureServer/{layerId}/{objectId}/updateAttachment` with `attachmentId`, `keywords=updated`, and `attachment=photo-v2.jpg`. To delete it, run the sibling `deleteAttachments` operation with `attachmentIds={attachmentId}`.

`updateAttachment` replaces keywords and, when a file is supplied, the content; `deleteAttachments` takes a comma-separated `attachmentIds` list.

### 6. Query related records

> Open `/rest/services/{service}/FeatureServer/{layerId}/queryRelatedRecords?objectIds={objectId}&relationshipId=0&outFields=*&f=json` in a browser.

Relationships are defined per layer by the publisher (`GET /rest/services/{service}/FeatureServer/relationships` lists them; admins manage them via `PUT /api/v1/admin/metadata/layers/{storageLayerId}/relationships`). The same operation is available on MapServer layers.

## Verify

> Open `/rest/services/{service}/FeatureServer/{layerId}/queryAttachments?objectIds={objectId}&f=json` in a browser.

```json
{ "attachmentGroups": [ { "parentObjectId": 1, "attachmentInfos": [ { "id": 1, "name": "photo.jpg", "contentType": "image/jpeg", "size": 48211, "keywords": "site-photo" } ] } ] }
```

## Troubleshoot

| Symptom | Fix |
|---|---|
| `415 Unsupported Media Type` on add/update | Use `FeatureLayer.addAttachment` / `updateAttachment`, which generate the multipart request; do not send raw JSON. |
| Upload rejected by size or type | Raise `Limits__Attachments__MaxAttachmentSize` / `MaxAttachmentsPerFeature` / `MaxTotalAttachmentSize` or extend `Limits__Attachments__AllowedMimeTypes`. |
| `globalIds is not supported` | Honua attachments are keyed by integer object IDs only; query with `objectIds`. |
| `401` / `403` on mutation | Attachment writes require write access to the layer, same as feature edits — see [Control access](../secure/access-control.md). |
| `Attachments are not supported for this layer.` | The layer was published without `supportsAttachments`. Check `hasAttachments` in the layer metadata (`FeatureServer/{layerId}?f=json`) and publish the layer with `supportsAttachments: true`. |
| `Relationship {id} not found for layer {layerId}` (error code 404) on `queryRelatedRecords` | The layer has no relationship with that id. List the defined ones with `GET .../FeatureServer/relationships`; an empty `relationships` list means the publisher has not defined any. Do not assume id `0` exists; use an id from that list. |
| Empty `queryRelatedRecords` result | Usually the relationship exists but no related rows match; confirm the related layer is published and holds rows for those object ids. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Edit features](edit-features.md)
- [React to feature changes](react-to-changes.md)
