# Query features

Filter, page, sort, and project features over whichever protocol your client speaks — OGC API Features with CQL2, ArcGIS-style FeatureServer queries, or OData v4 — against the same published layer.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)).

The fastest way to query from a terminal is the **`honua` CLI** (installed with
the JS SDK: `npm i -g @honua/sdk-js`, or run ad hoc with `npx @honua/sdk-js
honua …`). It wraps the same FeatureServer query endpoint, prints a readable
table by default, and emits GeoJSON or JSON on request — no URL-encoding, no
`f=` parameters. Point it at your server once:

```bash
export HONUA_BASE_URL=http://localhost:8080
# export HONUA_API_KEY=... # only if the service requires auth
```

The three HTTP surfaces answer the same questions with different syntax: OGC items take `filter` (CQL2 text or JSON), FeatureServer takes SQL-like `where` clauses, and OData takes `$filter`. The CLI maps onto the FeatureServer surface; the generated [OpenAPI and API explorer](../../reference/openapi-and-explorer.md) documents protocol-specific request shapes. The full filter-language reference is in [CQL2 and filtering](../../reference/cql2-and-filtering.md). To create or change features, see [edit features](../edit/edit-features.md).

## Steps

1. Ask an attribute question — "population over 10000". The `<service>/<layer>` reference uses the numeric layer id:

   ```bash
   honua query my_service/0 --where "population > 10000"
   honua query my_service/0 --where "population > 10000" --format geojson
   ```

   Use the API explorer when you need the equivalent OGC API Features or OData request.

2. Filter spatially with a bounding box (lon,lat order: `minLon,minLat,maxLon,maxLat`):

   ```bash
   honua query my_service/0 --bbox -122.5,37.7,-122.3,37.8 --format geojson
   ```

   CQL2 and FeatureServer also take real geometries, and OData supports `geo.distance` and `geo.intersects` in `$filter` (see [CQL2 and filtering](../../reference/cql2-and-filtering.md)).

3. Page through large results with `--limit` (the response notes when more rows are available):

   ```bash
   honua query my_service/0 --limit 100
   ```

   OGC responses include `next` links — follow those instead of computing offsets yourself when possible.

4. Select only the properties you need with repeated `--fields`:

   ```bash
   honua query my_service/0 --fields name --fields population
   ```

   In protocol clients, `sortby` takes comma-separated fields with an optional `-` prefix for descending; `orderByFields` takes `field ASC|DESC`.

5. Count without fetching when you only need a number:

   ```bash
   honua query my_service/0 --where "population > 10000" --count
   ```


## Verify

```bash
honua query my_service/0 --limit 1 --format geojson
```

Expected (trimmed): a GeoJSON FeatureCollection with `numberMatched` and paging links.

```json
{ "type": "FeatureCollection", "features": [ { "type": "Feature" } ],
  "numberMatched": 1234, "links": [ { "rel": "next" } ] }
```

## Troubleshoot

- **400 `Unknown query parameter`** — each endpoint validates its parameter set strictly; check spelling (`filter-lang`, not `filterLang`; `resultRecordCount`, not `count`).
- **400 on `filter`** — CQL2 syntax error; the response names the offending token. Property names are case-sensitive — check `GET /ogc/features/collections/{collectionId}/queryables`.
- **Empty results with a spatial filter** — coordinates are lon,lat order for `bbox` and WKT; FeatureServer envelopes need `inSR` when not in the layer's SRID.
- **FeatureServer returns fewer rows than expected** — server page limits apply; the response sets `exceededTransferLimit: true` when truncated, so keep paging.
- **OData 404 on `/odata/Layers(...)`** — the path value is the numeric layer id, not the collection name. See [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Export data](export-data.md)
- [CQL2 and filtering reference](../../reference/cql2-and-filtering.md)
- [Work with time](work-with-time.md)
