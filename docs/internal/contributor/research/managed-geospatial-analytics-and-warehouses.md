# Research: Managed geospatial analytics + warehouse integration

Status: Research / spike (decision-oriented). Owners: server.
Resolves honua-server#950 (AWS/Azure managed geospatial analytics) and
honua-server#951 (BigQuery + Snowflake parking lot).

## TL;DR (decisions)

| Service | Honua fit as read provider | State today | Decision |
|---|---|---|---|
| **Amazon Redshift** | Good (SQL + `ST_*` + WKB out) | **Already shipped** (read-only, #1712) | **Keep**; harden follow-on slices on demand |
| **Amazon Athena** | Good shape, poor latency/cost | Not built | **Defer (build-ready slice)** — gate on a named buyer |
| **Azure Data Explorer (Kusto)** | Poor (KQL, not SQL; GeoJSON dynamic) | Not built | **Park / docs-only** — large effort, weak OGC-feature fit |
| **Azure Stream Analytics** | N/A (streaming engine, not a queryable store) | Not built | **No connector**; note for future event/streaming lane (#357) |
| **Azure SQL Database** | Already covered | Covered by SQL Server provider | **No new work**; document equivalence |
| **Snowflake** | Good (SQL + `ST_*` + `ST_ASWKB`) | **Already shipped** (read-only, #1713) | **Reconcile #951** — Snowflake read-only is done; keep BigQuery parked |
| **Google BigQuery** | Good (SQL + `ST_*` + `ST_AsBinary`) | Not built | **Keep parked** (GTM: no Google) — promote only on named buyer |

The single most important finding: the warehouse read-provider pattern these
issues ask us to *evaluate* has **already been built three times** (Redshift,
Snowflake, Databricks). The remaining decision is not "can we" but "should we
spend the next increment of effort," and the answer is demand-gated, not
architectural.

## How a warehouse becomes a Honua data source (the established pattern)

Honua already has a clean, provider-neutral seam for read-only external stores.
Three warehouse providers ship against it today, so the integration shape is
proven, not speculative:

- **Provider seam** — a backend implements
  `IFeatureDataProvider` + `IFeatureReader` (and `IBindableFeatureDataProvider`
  for per-layer Metadata-v2 binding).
  `src/Honua.Core/Features/FeatureStore/Abstractions/IFeatureDataProvider.cs`,
  `IFeatureReader.cs`.
- **Capability declaration** — `FeatureProviderCapabilities`
  (`src/Honua.Core.Abstractions/Features/FeatureStore/Domain/FeatureProviderCapabilities.cs`)
  lets a read-only backend advertise `SupportsQuery/Count/Extent`, set
  `Edits = FeatureProviderEditCapabilities.ReadOnly`, and disable native output
  formats. ADR-0035 makes read-only providers first-class by *declaring*
  unsupported paths rather than throwing from protocol adapters.
- **Storage mapping** — `FeatureStorageMapping`
  (`.../Domain/FeatureStorageMapping.cs`) carries `TableName`, `SchemaName`,
  `CatalogName`, `DatabaseName`, `PrimaryKeyColumn`, `GeometryColumn`,
  `StorageSrid`, `TemporalColumn`, and a `ProviderOptions` bag (e.g.
  `geometryType=geography|geometry`). No per-provider metadata store
  (ADR-0035).
- **Provider name + routing** — canonical names live in `DataProviderNames`
  (`.../Domain/DataProviderNames.cs`; already includes `redshift`, `snowflake`,
  `databricks` with aliases). `FeatureProviderQueryRouter`
  (`src/Honua.Core/Features/FeatureStore/Services/FeatureProviderQueryRouter.cs`)
  resolves *service → secure connection → provider engine → registered
  implementation* per ADR-0035. `DataSource:Provider` selects the **primary**
  backend; warehouse providers register as **secondary** read backends and are
  routed per-layer by the secure connection's provider name.
- **Query/SQL generation** — the canonical `FeatureQuery`
  (`.../Domain/FeatureQuery.cs`) flows through per-provider
  `IFeatureQueryBuilder`, `ISqlDialect` (identifier quoting + parameter marker),
  and `ISqlFilterTranslator` implementations. This SQL-centric seam is the
  decisive fit/no-fit test below.

A new SQL warehouse provider is therefore a bounded, repeatable slice:
`<Provider>FeatureStore` + `<Provider>SqlDialect` + `<Provider>FeatureQueryBuilder`
+ a connection driver + DI registration + `DataProviderNames` entry + a docs page
+ unit tests (gated integration tests where no Testcontainer exists). Redshift,
Snowflake, and Databricks each follow exactly this layout under `src/Honua.<X>/`.

### Two governing constraints on every warehouse read provider

1. **Shared filter translator emits PostGIS SQL.** The `ISqlFilterTranslator`
   pipeline currently registers only a Postgres translator (JSONB `->>`, `::`
   casts, PostGIS `ST_*` signatures). All three shipped warehouse providers
   therefore **reject a populated `FeatureQuery.SqlFilter`** and re-parse the
   canonical `Where` text with a provider-aware parser. Any new warehouse
   provider inherits this limitation until it registers its own dialect
   translator. This caps CQL2/FES/OData `$filter` push-down for warehouse layers
   — a real GA hardening item, not a per-provider bug.
2. **Interactive map rendering is the hard cost/latency gate.** Per
   `CLAUDE.md`, ad-hoc spatial requests (arbitrary bbox/geometry/distance) are
   explicitly **not** response-cached — keys are too high-cardinality. Warehouse
   backends with per-TB-scanned billing and second-scale async query models
   (Athena, BigQuery on-demand) are fine for export/analysis but expensive and
   slow under uncached tile/feature fan-out. This, not SQL syntax, is the real
   reason to gate Athena/BigQuery behind a buyer.

## AWS/Azure managed services (#950)

### Amazon Redshift — already shipped (keep)

Read-only provider delivered under #1712 (`src/Honua.Redshift/`,
`docs/reference/configuration/data-sources/redshift.md`).
`FeatureProviderCapabilities.ReadOnlyMySql`: query/count/extent/object-ids,
`Intersects/Within/Contains/Disjoint/EnvelopeIntersects`, narrow `Where`
grammar, `LIMIT/OFFSET` paging. Connects via **Npgsql** (Redshift speaks the
Postgres wire protocol) but restricts to Redshift-native `ST_*`
(`ST_AsBinary`, `ST_GeomFromWKB`, `ST_XMin…`), never PostGIS-only functions.
`geometryType` provider option selects planar `GEOMETRY` vs geodetic
`GEOGRAPHY`. Auth = connection string with SSL; in customer-operated
deployments prefer secret-store references (Redshift does not require AWS SigV4
for the data path — it's standard Postgres auth, optionally IAM-token based).

- Register as read-only source? **Yes — done.**
- Publish results as OGC/GeoServices layers? **Yes** — routes through the shared
  query pipeline, so all read protocols (FeatureServer, OGC API Features, WFS,
  MVT, etc.) get it for free.
- Push down predicates/bounds? **Spatial + narrow attribute `Where` yes;**
  translated `SqlFilter`, distance/KNN, aggregation, reprojection **no**
  (follow-on slices).
- Where it lives: `honua-server` (done). Docs already present.
- **Decision:** keep as-is; promote follow-on slices (statistics aggregates, a
  Redshift `ISqlFilterTranslator`, native output formats) only as GA/Enterprise
  demand or a named buyer pulls them.

### Amazon Athena — defer (build-ready slice, buyer-gated)

Serverless Trino/Presto over S3 via the Glue Data Catalog. Geospatial functions
(`ST_GeometryFromText`/`ST_Point`/`ST_Intersects`/`ST_Contains`/`ST_AsBinary`,
etc.) operate on **WKT/WKB text** — there is no native typed `GEOMETRY` column
and SRID is assumed (typically 4326). Glue tables map cleanly onto
`FeatureStorageMapping` (database/schema/table + geometry column as WKT/WKB).

Fit assessment:
- **SQL shape: good.** Trino SQL + `ST_*` + WKB output maps onto the same
  `IFeatureQueryBuilder`/`ISqlDialect`/data-access slice as Redshift/Snowflake.
- **Driver:** AWS SDK async model — `StartQueryExecution → poll
  GetQueryExecution → GetQueryResults`, results staged to S3. There is no
  low-latency ADO.NET path; the JDBC/ODBC drivers wrap the same async API.
- **Auth:** AWS IAM / SigV4 (access keys, assumed roles, or instance/pod
  identity). Heavier than the connection-string model the other warehouse
  providers use — needs an AWS credential-provider in the connection driver.
- **Latency/cost:** seconds-scale per query and **per-TB-scanned billing** —
  acceptable for export/analysis, poor for uncached interactive tile/feature
  rendering (see constraint #2). Result reuse helps but cannot be relied on for
  ad-hoc spatial keys.
- **Effort:** **M** — comparable to Snowflake plus an AWS-SDK async/credential
  connection driver. Where: `honua-server` (`src/Honua.Athena/`).
- **Decision:** **Defer.** Architecturally clean and build-ready, but the
  async/per-TB profile does not fit Honua's interactive read path and there is
  no named buyer. Promote on a buyer who wants **export / analysis publication
  of S3+Glue spatial data**, not live map serving. Keep an explicit follow-on
  ticket scoped GA/Enterprise.

### Azure Data Explorer (Kusto/ADX) — park / docs-only

ADX is a telemetry/log analytics engine queried in **KQL, not SQL**. Geospatial
support is real but shaped for analytics, not OGC feature serving: geometry is
**GeoJSON in dynamic columns** (no typed geometry), and functions are KQL
(`geo_point_in_polygon`, `geo_intersects_2lines`, S2/H3/geohash cells,
`geo_distance_2points`). Access is the `Microsoft.Azure.Kusto.Data` SDK over
REST; auth is Entra ID / managed identity (good fit for Azure deployments).

Fit assessment:
- **SQL seam mismatch (decisive).** Honua's provider seam is SQL-first
  (`IFeatureQueryBuilder`, `ISqlDialect`, `ISqlFilterTranslator`). ADX would
  need a parallel KQL builder/translator and a GeoJSON-dynamic materialization
  path — it cannot reuse the warehouse slice the way Athena/BigQuery can.
- **Effort:** **L (large)** — effectively a new query-generation family.
- **Decision:** **Park (docs-only).** Weak OGC-feature fit and large effort
  versus a migration-first GTM. Revisit only for a telemetry/geospatial buyer
  who specifically standardizes on ADX; even then, prefer materializing ADX
  output into a SQL-queryable store over a native KQL provider.

### Azure Stream Analytics — no connector (event-lane note)

ASA is a **streaming query engine** (SQL-like with `ST_Within`/`ST_Overlaps`/
`ST_Intersects`/`ST_Distance`/`CreatePoint` over GeoJSON), reading from Event
Hubs/IoT Hub and writing to sinks. It is **not a queryable data store** — there
is no random-access read API to register as a feature source.

- **Decision:** **No read connector — N/A by design.** It is relevant only if
  Honua grows an event/streaming ingestion lane; route any geospatial-streaming
  interest to the event-bus track (#357) where ASA would be an *upstream
  producer* landing data into a Honua-readable store (PostGIS/warehouse), not a
  provider. Docs-only mention.

### Azure SQL Database — already covered (no new work)

Azure SQL Database speaks **T-SQL with the same `geometry`/`geography`** types
and `STIntersects`/`STWithin`/`STAsBinary` surface as on-prem SQL Server. The
existing SQL Server provider (`DataProviderNames.SqlServer`, ADR-0035 follow-on)
connects to it unchanged — it is a connection-string/endpoint difference, not a
new dialect.

- **Decision:** **No new provider.** Document that Azure SQL Database / Managed
  Instance is reached through the SQL Server provider; optionally add Entra-ID
  (access-token) auth to that connection driver if a buyer needs managed
  identity instead of SQL auth.

## Parking lot: BigQuery + Snowflake (#951)

### Snowflake — already shipped; reconcile the parking-lot stance

#951 lists Snowflake as parked ("no Snowflake support now"), but a **read-only
Snowflake provider already shipped** under #1713 (`src/Honua.Snowflake/`,
`docs/reference/configuration/data-sources/snowflake.md`). It supports native
`GEOGRAPHY`/`GEOMETRY` via `ST_ASWKB`, the same read/spatial-filter/paging
surface as Redshift, Entra-independent connection-string auth (account / user /
warehouse / role), and is explicitly **excluded from Native AOT / slim builds**
because the `Snowflake.Data` driver uses reflection (`HONUA_SKIP_SNOWFLAKE`).

- **Decision:** **Reconcile #951.** Snowflake read-only is done — the
  parking-lot text is stale. Action: update/close the Snowflake half of #951 as
  delivered (read slice), and file any *additional* Snowflake scope
  (statistics, dialect translator, edits) as demand-gated GA follow-ons. Do not
  re-park Snowflake.

### Google BigQuery — keep parked (GTM, not architecture)

BigQuery GIS is technically the **strongest remaining fit**: Standard SQL with a
native `GEOGRAPHY` type (WGS84/S2 geodetic; no planar `GEOMETRY`), full `ST_*`
surface incl. `ST_GeogFromText`, `ST_DWithin`, `ST_Intersects`, and `ST_AsBinary`
for WKB output — i.e. it maps onto the Snowflake-style slice almost line for
line. Access via `Google.Cloud.BigQuery.V2` (jobs API; Storage Read API for
high-throughput); auth via GCP service account / ADC / workload identity. Same
per-TB-scanned cost caveat as Athena for interactive rendering.

- **Effort if promoted:** **M** — mirror the Snowflake provider (`GEOGRAPHY`
  default, `ST_AsBinary` WKB out, narrow `Where` parser, gated integration
  tests) plus a GCP credential connection driver.
- **Decision:** **Keep parked.** The blocker is **GTM, not feasibility** — the
  current motion is AWS/Azure customer-operated and "no Google support now"
  (CLAUDE.md repo stance + #951). Promote only on a named buyer / partnership;
  if promoted, split into its own implementation ticket with official-source
  review and service-publishing acceptance criteria per #951.

## Recommendation summary + next steps

1. **Redshift / Snowflake / Databricks:** already delivered as read-only
   providers — no new build. Track only demand-gated hardening (statistics,
   per-dialect `ISqlFilterTranslator`, edits, native output formats) as
   GA/Enterprise follow-ons.
2. **Athena:** defer; build-ready M-slice, file a GA/Enterprise follow-on gated
   on a buyer wanting S3+Glue export/analysis publication (not live rendering).
3. **Azure Data Explorer:** park / docs-only — KQL mismatch, large effort.
4. **Azure Stream Analytics:** no connector; fold any interest into the
   event/streaming lane (#357) as an upstream producer.
5. **Azure SQL Database:** document that it is served by the existing SQL Server
   provider; optional Entra-ID auth follow-on.
6. **BigQuery:** keep parked (GTM). Feasible M-slice mirroring Snowflake; split
   into its own ticket only if a named buyer pulls it forward.
7. **Cross-cutting GA hardening (applies to all warehouse providers):** register
   per-dialect `ISqlFilterTranslator`s so CQL2/FES/OData `$filter` push-down and
   richer predicates work beyond the narrow canonical `Where` grammar. This,
   plus the interactive-rendering cost gate, is the real shared limiter — worth
   one umbrella ticket rather than per-provider patches.

GTM stance preserved: no new Google/Snowflake *commitment* is created here (the
Snowflake read slice already exists independently of this research), and no
generic big-data expansion is recommended.

## Sources

Official docs reviewed (issue #950 source seeds):
- Amazon Athena geospatial queries / examples — docs.aws.amazon.com/athena
- Amazon Redshift spatial functions — docs.aws.amazon.com/redshift
- Azure Data Explorer (Kusto) geospatial / product page — azure.microsoft.com, learn.microsoft.com
- Azure Stream Analytics geospatial functions — learn.microsoft.com/azure/stream-analytics
- BigQuery GIS, Snowflake geospatial — cloud.google.com/bigquery, docs.snowflake.com

Codebase grounding: ADR-0025 (multi-provider operation architecture),
ADR-0035 (provider-ready data source binding); provider seam under
`src/Honua.Core(.Abstractions)/Features/FeatureStore/`; shipped warehouse
providers `src/Honua.Redshift/`, `src/Honua.Snowflake/`, `src/Honua.Databricks/`
and their docs under `docs/reference/configuration/data-sources/`.
</content>
</invoke>
