# Publish layers

Register a database connection and publish its spatial tables through Honua's supported control-plane SDK. Each published layer is immediately available through every enabled protocol.

**Prerequisites:** a running server, an admin API key, a reachable database, and Python with
[`honua-admin` installed from source](https://github.com/honua-io/honua-sdk-python#install)
until the package's first PyPI release.

## Create a secure connection

Prefer a server-side secret reference in production. The plaintext password below is only for the local development stack.

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

The table-discovery and pre-publish validation operations do not yet have high-level SDK methods. Use the [admin API explorer](../../reference/openapi-and-explorer.md) or generate a client from [`admin-api.json`](../../developer/api-specs/admin-api.json) for:

- `GET /api/v1/admin/connections/{id}/tables`
- `POST /api/v1/admin/connections/{id}/tables/validate`

Validation reports missing primary keys, unsupported geometry, and other publish-blocking problems before catalog mutation.

## Publish a layer

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

The result includes the numeric layer ID and owning service name. Optional request fields include a description, geometry type, primary key, attribute allowlist, service name, and enabled state.

Manage published layers through `list_layers`, `set_layer_enabled`, and `set_service_layers_enabled`:

```python
with HonuaAdminClient("http://localhost:8080", api_key="your-admin-api-key") as admin:
    for layer in admin.list_layers("primary-db"):
        print(layer)
    admin.set_layer_enabled("primary-db", layer_id=0, enabled=True)
```

Extent refresh and several bulk catalog operations do not yet have SDK wrappers; use the generated admin client for those endpoints.

## Verify the data plane

```bash
export HONUA_BASE_URL=http://localhost:8080
export HONUA_API_KEY=your-admin-api-key

honua services
honua layers default
honua query default/0 --limit 5
honua query default/0 --count
```

Use the actual service and layer ID from the publish result. The same catalog entry also drives OGC API Features, GeoServices, OData, vector tiles, and the other enabled protocol adapters.

## Troubleshoot

- **401 or 403** — verify the admin key and required connection/layer permissions.
- **Connection test fails** — check host reachability, TLS mode, database name, and credential secret configuration.
- **Table is absent from discovery** — verify the connection user can read the schema and geometry metadata.
- **Publish validation fails** — add a stable primary key and use a supported geometry/SRID before retrying.
- **The layer publishes but queries fail** — confirm it is enabled and the service access policy permits the caller.

More help: [deployment troubleshooting](../deploy/troubleshooting.md).
