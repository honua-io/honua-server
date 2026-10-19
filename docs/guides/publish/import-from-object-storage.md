---
type: guide
title: "Import a layer from object storage"
description: "Import an s3:// object with the host's cloud identity, without a presigned URL or a credential in the request."
resource: "honua://capability/import.file"
---
# Import a layer from object storage

You'll import a private object from an allow-listed S3 bucket into a PostGIS table. The server uses the host identity (the Lambda execution role, ECS task role, or EKS Pod Identity / IRSA). The request does not carry an access key, a session token, or an external id.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), and a bucket the host role can read.

## Allow the bucket

Set `Import__ObjectStore__AllowedBuckets__0` to the exact bucket name, or to a name prefix that ends in `*` (for example `customer-imports-*`). An empty list denies every bucket before any cloud call, even when the host role could read the object. The same list is published as the `import.objectStore` capability: with no bucket allow-listed it is `disabled-by-configuration`, and `schemes` is `["s3"]`.

To read through another role, add that role to `Import__ObjectStore__AssumeRoles__0__RoleArn`. If the role trust policy requires `sts:ExternalId`, set `Import__ObjectStore__AssumeRoles__0__ExternalId` on the server. The import request accepts the role ARN and never the external id.

The host identity needs `s3:GetObject` on the object (that action authorizes both HeadObject and GetObject), and `sts:AssumeRole` when a role ARN is used. A KMS-encrypted object also needs `kms:Decrypt` on the key the object uses. Those grants are deployment configuration; this server only reads what the allow-list names.

Optional `Import__ObjectStore__Region`, `Import__ObjectStore__ServiceUrl`, and `Import__ObjectStore__ForcePathStyle` override the client endpoint. Leave them unset in production so the host identity talks to AWS. They are not a place to put customer credentials.

## Limits

`GET /api/v1/admin/import/limits` includes `maxObjectStoreImportBytes` (the `Import__Limits__MaxObjectStoreImportBytes` setting, 50 GiB unless you set it). An object above that size is refused with `import.source_too_large` before any byte is read. The detail names the object size and the limit.

An object above `backgroundJobThresholdBytes`, and every archive (`.zip`, `.kmz`, `.gdb.zip`), returns `202 Accepted` with a `jobId`. The accepting request does not download the object. The worker reads it with the preflight ETag pinned (`If-Match`). A host with no background runner returns `503` and `Background import service not available. File is too large for synchronous import.` before that read.

## Import

Run `POST /api/v1/admin/import/upload-object` with this JSON body:

```json
{
  "sourceUri": "s3://customer-imports/parcels.geojson",
  "tableName": "parcels",
  "targetSrid": 4326,
  "overwriteExisting": true
}
```

Add `assumeRoleArn` only when the object must be read as an allow-listed role. `targetSchema` and `sourceSrid` match the file-import fields.

The same call is the MCP tool `honua_admin_import_upload_object` and the CLI operation `uploadImportFileFromObjectStore` (`honua admin import uploadImportFileFromObjectStore`).

## Failures

| Code | Status | When |
| --- | --- | --- |
| `import.source_unsupported_scheme` | 400 | The URI is not `s3://`. `az://` and `gs://` are not accepted yet. |
| `import.source_forbidden` | 403 | The bucket or role is not allow-listed, or the host identity cannot read the object. |
| `import.source_not_found` | 404 | The bucket or object does not exist. |
| `import.source_changed` | 409 | The object ETag changed after the preflight check. |
| `import.source_too_large` | 413 | The object is larger than `maxObjectStoreImportBytes`. |

A malformed `s3://` URI (credentials, a query, a fragment, or an empty key) is a 400 and the detail does not echo embedded secrets.

Each accepted or refused import with a source URI writes an audit record (`import.object-store`) with the principal, source URI, ETag, byte size, assumed role ARN when one was used, job id, and outcome. The record does not contain credentials, session tokens, or external ids.

## Verify

A synchronous import returns the import result. A background import returns a `jobId`; read it with `GET /api/v1/admin/import/jobs/{jobId}`. Publish the table afterward ([Publish layers](publish-layers.md)).
