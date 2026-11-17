---
type: guide
title: "Publish layers"
description: "Register a database connection and publish its spatial tables through Honua's supported control-plane SDK."
---
# Publish layers

See the [Honua vocabulary](../../concepts/domain/README.md) for the meanings of Connection, Dataset, Layer, Service, Folder, and Environment.

Register a database connection and publish its spatial tables through Honua's supported control-plane SDK. Each published layer is immediately available through every enabled protocol.

**Prerequisites:** a running server, an admin API key, a reachable database, and Python with
`honua-admin` installed (`pip install honua-admin`).

## Create a secure connection

Prefer a server-side secret reference in production. Replace the connection settings below with values reachable from the Honua server. In a container, `localhost` refers to that container; use the database service hostname for a separate database container. The plaintext password below is only for local development.

`ssl_mode="Require"` needs a PostgreSQL server configured for TLS. For a local development database without TLS, use `ssl_mode="Disable"`; keep TLS enabled for production connections.

```python
from honua_admin import CreateSecureConnectionRequest, HonuaAdminClient

with HonuaAdminClient("http://localhost:8080", api_key="your-admin-api-key") as admin:
    connection = admin.create_connection(CreateSecureConnectionRequest(
        name="primary-db",
        host="localhost",
        port=5432,
        database_name="honua",
        username="postgres",
        password="development-only-password",
        ssl_mode="Require",
    ))
    print(connection)
```

Use `admin.list_connections()` and `admin.get_connection(id)` to inspect existing connections. The SDK never returns stored secrets.

## Inspect and validate tables

`admin.discover_tables(connection.connection_id)` lists the connection's spatial tables with the
schema, geometry column and SRID that `publish_layer` needs. Discovery includes the default
operational schema (`honua_data` unless `Database:DefaultOperationalSchema` is set), `public`,
schemas listed in `Database:OperationalSchemas`, and the active request's schema context.
Reserved metadata schemas are excluded. The connection user must be able to read the source tables.

Pre-publish validation
(`POST /api/v1/admin/connections/{id}/tables/validate`) does not yet have a high-level SDK
method; call it through the admin client's HTTP transport or a client generated from
[`admin-api.json`](../../developer/api-specs/admin-api.json). Validation reports missing primary keys, unsupported geometry, and other publish-blocking problems before catalog mutation.

## Publish a layer

Use this SDK example for a small table that completes synchronously. For tables
above the asynchronous publication threshold, use the generated admin client as
described under [Large-table publication](#large-table-publication).

```python
from honua_admin import HonuaAdminClient, PublishLayerRequest

with HonuaAdminClient("http://localhost:8080", api_key="your-admin-api-key") as admin:
    layer = admin.publish_layer("primary-db", PublishLayerRequest(
        schema="public",
        table="parcels",
        layer_name="city-parcels",
        geometry_column="geom",
        srid=4326,
        service_name="default",
    ))
    print(layer)
```

The result exposes the numeric layer ID as `layer.storage_layer_id` and the owning service as `layer.service_name`. Optional SDK request fields include a description, geometry type, primary key, attribute allowlist, service name, and enabled state. Repeating this publication for the same source table returns a conflict; inspect the existing layer before retrying.

The JSON API also accepts source governance fields. The published `honua-admin` 0.3.2 `PublishLayerRequest` does not expose these fields; use a client generated from [`admin-api.json`](../../developer/api-specs/admin-api.json) to submit the following payload.

For public data, submit only rights and provenance supplied by the data owner. Honua does not infer a license, attribution, publisher, or documentation URL:

```json
{
  "schema": "public",
  "table": "boundaries",
  "layerName": "public-boundaries",
  "geometryColumn": "geom",
  "srid": 4326,
  "license": "CC0-1.0",
  "attribution": "Example boundary data contributors",
  "publisher": "Example Data Office",
  "licenseUrl": "https://data.example/licenses/cc0-1.0",
  "sourceUrl": "https://data.example/datasets/boundaries"
}
```

`license` is limited to 256 characters and must be a syntactically valid SPDX expression (or the literal `proprietary`). `attribution` is limited to 512 characters and `publisher` to 256. `licenseUrl` and `sourceUrl` are limited to 2,048 characters and must be absolute HTTP(S) URLs without embedded credentials. Control characters are rejected. Empty values publish as absent; omitted governance fields preserve legacy behavior.

Attribution is served as GeoServices `copyrightText` and OGC collection `attribution`. GeoServices also returns the license identifier, publisher, and additive `links`; OGC collections return `rel=license` and `rel=describedby` links. For a standalone SPDX identifier such as `MIT`, Honua derives the canonical `https://spdx.org/licenses/MIT.html` license link when `licenseUrl` is omitted. An explicit `licenseUrl` overrides that derived URL. Compound SPDX expressions and `proprietary` do not have one canonical SPDX document, so submit `licenseUrl` explicitly when those values need a license link.

Manage published layers through `list_layers`, `set_layer_enabled`, and `set_service_layers_enabled`:

```python
with HonuaAdminClient("http://localhost:8080", api_key="your-admin-api-key") as admin:
    for published_layer in admin.list_layers("primary-db", service_name=layer.service_name):
        print(published_layer)
    admin.set_layer_enabled(
        "primary-db",
        layer_id=layer.storage_layer_id,
        service_name=layer.service_name,
        enabled=True,
    )
```

Extent refresh and several bulk catalog operations do not yet have SDK wrappers; use the generated admin client for those endpoints.

## Verify the data plane

```bash
# `honua` here is the JavaScript SDK's CLI - the one with `query`.
# npx -p @honua/sdk-js honua …  works without installing anything.
export HONUA_BASE_URL=http://localhost:8080
export HONUA_API_KEY=your-admin-api-key

honua services
honua layers default
LAYER_ID=1  # placeholder; see below
honua query "default/$LAYER_ID" --limit 5
honua query "default/$LAYER_ID" --count
```

Use the actual service and layer ID from the publish result. The same catalog entry also drives OGC API Features, GeoServices, OData, vector tiles, and the other enabled protocol adapters.

## Large-table publication

The published `honua-admin` 0.3.2 `publish_layer()` method expects a completed
layer and cannot decode a background job handle. Use a client generated from
[`admin-api.json`](../../developer/api-specs/admin-api.json) that preserves the
HTTP 202 response for these publications and subsequent polling.

Tables above `LayerPublishing:AsyncPublishRowThreshold` (10,000 rows by default)
return HTTP 202 with a job handle instead of holding the request open for the
snapshot copy. The response's `data` contains `jobId`, `statusUrl`, `rowsTotal`,
and `status`. Follow that same-origin relative status URL with the accepting
tenant's admin credentials. Each poll returns `jobId`, `status`,
`rowsMaterialized`, `rowsTotal`, `storageLayerId`, and `errorMessage` in `data`.
`Queued` and `Processing` are pending states; `Completed`, `Failed`, and
`Cancelled` are terminal. Use the completed `storageLayerId` for subsequent queries.
Do not treat acceptance as publication completion.

Accepted work uses its own publication budget and survives a disconnected
request. Small tables retain the HTTP 201 layer response. Lambda needs a
configured remote durable runner for large tables; without one it returns a
typed 503 rather than accepting work that cannot finish. See
[AWS Batch worker configuration](../deploy/geoprocessing-aws-batch.md#large-layer-publishes)
for the API and worker rollout prerequisites.

Publication and refresh copy every page from one repeatable-read database
snapshot. Concurrent source changes do not add duplicates or mix newer rows into
that copy; refresh again when the later source changes should be included.

Polling can recover from an expired progress document using the durable job,
subject to the same tenant and connection checks. A running recovered job reports
`rowsMaterialized: 0` as a conservative lower bound when its live page progress is
unavailable, and `rowsTotal` retains the count at acceptance. Completion replaces
both with the actual committed counts. A legacy layer-only receipt cannot recover
lost completion counts and still returns not found if its progress document has
expired; the server does not invent a completed count.

Admission performs an exact source count with a 30-second command limit before
returning 202. A slow count can fail before work is accepted; the detached
publication budget begins only after admission. Increasing that count limit alone
would not solve a gateway deadline. The bounded release qualification must prove
its actual source count and 330,000-row publish journey.

## Troubleshoot

- **401 or 403** — verify the admin key and required connection/layer permissions.
- **Connection test fails** — check host reachability, TLS mode, database name, and credential secret configuration.
- **Table is absent from discovery** — check the operational-schema settings above and verify the connection user can read the schema and geometry metadata.
- **Publish returns 409** — check whether the source table already has a published layer; changing only the requested layer name does not create another publication.
- **Publish validation fails** — add a stable primary key and use a supported geometry/SRID before retrying.
- **The layer publishes but queries fail** — confirm it is enabled and the service access policy permits the caller.

More help: [deployment troubleshooting](../deploy/troubleshooting.md).
