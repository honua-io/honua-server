---
type: guide
title: "Query features"
description: "Filter, page, sort, and project features over whichever protocol your client speaks — OGC API Features with CQL2, GeoServices REST-style FeatureServer queries, or OData v4 — against the same published layer."
resource: "honua://capability/serve.ogc-api-features"
---
# Query features

Filter, page, sort, and project features over whichever protocol your client speaks — OGC API Features with CQL2, GeoServices REST-style FeatureServer queries, or OData v4 — against the same published layer.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)).

The fastest way to query from a terminal is the **`honua` CLI** (installed with
the JS SDK: `npm i -g @honua/sdk-js`, or run ad hoc with `npx -p @honua/sdk-js
honua …` — the `-p` matters, because npx otherwise looks for a binary named
`sdk-js`). Note that the PyPI `honua-sdk` distribution installs an executable
with the same name and no `query` subcommand. It wraps the same FeatureServer query endpoint, prints a readable
table by default, and emits GeoJSON or JSON on request — no URL-encoding, no
`f=` parameters. Point it at your server once:

```bash
export HONUA_BASE_URL=http://localhost:8080
# export HONUA_API_KEY=... # only if the service requires auth
```

The three HTTP surfaces answer the same questions with different syntax: OGC items take `filter` (CQL2 text or JSON), FeatureServer takes SQL-like `where` clauses, and OData takes `$filter`. The CLI maps onto the FeatureServer surface; the [OpenAPI explorer](../../reference/openapi-and-explorer.md) documents OGC API Features, while `@honua/sdk-js` supplies the OData client. The full filter-language reference is in [CQL2 and filtering](../../reference/cql2-and-filtering.md). To create or change features, see [edit features](../edit/edit-features.md).

## Steps

1. Ask an attribute question — "population over 10000". The `<service>/<layer>` reference uses the numeric layer id that `honua layers <service>` lists. Layer ids are deployment-wide rather than per-service positions, so the `1` below is only a placeholder. The later steps reuse `LAYER_ID`:

   ```bash
   honua layers my_service
   LAYER_ID=1  # replace with an id from the list
   honua query "my_service/$LAYER_ID" --where "population > 10000"
   honua query "my_service/$LAYER_ID" --where "population > 10000" --format geojson
   ```

   Use the API explorer for the equivalent OGC API Features request, or the `@honua/sdk-js` OData source for its `$filter` equivalent.

2. Filter spatially with a bounding box (lon,lat order: `minLon,minLat,maxLon,maxLat`):

   ```bash
   honua query "my_service/$LAYER_ID" --bbox -122.5,37.7,-122.3,37.8 --format geojson
   ```

   CQL2 and FeatureServer also take real geometries, and OData supports `geo.distance` and `geo.intersects` in `$filter` (see [CQL2 and filtering](../../reference/cql2-and-filtering.md)).

3. Limit the first response with `--limit` (the table output notes when more rows are available):

   ```bash
   honua query "my_service/$LAYER_ID" --limit 100
   ```

   The default table output ends with `(more available; raise --limit)` when the server has more rows; `--format json` shows the FeatureServer `exceededTransferLimit` flag, and GeoJSON output omits it. The CLI has no page-offset option. Increasing `--limit` requests a larger first page, subject to server limits. For complete results, use a protocol client to page with FeatureServer `resultOffset`, or follow OGC `next` links.

4. Select only the properties you need with repeated `--fields`:

   ```bash
   honua query "my_service/$LAYER_ID" --fields name --fields population
   ```

   In protocol clients, `sortby` takes comma-separated fields with an optional `-` prefix for descending; `orderByFields` takes `field ASC|DESC`.

5. Count an attribute-filtered result without fetching its features:

   ```bash
   honua query "my_service/$LAYER_ID" --where "population > 10000" --count
   ```

   In CLI 0.4.0, `--count` applies `--where` but ignores `--bbox`. For spatial counts, use a protocol client with the same spatial filter and FeatureServer `returnCountOnly=true`, or inspect OGC `numberMatched`.

## Verify

```bash
LAYER_ID=1  # replace with an id from `honua layers my_service`
honua query "my_service/$LAYER_ID" --limit 1 --format geojson
```

Expected (trimmed): a GeoJSON FeatureCollection with at most one feature. The CLI prints only `type` and `features` (no `numberMatched` or `links`); for a total, run the same query with `--count`, and for `numberMatched` and `next` links, request the OGC API Features items endpoint.

```json
{ "type": "FeatureCollection",
  "features": [ { "type": "Feature", "geometry": { }, "properties": { } } ] }
```

## Troubleshoot

- **400 `Unknown query parameter`** — each endpoint validates its parameter set strictly; check spelling (`filter-lang`, not `filterLang`; `resultRecordCount`, not `count`).
- **400 on `filter`** — a CQL2 syntax error returns "Failed to parse CQL2 expression" without pointing at the position, so check operators and quoting by hand; an unknown property returns "Unknown field '…' in filter expression". Property names are case-sensitive — check `GET /ogc/features/collections/{collectionId}/queryables`.
- **Empty results with a spatial filter** — coordinates are lon,lat order for `bbox` and WKT; FeatureServer envelopes need `inSR` when not in the layer's SRID.
- **FeatureServer returns fewer rows than expected** — server page limits apply; the response sets `exceededTransferLimit: true` when truncated, so keep paging.
- **OData 404 on `/odata/Layers(...)`** — the path value is the numeric layer id, not the collection name. See [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Export data](export-data.md)
- [CQL2 and filtering reference](../../reference/cql2-and-filtering.md)
- [Work with time](work-with-time.md)
