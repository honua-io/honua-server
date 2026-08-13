# Connections and layers

Reference for the connection registry, table discovery, layer publishing, and service/layer settings endpoints. A connection stores encrypted database credentials; layers are published from tables on a connection and served through every enabled protocol.

All endpoints require admin authentication — see [Authentication](../../guides/secure/authentication.md).

## Connection registry

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/connections` | List connections (credential material is never returned) |
| POST | `/api/v1/admin/connections` | Create a connection |
| GET | `/api/v1/admin/connections/{id}` | Get connection details |
| PUT | `/api/v1/admin/connections/{id}` | Update a connection |
| DELETE | `/api/v1/admin/connections/{id}` | Delete a connection |
| POST | `/api/v1/admin/connections/test` | Test a draft connection before saving |
| POST | `/api/v1/admin/connections/{id}/test` | Test health of a saved connection |
| POST | `/api/v1/admin/connections/encryption/validate` | Validate encryption service status |
| POST | `/api/v1/admin/connections/encryption/rotate-key` | Trigger credential key rotation (may be rejected by policy) |

Validation rules: supply either `password` or `secretReference` (+ `secretType`), not both. `sslMode` accepts `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`; `sslMode=Disable` is rejected when `sslRequired=true`.

In the authorized [API explorer](../openapi-and-explorer.md), run `POST /api/v1/admin/connections` with this body:

```json
{
  "name": "primary-db",
  "host": "db.internal",
  "port": 5432,
  "databaseName": "honua",
  "username": "postgres",
  "password": "secure-password",
  "sslMode": "Require"
}
```

## Table discovery

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/connections/{id}/tables` | Discover PostGIS tables on a connection (`id` is GUID or name) |
| POST | `/api/v1/admin/connections/{id}/tables/validate` | Validate a table before publishing |
| GET | `/api/v1/admin/connections/tables` | Discover tables across all connections |

Run `GET /api/v1/admin/connections/primary-db/tables` in the explorer.

## Layer publishing

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/connections/{id}/layers` | List published layers for a connection |
| POST | `/api/v1/admin/connections/{id}/layers` | Publish a layer from a table |
| PUT | `/api/v1/admin/connections/{id}/layers/{layerId}/enabled` | Enable or disable one published layer |
| PUT | `/api/v1/admin/connections/{id}/layers/enabled` | Enable or disable all layers on a connection (bulk) |
| POST | `/api/v1/admin/connections/{id}/layers/extents/refresh` | Recompute published layer extents from current data |

Run `POST /api/v1/admin/connections/primary-db/layers` with `{"schema":"public","table":"parcels","layerName":"city-parcels","geometryColumn":"geom","srid":4326}`.

The publish request accepts these optional source-governance fields:

| Field | Limit and validation | Canonical/protocol projection |
|---|---|---|
| `license` | 256 characters; SPDX expression syntax or literal `proprietary` | Metadata v2 license; GeoServices license metadata |
| `attribution` | 512 characters; no control characters | GeoServices `copyrightText`; OGC collection `attribution` |
| `publisher` | 256 characters; no control characters | Metadata v2 publisher; additive GeoServices publisher metadata |
| `licenseUrl` | 2,048 characters; absolute HTTP(S), no embedded credentials | Optional override for the Metadata v2 and protocol link with `rel=license` |
| `sourceUrl` | 2,048 characters; absolute HTTP(S), no embedded credentials | Metadata v2 and protocol link with `rel=describedby` |

All fields are optional and absent by default. Honua does not infer license rights, attribution, or publisher identity. When `license` is one standalone SPDX identifier and `licenseUrl` is omitted, Honua derives its canonical `https://spdx.org/licenses/{identifier}.html` documentation link. Explicit `licenseUrl` values override the derived URL; compound SPDX expressions and `proprietary` do not produce a derived link.

## Service and layer settings

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/services` | List services |
| GET | `/api/v1/admin/services/{serviceName}/settings` | Get protocol and MapServer settings for a service |
| PUT | `/api/v1/admin/services/{serviceName}/protocols` | Update enabled protocols |
| PUT | `/api/v1/admin/services/{serviceName}/mapserver` | Update MapServer defaults and limits |
| PUT | `/api/v1/admin/services/{serviceName}/access-policy` | Update service access policy (read/write roles, anonymous access) |
| PUT | `/api/v1/admin/services/{serviceName}/timeinfo` | Update service-level temporal metadata |
| PUT | `/api/v1/admin/services/{serviceName}/layers/{layerId}/metadata` | Patch layer-level governance, access policy, time info, and raster mosaic defaults |

Layer metadata accepts `rasterMosaic.mergeStrategy` values `newest`, `oldest`, `average`, `max`, and `min` (case-insensitive). An empty string clears the layer default; a missing or `null` field preserves the existing value; unknown values return `400`.

The layer metadata update accepts the same `license`, `attribution`, `publisher`, `licenseUrl`, and `sourceUrl` fields and limits as publish. It is a patch: an omitted or `null` governance field preserves its current value, while an empty string clears that field (or removes the corresponding link). A license-only patch derives or refreshes the canonical link for a standalone SPDX identifier unless an explicit custom license URL already exists. Malformed SPDX expressions, over-limit text, non-HTTP(S)/relative URLs, embedded URL credentials, and control characters return `400`; rejected values are not copied into canonical metadata.

Run `PUT /api/v1/admin/services/city/access-policy` with `{"readRole":"viewer","writeRole":"editor","allowAnonymousRead":false}`.

## Related guides

- [Serve existing databases](../../guides/publish/serve-existing-databases.md)
- [Publish layers](../../guides/publish/publish-layers.md)
- [Access control](../../guides/secure/access-control.md)
