# Query features

Filter, page, sort, and project features over whichever protocol your client speaks — OGC API Features with CQL2, ArcGIS-style FeatureServer queries, or OData v4 — against the same published layer.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)).

The three surfaces answer the same questions with different syntax: OGC items take `filter` (CQL2 text or JSON), FeatureServer takes SQL-like `where` clauses, and OData takes `$filter`. This page shows the request shapes; the full filter-language reference is in [CQL2 and filtering](../../reference/cql2-and-filtering.md). To create or change features, see [edit features](../edit/edit-features.md).

## Steps

1. Ask an attribute question — "population over 10000" — on each surface:

   ```bash
   BASE=http://localhost:8080
   COLL=my_layer        # OGC collection id
   SVC=my_service       # GeoServices service id
   LAYER=1              # numeric layer id (OData)

   # OGC API Features, CQL2 text
   curl -s "$BASE/ogc/features/collections/$COLL/items?filter=population%20%3E%2010000&filter-lang=cql2-text"

   # OGC API Features, CQL2 JSON (same filter, JSON-encoded)
   curl -s -G "$BASE/ogc/features/collections/$COLL/items" --data-urlencode \
     'filter={"op":">","args":[{"property":"population"},10000]}' --data-urlencode 'filter-lang=cql2-json'

   # GeoServices FeatureServer
   curl -s "$BASE/rest/services/$SVC/FeatureServer/0/query?where=population%20%3E%2010000&outFields=*&f=json"

   # OData v4
   curl -s "$BASE/odata/Layers($LAYER)/Features?\$filter=population%20gt%2010000"
   ```

2. Filter spatially. Every surface accepts a bounding box; CQL2 and FeatureServer also take real geometries, and OData supports `geo.distance` and `geo.intersects` in `$filter` (see [CQL2 and filtering](../../reference/cql2-and-filtering.md)):

   ```bash
   # OGC: bbox (minLon,minLat,maxLon,maxLat) or CQL2 S_INTERSECTS
   curl -s "$BASE/ogc/features/collections/$COLL/items?bbox=-122.5,37.7,-122.3,37.8"
   curl -s -G "$BASE/ogc/features/collections/$COLL/items" --data-urlencode \
     "filter=S_INTERSECTS(geometry,POLYGON((-122.5 37.7,-122.3 37.7,-122.3 37.8,-122.5 37.8,-122.5 37.7)))"

   # FeatureServer: envelope + spatial relationship
   curl -s "$BASE/rest/services/$SVC/FeatureServer/0/query?geometry=-122.5,37.7,-122.3,37.8&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&inSR=4326&f=geojson"
   ```

3. Page through large results:

   ```bash
   curl -s "$BASE/ogc/features/collections/$COLL/items?limit=100&offset=200"
   curl -s "$BASE/rest/services/$SVC/FeatureServer/0/query?where=1%3D1&resultOffset=200&resultRecordCount=100&f=json"
   curl -s "$BASE/odata/Layers($LAYER)/Features?\$top=100&\$skip=200&\$count=true"
   ```

   OGC responses include `next` links — follow those instead of computing offsets yourself when possible.

4. Sort and select only the properties you need:

   ```bash
   curl -s "$BASE/ogc/features/collections/$COLL/items?sortby=-population&properties=name,population"
   curl -s "$BASE/rest/services/$SVC/FeatureServer/0/query?where=1%3D1&orderByFields=population%20DESC&outFields=name,population&f=json"
   curl -s "$BASE/odata/Layers($LAYER)/Features?\$orderby=population%20desc&\$select=name,population"
   ```

   `sortby` takes comma-separated fields with an optional `-` prefix for descending; `orderByFields` takes `field ASC|DESC`.

5. Count without fetching when you only need a number:

   ```bash
   curl -s "$BASE/rest/services/$SVC/FeatureServer/0/query?where=population%20%3E%2010000&returnCountOnly=true&f=json"
   curl -s "$BASE/odata/Layers($LAYER)/Features/\$count?\$filter=population%20gt%2010000"
   ```

## Verify

```bash
curl -s "$BASE/ogc/features/collections/$COLL/items?limit=1"
```

Expected (trimmed): a GeoJSON FeatureCollection with `numberMatched` and paging links.

```json
{ "type": "FeatureCollection", "features": [ { "type": "Feature" } ],
  "numberMatched": 1234, "links": [ { "rel": "next" } ] }
```

## Troubleshoot

- **400 `Unknown query parameter`** — each endpoint validates its parameter set strictly; check spelling (`filter-lang`, not `filterLang`; `resultRecordCount`, not `count`).
- **400 on `filter`** — CQL2 syntax error; the response names the offending token. Property names are case-sensitive — check `GET /ogc/features/collections/$COLL/queryables`.
- **Empty results with a spatial filter** — coordinates are lon,lat order for `bbox` and WKT; FeatureServer envelopes need `inSR` when not in the layer's SRID.
- **FeatureServer returns fewer rows than expected** — server page limits apply; the response sets `exceededTransferLimit: true` when truncated, so keep paging.
- **OData 404 on `/odata/Layers(...)`** — `$LAYER` is the numeric layer id, not the collection name. See [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Export data](export-data.md)
- [CQL2 and filtering reference](../../reference/cql2-and-filtering.md)
- [Work with time](work-with-time.md)
