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
| `GET`  | `/api/enrich/catalog` | List the configuration-driven enrichment datasets you may reference (back-compat). |
| `GET`  | `/api/enrich/datasets` | Discover the managed enrichment-dataset catalog, filtered by your edition (#2280). |
| `GET`  | `/api/enrich/datasets/{id}` | Discover a single managed enrichment dataset by id. |
| `POST` | `/api/enrich/datasets` | **Admin:** register a managed layer as an enrichment dataset (#2280). |
| `PUT`  | `/api/enrich/datasets/{id}` | **Admin:** update a registered enrichment dataset. |
| `DELETE` | `/api/enrich/datasets/{id}` | **Admin:** deregister an enrichment dataset. |
| `POST` | `/api/enrich`         | Enrich a registered source layer with attributes from an enrichment dataset. |

The compute endpoint (`POST /api/enrich`) requires an active **Pro** entitlement
(it shares the `analytics.spatial-join` entitlement, since enrichment is a curated
facade over the same join primitive). Discovery (`GET /api/enrich/datasets`) is
**edition-filtered** rather than hard-gated: callers see only datasets whose
minimum edition is at or below theirs. The admin registration routes require the
admin authorization policy and are only mapped when the active data provider is
Postgres (the managed registry table is Postgres-backed).

## Managed enrichment-dataset catalog (#2280)

`POST /api/enrich/datasets` designates an existing managed layer as a reusable
enrichment dataset. Each entry captures: a stable `id` (slug), `title`,
`category` (`boundary` | `demographic` | `poi`), the backing `layerId`,
`geometryType`, joinable `attributes`, default `defaultPredicate`/`distanceMeters`,
and the provenance/`attribution`/`license`/`minimumEdition` metadata so downstream
consumers can comply with the data provider's terms. The catalog is persisted in
`honua.enrichment_datasets` (migration 071), cached through the shared
generation-stamped catalog scope, and invalidated on register/update/deregister.

`POST /api/enrich` resolves its `datasetId` through this catalog (falling back to
the configuration catalog key for back-compat), maps the enrichment `method`
vocabulary (`intersects`, `point-in-polygon`, `within-distance`) to the canonical
spatial predicates, supports `outputFields` and per-match `aggregates`
(count/sum/avg/min/max), echoes the dataset attribution in the
`X-Honua-Data-Attribution` response header, and returns `413` (pointing to the
async batch path) when the source selection exceeds the synchronous input cap.

## Async batch enrichment jobs (`enrichment.enrich`)

Large or staged-input enrichment runs as a **canonical geoprocessing job**
(#2283) through the existing OGC API Processes surface — there is no
enrichment-local job lifecycle:

- **Submit**: `POST /ogc/processes/processes/enrichment.enrich/execution` with
  the same enrichment vocabulary as `POST /api/enrich` — `datasetId` (required),
  `method` (`intersects`, `point-in-polygon`, `within`, `within-distance`,
  `nearest-neighbor`), `outputFields`, `aggregates` (`field:stat` pairs), and the
  source as EITHER a registered `layerId` (with optional `where`/`bbox`
  windowing) OR a staged inline FeatureCollection via `input`
  (`data:application/geo+json;base64` data URI). Returns `201` with a job id.
- **Poll / results / dismiss**: the standard job endpoints —
  `GET /ogc/processes/jobs/{jobId}`, `GET /ogc/processes/jobs/{jobId}/results`,
  `DELETE /ogc/processes/jobs/{jobId}`.
- **Results** are an enriched GeoJSON FeatureCollection artifact (`JOIN_COUNT`,
  carried attributes, aggregates; `NEAR_DIST` for nearest-neighbor) with the
  dataset id and attribution embedded as foreign members.
- **Gating**: the shared `analytics.spatial-join` (Pro) entitlement and the
  dataset's `minimumEdition` are enforced at execution.
- **CRS**: both the source and dataset layers are streamed in EPSG:4326 and the
  result is published in EPSG:4326, so a cross-SRID pair is never joined on
  incomparable ordinates and the GeoJSON output is valid WGS 84 (RFC 7946).
  `bbox` is likewise EPSG:4326. Within-distance thresholds and `NEAR_DIST` are
  therefore in degrees (managed NTS join, no geodesic conversion) — the sync
  endpoint's `distanceMeters` semantics do not apply, so supply `distance`
  explicitly in degrees.
- **Bounded input**: `maxInputFeatures` (default 250000) caps each layer read
  while streaming, so an oversized selection fails fast with an actionable error
  instead of exhausting worker memory. The value is clamped to an operator
  ceiling of 1000000 — a caller may only lower the cap, never disable it — and it
  applies equally to a staged `input` collection. `maxCarriedMatchValues`
  (default 20000000, likewise lower-only) additionally bounds the join itself,
  which is a Cartesian product the per-layer caps cannot see.

### Authorization on the async path

- **Layer read access is enforced at submission**, against the submitting
  principal, for BOTH the caller-selected source layer and the dataset's backing
  layer — the same pair the synchronous endpoint validates. `Process.Execute`
  authorizes running a process; it never authorizes the specific layers the
  process reads. A caller that cannot read either layer is refused with `401`/
  `403` and no job is queued. Denials are indistinguishable from "layer does not
  exist", so the submit surface cannot be used to probe layer ids.
- **The authorized dataset layer is bound to the job.** Re-pointing a managed
  enrichment dataset at a different layer while a job is queued causes that job
  to fail at execution rather than read a layer that was never authorized;
  resubmit so the new layer is authorized.
- **Known limitation — row-level security and field masking are NOT applied to
  the job's layer reads.** Both are request-scoped concerns and a job executes on
  a background worker with no request context, so a caller who may read a layer
  but is restricted to particular rows or fields receives unrestricted rows and
  unmasked attributes in the job artifact. This affects every layer-sourced
  geoprocessing process, not only enrichment, and is tracked as
  [#3068](https://github.com/honua-io/honua-server/issues/3068). Until it lands,
  do not rely on RLS or field-mask policies to constrain what a
  `Process.Execute` holder can obtain through a job artifact — restrict the layer
  itself.

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
- **No inline-GeoJSON source feature sets on the synchronous endpoint.** The
  sync source must be a registered layer; inline/staged feature sets run through
  the async `enrichment.enrich` job's `input` data URI instead.
- **Synchronous spatial-join method only.** The point-in-polygon / within /
  contains / dwithin predicates are served through the shared spatial-join
  pipeline. Nearest-neighbour is available on the async `enrichment.enrich` job
  path (#2283); buffer+aggregate weighting and intersection area-weighting as
  *enrichment methods*, plus CDC-triggered enrichment, are deferred to
  follow-up work.

These deferrals are tracked under [#374](https://github.com/honua-io/honua-server/issues/374).
