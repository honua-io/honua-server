# Metadata v2 Extensions Vocabulary Specification

Formal specification of the namespaced `Extensions` keys used on V2 graph
entities. Documents what each key means, who consumes it, and what its content
must look like.

Companion to:
- [ADR-0040](../adr/0040-metadata-v2-canonical-graph.md) — design rationale
- [metadata-v2-crosswalk.md](metadata-v2-crosswalk.md) — concept inventory
- [metadata-v2-mapping.md](metadata-v2-mapping.md) — field-level mapping spec

## Purpose

V2 graph entities each carry an `Extensions: IReadOnlyDictionary<string, JsonElement>`
slot to hold **format-specific** metadata that doesn't qualify for a typed
top-level slot (per the promotion criteria in ADR-0040 §"Principle 2").

To keep `Extensions` from becoming a junk drawer:

1. Every key must use one of the **registered namespace prefixes** below.
2. Each namespace has a documented owner, shape, and consumer.
3. Adding a new prefix requires updating this spec.

## Entity-level extensions slots

| Entity | Slot path | Owner |
|---|---|---|
| Graph | `Graph.Extensions` | server-wide config extensions |
| Resource | `Resource.Extensions` | per-resource opaque metadata |
| Service | `Service.Extensions` | per-service opaque metadata |
| Service | `Service.Options` | per-service operator-set knobs (typed-ish via convention) |
| Publication | `Publication.Extensions` | per-publication opaque metadata |
| Publication | `Publication.Options` | per-publication operator-set knobs |
| StorageBinding | `StorageBinding.Extensions` | per-binding opaque metadata |
| StorageBinding | `StorageBinding.Options` | per-binding operator-set knobs (provider-specific) |
| Connection | `Connection.Extensions` | per-connection opaque metadata |
| Connection | `Connection.Options` | per-connection provider-specific config |
| Field | `Field.Extensions` | per-field opaque metadata |

**`Options` vs `Extensions` convention**:
- **`Options`**: operator-facing configuration knobs that the producer of the
  graph (admin UI, import tool) sets and the runtime reads. Typed-ish via
  documented per-provider conventions (e.g. `Connection.Options.poolSize`).
- **`Extensions`**: opaque metadata attached to an entity by tools / projections,
  not directly edited by operators. Read by render-time projectors.

If a feature blurs the line, prefer `Extensions` (lower expectation of operator
edit).

---

## Registered namespaces

### `stac` — STAC ecosystem

| Key | Location | Shape | Consumer |
|---|---|---|---|
| `stac` | `Resource.Extensions["stac"]` | Object containing STAC collection-level extras (license is now on Metadata; the rest stays here) | STAC handler (`StacCollectionsTests.GetCollection`) |
| `stac:eo` | `Resource.Extensions["stac:eo"]` | EO extension object: `{ "bands": [{ name, common_name, center_wavelength, ... }], "cloud_cover": number }` | STAC handler when emitting collection summaries |
| `stac:sar` | `Resource.Extensions["stac:sar"]` | SAR extension: `{ instrument_mode, frequency_band, polarizations[], product_type, ... }` | STAC handler |
| `stac:view` | `Resource.Extensions["stac:view"]` | View extension: `{ sun_azimuth, sun_elevation, off_nadir, incidence_angle, azimuth }` | STAC handler |
| `stac:proj` | `Resource.Extensions["stac:proj"]` | Projection extension; per-item EPSG override: `{ epsg, wkt2, projjson, bbox, shape }` | STAC item-level projection |
| `stac:raster` | `Resource.Extensions["stac:raster"]` | Raster extension: `{ bands: [{ data_type, nodata, sampling, statistics }] }` | STAC + raster handlers |
| `stac:processing` | `Resource.Extensions["stac:processing"]` | Processing extension: `{ level, lineage }` | STAC handler |
| `stac:version` | `Resource.Extensions["stac:version"]` | Versioning extension: `{ version, deprecated }` | STAC handler |
| `stac:mlm` | `Resource.Extensions["stac:mlm"]` | ML Model extension (vendor-extension) | STAC handler |
| `stac:web-map-links` | `Resource.Extensions["stac:web-map-links"]` | Web map links extension | STAC handler |

**Reason for namespace**: STAC extensions are an open and fast-moving vocabulary
(individual extensions live in their own repos and version independently).
Typing every STAC extension would chase a moving target.

**Conventions**:
- Use the STAC extension's own JSON shape verbatim under each sub-key.
- `stac` (no suffix) is the catch-all for collection-level STAC-specific fields
  that don't fit a typed extension (e.g., legacy/non-standardized fields).

---

### `connection-*` — connection provider-specific options

These live on `Connection.Options` (not Extensions), but follow a namespaced
convention to keep providers from colliding:

| Key prefix | Provider | Example fields |
|---|---|---|
| `postgres-*` | Postgres / PostGIS | `postgres-search-path`, `postgres-application-name`, `postgres-pool-size`, `postgres-prepared-statement-cache-size` |
| `mysql-*` | MySQL / MariaDB | `mysql-ssl-mode`, `mysql-pool-size` |
| `s3-*` | S3 / S3-compatible | `s3-region`, `s3-endpoint-url`, `s3-path-style`, `s3-multipart-threshold` |
| `duckdb-*` | DuckDB | `duckdb-memory-limit`, `duckdb-threads` |
| `stac-*` | STAC API | `stac-api-version`, `stac-pagination-style` |

---

### `sample-data` — admin UI sample / preview helpers

| Key | Location | Shape | Consumer |
|---|---|---|---|
| `sample-data` | `Resource.Extensions["sample-data"]` | `{ sampleFeatures: [...], sampleBboxes: [...], computedAt }` | Admin UI preview pane (cached preview to avoid hitting the storage) |

Optional — set by an admin-side job that scans the storage for representative
samples to show in the UI. Not consumed by external protocol handlers.

---

## Reserved future namespaces

Reserved (not yet in use) — documented to prevent collision:

| Prefix | Intended use |
|---|---|
| `dcat-*` | DCAT-specific catalog metadata (when Records cluster needs it) |
| `iso-19115-*` | ISO 19115 metadata typed fields (alternative to `iso19139` XML blob) |
| `cog-*` | Cloud-Optimized GeoTIFF-specific metadata beyond `geotiff-tags` |
| `zarr-*` | Zarr-specific multi-array metadata |
| `netcdf-cf-*` | NetCDF CF-convention metadata |
| `cmr-*` | NASA CMR (Common Metadata Repository) sync metadata |

---

## How to add a new namespace

1. **Check the [promotion criteria in ADR-0040 §"Promote L2→L1"](../adr/0040-metadata-v2-canonical-graph.md#promote-l2--l1-criteria)**.
   If your concept maps to ≥2 standards / consumers, propose a typed slot
   instead.
2. **Pick a stable namespace**. Prefer protocol-format-name dashes:
   `stac-foo`, never freeform names.
3. **Document in this file**: add a row to "Registered namespaces" with the
   key, location, shape, and consumer.
4. **Update consumers**: cluster `Map*ResponseV2` builder reads from the new
   key.

## Validation

`MetadataV2GraphValidator` does not enforce Extensions key conventions today —
it would risk false positives on legitimately-unknown extensions. The convention
is enforced via:

- This spec document (review-time enforcement).
- Code review checklist for new Extensions keys: "Is this prefix registered in
  `metadata-v2-extensions.md`?"
- Periodic CI architecture test (future): scan for `Extensions["..."]` reads
  in cluster builders and check the key matches a registered prefix.

## Cross-references

- [ADR-0040](../adr/0040-metadata-v2-canonical-graph.md) — design rationale
- [metadata-v2-crosswalk.md](metadata-v2-crosswalk.md) — what's in V2 typed
  slots vs. Extensions vs. derived
- [metadata-v2-mapping.md](metadata-v2-mapping.md) — field-level mapping
- [metadata-v2-admin-input-model.md](metadata-v2-admin-input-model.md) —
  operator-input vs calculated classification (most extensions are not
  operator-edited)
