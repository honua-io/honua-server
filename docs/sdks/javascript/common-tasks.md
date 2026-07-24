# JavaScript SDK: common tasks

Two of the most common reads with the Honua JavaScript SDK: querying a FeatureServer layer and searching a STAC catalog. Both assume a constructed client — see [Get started with the JavaScript SDK](getting-started.md).

## Query a FeatureServer layer

Honua serves layers over the ArcGIS-compatible [GeoServices REST](../../reference/protocols/geoservices-rest.md) FeatureServer. Two ways to query it:

**Directly on the client** — the GeoServices shape:

```ts
import { HonuaClient } from "@honua/sdk-js/honua";

const client = new HonuaClient({ baseUrl: "http://localhost:8080", apiKey: API_KEY });

const { features } = await client.queryFeatures({
  serviceId: "parcels",
  layerId: 0,
  where: "status = 'active'",
  outFields: ["OBJECTID", "NAME"],
  returnGeometry: true,
  resultRecordCount: 100,
});

console.log(`${features.length} features`);
```

**Protocol-neutral (`Dataset` / `Source`)** — the same code shape for any protocol, with built-in paging via `queryAll`:

```ts
import { createDataset, PROTOCOL_DEFAULT_CAPABILITIES } from "@honua/sdk-js/contract";
import { HonuaClient } from "@honua/sdk-js/honua";

const client = new HonuaClient({ baseUrl: "http://localhost:8080", apiKey: API_KEY });

const dataset = createDataset({
  id: "parcels",
  client,
  sources: [{
    id: "parcels-fs",
    protocol: "geoservices-feature-service",
    locator: { url: "http://localhost:8080", serviceId: "parcels", layerId: 0 },
    capabilities: PROTOCOL_DEFAULT_CAPABILITIES["geoservices-feature-service"],
  }],
});

const result = await dataset.source("parcels-fs")!.queryAll({
  where: "STATUS = 'ACTIVE'",
  outFields: ["OBJECTID", "NAME"],
  returnGeometry: true,
  pagination: { limit: 500 },
});

console.log(`Loaded ${result.features.length} features`);
```

`HonuaFeatureLayer` (from the `honua` subpath) also offers `query`, `stream`, `queryCount`, and `queryObjectIds`. The query fields map onto the FeatureServer parameters in [Query features](../../guides/query-analyze/query-features.md).

## Run a STAC search

Honua exposes a [STAC API](../../reference/protocols/stac.md) for spatiotemporal discovery. Use the dedicated STAC search helper:

```ts
import { createHonuaStacSearch } from "@honua/sdk-js";

const stac = createHonuaStacSearch({ baseUrl: "http://localhost:8080" });

const results = await stac.searchAll({
  collections: ["imagery"],
  bbox: [-122.5, 37.7, -122.3, 37.9],
  datetime: "2025-01-01T00:00:00Z/..",
  limit: 10,
});

for (const item of results.features) {
  console.log(item.id, item.collection);
}
```

`searchAll` follows the catalog's paging links so you get every matching item. `collections`, `bbox`, and `datetime` correspond to the [STAC search parameters](../../reference/protocols/stac.md#search-parameters). You can also wire a STAC collection into the protocol-neutral contract with the `stacSearchSource()` factory from `@honua/sdk-js/contract`.

## Migrate ArcGIS JS code

The JavaScript engine from [`honua-migrate`](https://github.com/honua-io/honua-migrate) scans and rewrites ArcGIS JS API imports toward the Honua SDK:

```bash
npm install --global @honua/honua-migrate
honua-js-migrate scan ./src
honua-js-migrate codemod ./src --write --report migration-report.json
```

See [ArcGIS apps & SDKs](../../guides/migrate/arcgis-apps-and-sdks.md) for the migration path and the `esri-compat` layer.

## Next steps

- [Get started with the JavaScript SDK](getting-started.md)
- [GeoServices REST reference](../../reference/protocols/geoservices-rest.md)
- [STAC reference](../../reference/protocols/stac.md)
- [MapLibre web maps](../../guides/connect/maplibre-web-maps.md)
- [SDK overview](../README.md)
