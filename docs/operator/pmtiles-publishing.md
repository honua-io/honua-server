# PMTiles Publishing

PMTiles archive generation has two distinct delivery modes. This guide documents the
durable publish workflow introduced in #845 and contrasts it with the existing
temporary admin archive download.

## Modes At A Glance

| Concern | `operation: "archive"` | `operation: "publish"` |
|---------|-----------------------|------------------------|
| Object lifetime | 24-hour TTL | Permanent (no TTL) |
| Object key | Random per job (`pmtiles/{guid}.pmtiles`) | Deterministic (`{prefix}/pmtiles/{serviceId}/{layerId}/{tms}.pmtiles`) |
| Result fields | `archiveFileId`, `downloadUrl`, `archiveSizeBytes` | `publishedArtifact` (full descriptor) |
| Audience | Admin operators downloading a PMTiles file | Browser-based MapLibre/PMTiles clients |
| Re-run behavior | Generates a new file each run | Overwrites the existing artifact |

`operation: "archive"` continues to behave exactly as before. New deployments wanting a
durable browser-readable artifact should use `operation: "publish"`.

## Publish API

```bash
curl -X POST https://<host>/api/v1/admin/tile-operations/jobs \
  -H "Content-Type: application/json" \
  -d '{
    "operation": "publish",
    "serviceId": "world",
    "layerId": 42,
    "minZoom": 0,
    "maxZoom": 12,
    "bbox": [-180, -85.0511, 180, 85.0511]
  }'
```

`layerId` is required. `serviceId`, `bbox`, `minZoom`, `maxZoom`, `maxTiles`, and
`tileMatrixSetId` follow the same semantics as the `archive` operation.

Polling `GET /api/v1/admin/tile-operations/jobs/{jobId}` returns
`TileOperationProgress` with a populated `publishedArtifact` once the job completes:

```json
{
  "jobId": "...",
  "operation": "publish",
  "status": 2,
  "publishedArtifact": {
    "artifactId": "pmtiles/world/42/WebMercatorQuad.pmtiles",
    "storageProvider": "AwsS3",
    "bucket": "honua-tiles",
    "objectKey": "pmtiles/world/42/WebMercatorQuad.pmtiles",
    "contentType": "application/vnd.pmtiles",
    "sizeBytes": 12483211,
    "urlStrategy": "SignedUrl",
    "accessUrl": "https://honua-tiles.s3.amazonaws.com/pmtiles/world/42/WebMercatorQuad.pmtiles?X-Amz-Signature=...",
    "accessUrlExpiresAt": "2026-05-05T00:00:00Z",
    "publishedAt": "2026-04-28T00:00:00Z",
    "minZoom": 0,
    "maxZoom": 12,
    "bounds": [-180.0, -85.0511, 180.0, 85.0511],
    "layerId": 42,
    "serviceId": "world",
    "tileMatrixSetId": "WebMercatorQuad"
  }
}
```

The descriptor carries all fields a MapLibre/PMTiles client needs to construct a
`pmtiles://{accessUrl}` source: bounds, minzoom, maxzoom, content type, and access URL.

## URL Strategies

`FileStorage:PMTilesPublish:UrlStrategy` selects how clients reach the artifact.

### `SignedUrl` (default)

Server returns a presigned/SAS URL with a finite lifetime:

```jsonc
"FileStorage": {
  "PMTilesPublish": {
    "UrlStrategy": "SignedUrl",
    "SignedUrlLifetime": "7.00:00:00"
  }
}
```

* AWS S3 SigV4 IAM credentials cap presign lifetime at 7 days.
* Azure SAS tokens accept longer lifetimes via account-level keys.
* Clients should refresh `accessUrl` before `accessUrlExpiresAt` by re-querying
  the job status endpoint.

This is the safe default for private buckets — credentials never reach the browser.

### `PublicUrl`

Suitable when the bucket/container is publicly readable and a stable URL is
acceptable. Configure the public base URL:

```jsonc
"FileStorage": {
  "PMTilesPublish": {
    "UrlStrategy": "PublicUrl",
    "PublicBucketBaseUrl": "https://cdn.example.com/honua-tiles"
  }
}
```

`accessUrl` is computed as `{PublicBucketBaseUrl}/{objectKey}`.
`accessUrlExpiresAt` is `null` because the URL never expires.

### `RangeProxy`

For private deployments that cannot expose object storage credentials or public
URLs to the browser. Range requests are routed through the server:

```jsonc
"FileStorage": {
  "PMTilesPublish": {
    "UrlStrategy": "RangeProxy"
  }
}
```

`accessUrl` is `/api/v1/tiles/pmtiles/{artifactId}`. The proxy endpoint:

* responds to `GET` and `HEAD`. `Accept-Ranges: bytes`, `ETag`, and
  `Last-Modified` are emitted on every response (including `HEAD`)
* `HEAD` returns `200 OK` with `Content-Length` and no body, so MapLibre /
  pmtiles.js probes that pre-flight the artifact succeed without transferring
  the archive
* honors RFC 7233 `Range: bytes=offset-end` `GET` requests with
  `206 Partial Content` and `Content-Range`
* returns `416 Range Not Satisfiable` (with `Content-Range: bytes */<size>`)
  for ranges past the artifact end
* a `GET` without a `Range` header returns the full archive as `200 OK`
* only serves objects that are tagged as durable PMTiles publish artifacts —
  the resolver requires `Content-Type: application/vnd.pmtiles`, the
  `operation=publish` storage metadata tag set by the publish workflow, and
  (when `FileStorage:PMTilesPublish:KeyPrefix` is configured) a key under the
  configured publish prefix. Other CloudFile objects in the same bucket are
  returned as `404 Not Found` even if the artifact ID is guessed.
* is publicly accessible — MapLibre browser clients have no admin credentials.
  Restrict via deployment-level rate limiting / WAF if necessary.

The proxy adds one server-side hop per range read. PMTiles clients typically read
small ranges (~16 KiB) per tile, so latency overhead is dominated by the cloud
read. Direct `SignedUrl` or `PublicUrl` access avoids the hop entirely.

## Object Storage CORS Requirements

`PublicUrl` and `SignedUrl` strategies stream bytes from object storage directly
to the browser. The bucket/container CORS policy must allow:

| Setting | Value |
|---------|-------|
| Methods | `GET`, `HEAD` |
| Allowed request headers | `Range`, `If-Range`, `If-None-Match`, `If-Modified-Since` |
| Exposed response headers | `Accept-Ranges`, `Content-Range`, `Content-Length`, `ETag`, `Last-Modified` |
| Allowed origins | Application domains that load the MapLibre map |

The Honua server CORS policy already exposes those response headers for the
`RangeProxy` strategy.

### AWS S3 CORS sample

```json
[
  {
    "AllowedMethods": ["GET", "HEAD"],
    "AllowedOrigins": ["https://app.example.com"],
    "AllowedHeaders": ["Range", "If-Range", "If-None-Match", "If-Modified-Since"],
    "ExposeHeaders": ["Accept-Ranges", "Content-Range", "Content-Length", "ETag", "Last-Modified"],
    "MaxAgeSeconds": 600
  }
]
```

### Azure Blob CORS sample

```xml
<Cors>
  <CorsRule>
    <AllowedOrigins>https://app.example.com</AllowedOrigins>
    <AllowedMethods>GET,HEAD</AllowedMethods>
    <AllowedHeaders>Range,If-Range,If-None-Match,If-Modified-Since</AllowedHeaders>
    <ExposedHeaders>Accept-Ranges,Content-Range,Content-Length,ETag,Last-Modified</ExposedHeaders>
    <MaxAgeInSeconds>600</MaxAgeInSeconds>
  </CorsRule>
</Cors>
```

## Failure Semantics

The publish path validates strategy-specific configuration **before** uploading
the durable artifact, and rolls back any object that was just written if access
URL generation subsequently fails:

* `PublicUrl` without `PublicBucketBaseUrl` configured fails the job with a
  pre-flight error and never uploads a file.
* `SignedUrl` strategy: if the storage provider returns no presigned URL (e.g.,
  IAM/SAS misconfiguration), the just-uploaded artifact is deleted before the
  job is marked failed, so a later re-run does not race against an orphan.
* `RangeProxy` does not call out to the storage provider for URL generation, so
  no rollback path is required.

If the rollback delete itself fails the job is still marked failed and a
warning is emitted with the artifact ID and job ID so operators can reconcile
the leftover object out-of-band:

* `PublishOrphanCleanupFailed` (`EventId 9212`) when `DeleteAsync` throws.
* `PublishOrphanCleanupReturnedFalse` (`EventId 9213`) when the provider reports
  a soft failure by returning `false` (S3 / Azure Blob / LocalFileStorage all
  catch transport-level failures and surface them this way, so the bool result
  is the only signal that the orphan still exists).

Whatever the failure mode of the access URL step, the job-status payload always
returns the stable client-safe message `Publish access URL generation failed.`
The provider-specific exception (account, IAM principal, signed-URL fragment,
SDK type name, …) is captured only in the structured log entry
`PublishAccessUrlFailed` (`EventId 9214`) so admin API clients never see
provider internals leak through `errorMessage`.

## Restart Recovery

Queued and in-flight `publish` jobs are tracked under
`OperationType.PMTilesPublish` in the universal operations progress store and
are restored alongside `TileCache` and `PMTilesArchive` jobs when the tile
operations service starts. A publish job that was queued before a restart
resumes at the next worker tick without operator intervention; an in-flight
upload that lost its host fails with the standard cancellation semantics and
must be re-issued.

## Object Keys And Re-publishing

Publish uses a deterministic key derived from the request:

```
{provider-key-prefix?}/{publish-key-prefix}/{serviceId or "_"}/{layerId}/{tileMatrixSetId}.pmtiles
```

Example with S3 `KeyPrefix=tenants/acme` and publish prefix `pmtiles`:

```
tenants/acme/pmtiles/world/42/WebMercatorQuad.pmtiles
```

Re-publishing the same `(serviceId, layerId, tileMatrixSetId)` overwrites the
existing object. Object stores guarantee atomic PUT, but in-flight readers may
observe the previous version until they retry. Plan re-publish during low-traffic
windows or move to a versioned URL scheme as a follow-up.

## MapLibre / PMTiles Client Hint

The descriptor exposes everything required for a browser client to register a
PMTiles source without ad hoc configuration. From SDK-JS / pmtiles.js:

```js
const protocol = new pmtiles.Protocol();
maplibregl.addProtocol("pmtiles", protocol.tile);

const p = new pmtiles.PMTiles(descriptor.accessUrl);
protocol.add(p);

map.addSource("layer-42", {
  type: "vector",
  url: `pmtiles://${descriptor.accessUrl}`,
  bounds: descriptor.bounds,
  minzoom: descriptor.minZoom,
  maxzoom: descriptor.maxZoom
});
```

Server-side metadata for downstream packaging additionally exposes
`SourceProtocol.PMTiles` so map packages can express PMTiles as a first-class
source binding.
