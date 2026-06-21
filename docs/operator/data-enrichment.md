# Data enrichment API (#374)

The data-enrichment API enriches your own features with attributes drawn from a
registered reference dataset (administrative boundary, points of interest, or
demographic reference layer) using a spatial join. It is the
[CARTO Data Observatory](https://carto.com/data-observatory) enrichment pattern —
*input features + enrichment dataset → enriched features* — built on Honua's
shared spatial-analytics pipeline.

## Endpoints

| Method | Route | Description |
|---|---|---|
| `GET`  | `/api/enrich/catalog` | List the registered enrichment datasets you may reference. |
| `POST` | `/api/enrich`         | Enrich a registered source layer with attributes from a registered enrichment dataset. |

Both endpoints require an active **Pro** entitlement (they share the
`analytics.spatial-join` entitlement, since enrichment is a curated facade over
the same join primitive).

## Registering enrichment datasets

The catalog is **operator-curated** and configuration-driven. Publish the
reference data as an ordinary layer, then register its key under the
`DataEnrichment` configuration section:

```jsonc
{
  "DataEnrichment": {
    "Datasets": [
      {
        "key": "admin-boundaries",
        "displayName": "Administrative boundaries",
        "category": "boundary",
        "layerId": 12,
        "predicate": "intersects",
        "attributes": ["country", "state", "county"]
      },
      {
        "key": "poi",
        "displayName": "Points of interest",
        "category": "poi",
        "layerId": 14,
        "predicate": "dwithin",
        "distanceMeters": 500
      }
    ]
  }
}
```

| Field | Meaning |
|---|---|
| `key` | Stable, case-insensitive identifier callers pass as `datasetKey`. |
| `category` | Presentation-only classification (`boundary`, `poi`, `demographic`). |
| `layerId` | Registered layer providing the enrichment geometry/attributes. Read access is re-checked per request. |
| `predicate` | Default spatial predicate: `intersects`, `contains`, `within`, `dwithin`. |
| `distanceMeters` | Default radius when `predicate` is `dwithin`. |
| `attributes` | Default reference-layer attributes carried onto each enriched feature. |

## Enriching features

```http
POST /api/enrich
Content-Type: application/json

{
  "datasetKey": "admin-boundaries",
  "sourceLayerId": 3,
  "where": "status = 'active'",
  "predicate": "intersects",
  "attributes": ["country", "state"]
}
```

The response is a GeoJSON `FeatureCollection`: one feature per source row,
carrying a `matchCount` plus the requested enrichment attributes in its
`properties`. `predicate`, `distanceMeters`, and `attributes` are optional
overrides; when omitted the dataset's registered defaults apply.

## MVP scope and deferrals

This first increment intentionally keeps a tight scope and defers the
enterprise/revenue features in the ticket:

- **Registered layers only.** Enrichment joins your registered source layer to a
  registered reference layer. There is **no bundled or proxied third-party
  demographic data** (ACS/Census/OSM extracts, Natural Earth boundaries, etc.) —
  no such dataset ships with the server. Operators bring their own reference data
  and register it as above.
- **No inline-GeoJSON source feature sets.** The source must be a registered
  layer; ad-hoc inline feature sets are not yet supported.
- **Synchronous spatial-join method only.** The point-in-polygon / within /
  contains / dwithin predicates are served through the shared spatial-join
  pipeline. Nearest-neighbour, buffer+aggregate weighting, and intersection
  area-weighting as *enrichment methods*, plus async/batch jobs and
  CDC-triggered enrichment, are deferred to follow-up work.

These deferrals are tracked under [#374](https://github.com/honua-io/honua-server/issues/374).
