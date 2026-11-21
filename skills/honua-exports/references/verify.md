# Verify export readback

Call a file Export done only after readback confirms a `ready` Version and reachable collection metadata.

1. Read `GET /api/v1/admin/exports/{exportId}` (`admin.exports.get`) and record:
   - `status` (must be `ready`)
   - `currentVersion`
   - `collectionUrl`
   - `publicUrl` or `notPublicReason`
   - `versions` entry matching `currentVersion` (`featureCount` and `artifacts` byte sizes)
2. Verify the signed-in pointer:
   - Send an authenticated `HEAD` or `GET` to `collectionUrl` (`/api/v1/exports/{exportId}/current.json`).
   - Confirm `307 Temporary Redirect` to the immutable Version's `collection.json` and `Cache-Control: private, no-store`.
   - Follow the redirect and confirm `collection.json` lists relative `geoparquet` (role `data`) and `pmtiles` (role `visual`) assets with `file:size` and `file:checksum`. Resolve asset links against the redirected immutable URL, never against `current.json`.
   - If the read returns `403`, report that the Layer's current row filter or field mask prevents immutable archive reads.
3. Verify public CDN delivery when `publicUrl` is present:
   - Confirm `publicUrl` returns `200` with `Cache-Control: public, max-age=31536000, immutable` (or the configured immutable policy) and that the referenced GeoParquet and PMTiles URLs answer `HEAD` / byte-range `GET` (`206`).
   - Confirm the root catalog (`{BaseUrl}/data/catalog.json`) lists the Export.
4. Report exact IDs, version, feature count, and URLs returned by the server. If `publicUrl` is absent, report the exact `notPublicReason` and state clearly that the Export is signed-in only.
