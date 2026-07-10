# Temporal Animation API

**Status:** GA (`Implemented`) since honua-io/honua-server#2429. Promoted from the
experimental capability set per ADR-0058 / ADR-0059.

This document is the contract for Honua's temporal (time-aware) query surfaces:
time filtering, temporal-extent discovery, date-bin histograms, time-series
tiles, and the client-side animation/playback contract they back. It is the
authoritative reference cited by the temporal handlers and their tests.

## Capabilities and edition split

Temporal analytics is exposed through four capabilities. The Community/Pro split
is enforced by license entitlements (`FeatureCatalog`), independent of the GA
maturity flag — GA means "no longer hidden/unadvertised," not "free."

| Capability | Surface | Edition |
|---|---|---|
| `temporal.filtering` | Time-range filtering of feature queries (`time=`, OGC `datetime=`, STAC, OData, WMS/WMTS `TIME`) | Community |
| `temporal.extent-discovery` | `timeInfo`, the dedicated `temporalExtent` endpoint, and OGC time-dimension metadata | Community |
| `temporal.histogram` | `queryDateBins` date-bin counts for animation frame planning | Pro |
| `temporal.time-series-tiles` | Time-filtered vector tiles (`?time=`) | Pro |
| `temporal.animation-api` | Capability flag for SDK/admin TimeSlider + playback integration | Pro |

## Opt-in time-awareness (strict)

A layer is temporal only when it explicitly declares a start-time field in its
metadata (`timeInfo.startTimeField`, optionally `endTimeField` / `trackIdField`),
resolving to a `Date` / `DateTime` schema field. Discovery and filtering never
fall back to scanning for an arbitrary date column.

Honua deliberately **rejects** a non-empty time filter against a layer that is
not time-aware with **HTTP 400**, rather than silently ignoring it. This is an
intentional divergence from Esri's lenient "ignore time" behavior (issue #1444):
clients learn the layer has no temporal dimension instead of receiving a full,
unfiltered result they believe is time-scoped.

## Time parameter grammar

The GeoServices `time=` parameter accepts:

- `time=<start>,<end>` — a bounded interval. Each bound is a Unix epoch
  milliseconds value **or** an ISO-8601 date-time.
- `time=<start>,null` / `time=null,<end>` — an open-ended interval.
- `time=null,null` — a documented **no-op**: parsed as valid and treated exactly
  like omitting the parameter (so animation clients can hold a constant parameter
  slot).
- `time=<instant>` — a single value; treated as `start == end`.

An inverted interval (`start > end`) and any unparseable value are rejected with
**HTTP 400**. Interval semantics are inclusive intersection: a feature matches
when its `[startTimeField, endTimeField]` (using `endTimeField`, else
`startTimeField`) overlaps the requested window.

OGC API Features (`datetime=`), STAC (`datetime`), OData time-window filters, and
WMS/WMTS `TIME` map onto the same `TemporalFilter` and honor the same opt-in and
interval rules.

## Endpoints

All paths below are relative to a published GeoServices FeatureServer layer
(`/rest/services/{serviceId}/FeatureServer/{layerId}`):

- `GET .../query?time=<start>,<end>` — time-filtered feature query
  (`temporal.filtering`, Community).
- `GET .../temporalExtent` — min/max time extent and the configured temporal
  fields for the layer (`temporal.extent-discovery`, Community). Returns 404 when
  the layer is not time-aware.
- `GET .../queryDateBins?binField=<field>&bin=<json>[&outStatistics=<json>][&where=<sql>]`
  — per-bucket counts (and optional statistics) using calendar (`date_trunc`) or
  fixed-interval bins, for animation frame planning (`temporal.histogram`, Pro;
  402 without the Pro entitlement).
- `GET .../tiles/{layerId}/{z}/{x}/{y}.mvt?time=<start>,<end>` — time-filtered
  vector tiles (`temporal.time-series-tiles`, Pro).

Temporal data-history (`/api/v1/temporal/*`: as-of / diff / timeline / rollback,
#1166) reuses the `temporal.filtering` capability gate but is a separate feature
documented in `docs/internal/design/temporal-data-history.md`.

## Provider support

Temporal SQL translation exists only where a provider can express interval
predicates and bin aggregation. Providers that cannot translate a temporal
predicate **fail loud** with `NotSupportedException` (surfaced as a protocol
error) — they never silently return rows outside the requested window.

| Provider | Filtering | Extent | Histogram | Time-series tiles |
|---|---|---|---|---|
| PostgreSQL / PostGIS | Yes | Yes | Yes | Yes |
| DuckDB | Yes | Yes | Yes | No (no `ST_AsMVT`) |
| MySQL / MariaDB | No (rejects) | No (rejects) | — | — |
| SQL Server | No (rejects) | No (rejects) | — | — |
| Oracle | No (rejects) | No (rejects) | — | — |
| Snowflake | No (rejects) | No (rejects) | — | — |
| Redshift | No (rejects) | No (rejects) | — | — |
| Databricks | No (rejects) | No (rejects) | — | — |

"Rejects" means the query builder throws `NotSupportedException` when a temporal
filter is present, so a mis-routed request returns a clear error rather than
unfiltered data. Use a PostgreSQL/PostGIS-backed layer (or DuckDB, except for MVT
tiles) for temporal workloads.

## Animation / playback

Clients drive playback by: (1) discovering the layer extent via `temporalExtent`,
(2) planning frames with `queryDateBins`, then (3) requesting each frame with a
bounded `time=` window against `query` or the vector-tile endpoint. The
`temporal.animation-api` capability advertises this contract to the SDK/admin
TimeSlider; execution is the same time-filter pipeline described above.
