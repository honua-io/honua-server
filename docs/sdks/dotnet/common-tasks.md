# .NET SDK: common tasks

Two of the most common reads with the Honua .NET SDK: querying a FeatureServer layer and searching a STAC catalog. Both assume a registered client — see [Get started with the .NET SDK](getting-started.md).

## Query a FeatureServer layer

`IHonuaFeatureServerClient` wraps the ArcGIS-compatible [GeoServices REST](../../reference/protocols/geoservices-rest.md) FeatureServer. Register it (`AddHonua(o => o.UseGeoServices = true)` or `AddHonuaFeatureServer()`), then query by service id and numeric layer id:

```csharp
using Honua.Sdk.GeoServices.FeatureServer;

var fs = host.Services.GetRequiredService<IHonuaFeatureServerClient>();

var result = await fs.QueryAsync(
    serviceId: "default",
    layerId: 0,
    query: new FeatureServerQueryParams
    {
        Where          = "status = 'open'",
        OutFields      = new[] { "name", "status" },
        ReturnGeometry = true,
        ResultRecordCount = 100,
    });

foreach (var feature in result.Features)
{
    Console.WriteLine(feature.Attributes["name"]);
}
```

Related calls on the same client:

- `GetServiceInfoAsync(serviceId)` / `GetLayerInfoAsync(serviceId, layerId)` — metadata and field definitions.
- `QueryCountAsync(...)` — a count without fetching rows (`returnCountOnly`).
- `QueryIdsAsync(...)` — object ids only, for client-side paging.
- `GetFeatureAsync(serviceId, layerId, objectId)` — a single feature by id.

The `Where`, `OutFields`, and paging fields map directly onto the FeatureServer query parameters documented in [Query features](../../guides/query-analyze/query-features.md).

## Run a STAC search

`IHonuaStacClient` wraps Honua's [STAC API](../../reference/protocols/stac.md). Register it (`AddHonua(o => o.UseStac = true)` or `AddHonuaStac()`), then search across collections:

```csharp
using Honua.Sdk.Catalogs.Stac;

var stac = host.Services.GetRequiredService<IHonuaStacClient>();

// List what's available first.
var collections = await stac.ListCollectionsAsync();

// Search items by bounding box and time, POSTing a search request.
var items = await stac.SearchPostAsync(new StacSearchRequest
{
    Collections = new[] { "imagery" },
    Bbox        = new[] { -122.5, 37.7, -122.3, 37.9 },
    Datetime    = "2025-01-01T00:00:00Z/..",
    Limit       = 10,
});

foreach (var item in items.Features)
{
    Console.WriteLine($"{item.Id} @ {item.Collection}");
}
```

Other STAC helpers:

- `SearchAsync(StacSearchQuery)` — the same search over `GET /stac/search` (query string form).
- `GetCollectionAsync(collectionId)` / `GetItemsAsync(collectionId, query)` — browse one collection.
- `GetItemAsync(collectionId, itemId)` — fetch a single item.
- `GetLandingPageAsync()` — the catalog root.

`Bbox`, `Datetime`, `Collections`, and `Limit` correspond to the [STAC search parameters](../../reference/protocols/stac.md#search-parameters).

## Next steps

- [Get started with the .NET SDK](getting-started.md)
- [GeoServices REST reference](../../reference/protocols/geoservices-rest.md)
- [STAC reference](../../reference/protocols/stac.md)
- [SDK overview](../README.md)
