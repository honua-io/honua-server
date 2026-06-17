# Get started with the Python SDK

Install the Honua Python SDK, construct a client, authenticate with an API key, and make your first feature query.

**Prerequisites:** A running Honua server ([quickstart](../../get-started/quickstart.md)) with at least one published layer ([publish layers](../../guides/publish/publish-layers.md)), Python 3.11 or newer, and an API key (see [Authenticate clients](../../guides/secure/authentication.md) — the SDK landing page shows how to [mint a scoped key](../README.md#authentication)).

The data-plane SDK ships as `honua-sdk` on PyPI and is imported as `honua_sdk`. The current release is **0.1.4** and requires **Python ≥ 3.11**. A companion control-plane package, `honua-admin` (imported as `honua_admin`, class `HonuaAdminClient`), wraps `/api/v1/admin/*`.

## Steps

### 1. Install the package

```bash
pip install honua-sdk
```

Optional extras add helpers:

```bash
pip install "honua-sdk[geopandas]"   # GeoDataFrame helpers
pip install "honua-sdk[grpc]"        # streaming gRPC client
```

### 2. Construct a client

`HonuaClient` takes the server base URL and credentials. The API key is sent as the `X-API-Key` header. Use it as a context manager so the underlying HTTP connection is closed for you:

```python
import os
from honua_sdk import HonuaClient

with HonuaClient(
    "http://localhost:8080",
    api_key=os.environ["HONUA_API_KEY"],
) as client:
    services = client.list_services()
    print(services)
```

For OIDC instead of an API key, pass `bearer_token=...` or an `auth_provider` callable. An `AsyncHonuaClient` with the same constructor is available for `async`/`await` code.

### 3. Make your first call

Query a published layer through the protocol-neutral `Source` / `Query` API. This works against any published layer regardless of which protocol serves it — point the `SourceLocator` at one of your own services:

```python
from honua_sdk import HonuaClient, Query, SourceDescriptor, SourceLocator

with HonuaClient("http://localhost:8080", api_key=os.environ["HONUA_API_KEY"]) as client:
    source = client.source(
        SourceDescriptor(
            id="default",
            protocol="geoservices-feature-service",
            locator=SourceLocator(service_id="default", layer_id=0),
        )
    )
    result = source.query(Query(where="1=1", out_fields=["*"]))

    print(f"Found {len(result.features)} features")
    for feature in result.features[:5]:
        print(feature.properties)
```

## Verify

```python
print(f"Found {len(result.features)} features")
```

A wrong or missing key raises an authentication error from the client — confirm `HONUA_API_KEY` is set and accepted (`curl -H "X-API-Key: $HONUA_API_KEY" http://localhost:8080/api/v1/admin/version`).

## Available surfaces

`HonuaClient` exposes both the protocol-neutral `source()` facade and protocol-specific clients:

| Accessor | Returns | Use it for |
|---|---|---|
| `client.source(SourceDescriptor(...))` | a `Source` | Protocol-neutral `Query` / `Result` over any layer |
| `client.feature_server(service_id)` | FeatureServer client | ArcGIS-style FeatureServer queries |
| `client.ogc_features()` | OGC Features client | OGC API Features collections and items |
| `client.stac()` | STAC client | STAC collections and item search |
| `HonuaAdminClient(base_url, api_key=...)` (from `honua_admin`) | admin client | Control plane — compatibility, connections, imports |

## Troubleshoot

| Symptom | Fix |
|---|---|
| Authentication error / 401 | `api_key` unset or wrong; verify with `curl -H "X-API-Key: $KEY" .../api/v1/admin/version`. |
| `ModuleNotFoundError: honua_sdk` | The import name uses an underscore (`honua_sdk`), the package name a hyphen (`honua-sdk`). |
| Empty `result.features` | The `where` filtered everything out, or the layer is empty; try `Query(where="1=1")`. |

More general failures: [Troubleshooting](../../guides/deploy/troubleshooting.md).

## Next steps

- [Python common tasks](common-tasks.md) — query a FeatureServer layer and run a STAC search
- [honua-sdk-python on GitHub](https://github.com/honua-io/honua-sdk-python) — examples and the admin client
- [Query features over HTTP](../../guides/query-analyze/query-features.md) — the protocol surfaces the SDK wraps
- [SDK overview](../README.md)
