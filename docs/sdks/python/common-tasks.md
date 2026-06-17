# Python SDK: common tasks

Two of the most common reads with the Honua Python SDK: querying a FeatureServer layer and searching a STAC catalog. Both assume a constructed client — see [Get started with the Python SDK](getting-started.md). Examples use the synchronous `HonuaClient`; `AsyncHonuaClient` exposes the same calls with `await`.

## Query a FeatureServer layer

Honua serves layers over the ArcGIS-compatible [GeoServices REST](../../reference/protocols/geoservices-rest.md) FeatureServer. Two ways to query it:

**Protocol-neutral (`source` + `Query`)** — the same code shape for any protocol:

```python
from honua_sdk import HonuaClient, Query, SourceDescriptor, SourceLocator

with HonuaClient("http://localhost:8080", api_key=API_KEY) as client:
    source = client.source(
        SourceDescriptor(
            id="parcels",
            protocol="geoservices-feature-service",
            locator=SourceLocator(service_id="parcels", layer_id=0),
        )
    )
    result = source.query(Query(where="status = 'active'", out_fields=["*"]))
    print(f"{len(result.features)} features")
```

**FeatureServer client (native shape)** — when you want the protocol's own surface:

```python
with HonuaClient("http://localhost:8080", api_key=API_KEY) as client:
    fs = client.feature_server("parcels")
    # query, count, and layer metadata against layer 0
```

The `where` and `out_fields` map onto the FeatureServer query parameters in [Query features](../../guides/query-analyze/query-features.md). With the `[geopandas]` extra installed, results can be loaded into a `GeoDataFrame`.

## Run a STAC search

Honua exposes a [STAC API](../../reference/protocols/stac.md) for spatiotemporal discovery. Search it through the dedicated STAC client or the `source` facade.

**STAC client:**

```python
with HonuaClient("http://localhost:8080", api_key=API_KEY) as client:
    stac = client.stac()
    # browse collections, then search items by bbox / datetime / collections
```

**Protocol-neutral (`source` + `Query`)** — point a source at a STAC collection:

```python
from honua_sdk import HonuaClient, Query, SourceDescriptor, SourceLocator

with HonuaClient("http://localhost:8080", api_key=API_KEY) as client:
    stac_source = client.source(
        SourceDescriptor(
            id="imagery",
            protocol="stac",
            locator=SourceLocator(collection_id="imagery"),
        )
    )
    result = stac_source.query(Query(), limit=10)
    for item in result.features:
        print(item.properties)
```

`bbox`, `datetime`, and `collections` correspond to the [STAC search parameters](../../reference/protocols/stac.md#search-parameters). The `limit` keyword caps the page size.

## Query OGC API Features

The same layer is also reachable as an [OGC API Features](../../reference/protocols/ogc-apis.md) collection:

```python
with HonuaClient("http://localhost:8080", api_key=API_KEY) as client:
    ogc = client.ogc_features()
    collections = ogc.collections()

    parcels = ogc.collection("parcels")
    items = parcels.items(limit=100, filter="status = 'active'")
    all_items = parcels.items_all(page_size=500, max_pages=20)
```

`items_all` handles paging for you, following the `next` links the server returns.

## Next steps

- [Get started with the Python SDK](getting-started.md)
- [GeoServices REST reference](../../reference/protocols/geoservices-rest.md)
- [STAC reference](../../reference/protocols/stac.md)
- [SDK overview](../README.md)
