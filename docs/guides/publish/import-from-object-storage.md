---
type: guide
title: "Import a layer from object storage"
description: "Connect an S3 bucket prefix through a role in your account and import an object from it, without a stored access key."
resource: "honua://capability/import.file"
---
# Import a layer from object storage

See the [Honua vocabulary](../../concepts/domain/README.md) for the meanings of Connection, Dataset, Layer, Service, Folder, and Environment.

You'll connect an S3 bucket prefix as a Connection and import an object from it into a PostGIS table. Honua never stores an access key for your bucket and never edits your bucket policy. It reads through a role in your account that trusts this installation with a per-connection external id.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), and permission to create an IAM role in the account that owns the bucket.

## Connect the bucket

1. Run `GET /api/v1/admin/connections/object-storage/setup?bucket=customer-imports&prefix=parcels/`. The response has `externalId`, `roleNamePrefix` (`honua-connection-`), and `quickCreateUrl` when the operator set `Connections__ObjectStorage__QuickCreateTemplateUrl`. The external id works once and expires after 24 hours.
2. Create a role named `honua-connection-*` in your account (the quick-create link does this). Its trust policy names this installation's principal and requires `sts:ExternalId` equal to the external id. Grant it `s3:GetObject` on `arn:aws:s3:::customer-imports/parcels/*`, `s3:ListBucket` on the bucket, and `s3:GetBucketLocation`.
3. Run `POST /api/v1/admin/connections` with `kind`, `provider`, `name`, `bucket`, `prefix`, `region`, `roleArn` and the `externalId` from step 1:

```json
{
  "kind": "object-storage",
  "provider": "aws-s3",
  "name": "Customer imports",
  "bucket": "customer-imports",
  "prefix": "parcels/",
  "region": "us-west-2",
  "roleArn": "arn:aws:iam::123456789012:role/honua-connection-imports",
  "externalId": "<externalId from setup>"
}
```

The bucket, prefix and external id cannot change after create; create a new connection instead. `POST /api/v1/admin/connections/{id}/test` checks the bucket region and lists one key. `GET /api/v1/admin/connections/{id}/objects?prefix=parcels/2026/` lists one folder level, up to 200 entries, with each object's kind (`cog`, `geotiff`, `pmtiles`, `geoparquet` or `other`) read from its first bytes.

Every read assumes the role for 15 minutes with an inline session policy that allows only `s3:GetObject` under the prefix, `s3:ListBucket` for the prefix, `s3:GetBucketLocation` and `s3:GetBucketRequestPayment`. A role broader than the prefix is still read-only on the prefix. Requester-pays buckets are refused.

The installation's own data bucket (`FileStorage__AwsS3__BucketName` under `FileStorage__AwsS3__KeyPrefix`), and its public PMTiles prefix when `PMTilesPublish` uses public URLs, appear in the list as built-in connections. They are read with the server's own identity, cannot be changed or deleted, and are the only installation storage shown.

Optional `Import__ObjectStore__Region`, `Import__ObjectStore__ServiceUrl`, and `Import__ObjectStore__ForcePathStyle` point the S3 and STS clients at an emulator. The server refuses `ServiceUrl` when the environment is Production.

## Limits

`GET /api/v1/admin/import/limits` includes `maxObjectStoreImportBytes` (the `Import__Limits__MaxObjectStoreImportBytes` setting, 50 GiB unless you set it). An object above that size is refused with `import.source_too_large` before any byte is read. The detail names the object size and the limit.

An object above `backgroundJobThresholdBytes`, and every archive (`.zip`, `.kmz`, `.gdb.zip`), returns `202 Accepted` with a `jobId`. The accepting request does not download the object. The worker reads it with the preflight ETag pinned (`If-Match`). A host with no background runner returns `503` and `Background import service not available. File is too large for synchronous import.` before that read.

## Import

Run `POST /api/v1/admin/import/upload-object` with this JSON body:

```json
{
  "connectionId": "3f2c6d1e-6a8b-4f4e-9d61-0c3b0f1f2a10",
  "key": "parcels/parcels.geojson",
  "tableName": "parcels",
  "targetSrid": 4326,
  "overwriteExisting": true
}
```

The key must lie under the connection prefix. `sourceUri` and `assumeRoleArn` are refused. `targetSchema` and `sourceSrid` match the file-import fields. A background job reads the connection again before it downloads, so deleting the connection stops queued imports from it.

The same call is the MCP tool `honua_admin_import_upload_object` and the CLI operation `uploadImportFileFromObjectStore` (`honua admin import uploadImportFileFromObjectStore`).

## Failures

| Code | Status | When |
| --- | --- | --- |
| `import.source_forbidden` | 403 | The key is not under the connection prefix, the role does not trust this installation, or the role cannot read the object. |
| `import.source_requester_pays` | 403 | The bucket has requester pays turned on. |
| `import.source_not_found` | 404 | The bucket or object does not exist. A missing connection is also a 404. |
| `import.source_changed` | 409 | The object ETag changed after the preflight check. |
| `import.source_too_large` | 413 | The object is larger than `maxObjectStoreImportBytes`. |

Each accepted or refused import writes an audit record (`import.object-store`) with the principal, source URI, ETag, byte size, connection id, job id, and outcome. The record does not contain credentials, session tokens, or external ids.

An agent that creates, changes or deletes a connection files a proposal for operator approval.

## Verify

A synchronous import returns the import result. A background import returns a `jobId`; read it with `GET /api/v1/admin/import/jobs/{jobId}`. Publish the table afterward ([Publish layers](publish-layers.md)).
