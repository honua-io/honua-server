# Cloud-Optimized HDF5 / NetCDF4 Coverage Support

Status: MVP — registration, validation, catalog. Metadata extraction and
bounded subset reads are gated on the reader strategy decision recorded in
[ADR-0039](../contributor/adr/0039-cloud-optimized-hdf-netcdf-reader-strategy.md).

This document is the operator-facing contract for issue #1010.

## Support matrix

| Capability | Status | Notes |
|---|---|---|
| Register cloud-hosted source | Supported | `POST /api/v1/admin/multidim-coverages` |
| List / get / delete registration | Supported | Admin-authenticated |
| AWS S3 source | Supported | Range reader uses existing `AwsS3RangeReader` |
| Azure Blob source | Supported | Range reader uses existing `AzureBlobRangeReader` |
| Local filesystem source | Rejected (400) | Not a cloud-optimized path |
| Cloud-optimized HDF5 (`.h5`, `.hdf5`) | Registration only | Reader follow-up |
| NetCDF-4 (`.nc`, `.nc4`) | Registration only | Reader follow-up |
| Operator-declared variable subset | Persisted | Empty list = "all CF data variables" |
| Metadata refresh | 501 Not Implemented | Stable problem code `HONUA-COV-HDF-READER-NOT-ENABLED` |
| Bounded spatial / temporal / variable subset reads | Deferred | Reader follow-up |
| OGC API Coverages exposure | Deferred | Reader follow-up |
| ImageServer / WMS surfacing | Deferred | Reader follow-up |
| STAC item metadata | Deferred | Reader follow-up |
| Whole-file download path | Out of scope | Non-goal in #1010 |
| HDF5 group browsing (non-coverage) | Out of scope | Non-goal in #1010 |
| Server-side scientific compute | Out of scope | Non-goal in #1010 |

## MVP scope

### Supported extensions

- `.nc`, `.nc4` for NetCDF-4.
- `.h5`, `.hdf5` for cloud-optimized HDF5.

The extension must match the declared `format`. Mismatched pairs (e.g.
`format=NetCdf4` + `granule.tif`) are rejected with 400.

### Layout expectations

The MVP only validates inputs at registration time. Once a metadata reader
is enabled (per ADR-0039), the reader will require:

- Chunked storage layout (no contiguous unchunked datasets — those force
  whole-file reads and will be rejected with `HONUA-COV-HDF-UNSUPPORTED-LAYOUT`).
- HDF5 superblock v2 or v3 (NetCDF-4 default).
- B-tree v1 or v2 chunk indices.
- Compression filters limited to identity, deflate, and shuffle. Other
  filters (szip, bitshuffle, JPEG-LS, custom plugins) will be rejected.
- CF Conventions 1.8 or later for coordinate and CRS discovery
  (`grid_mapping`, `standard_name`, `axis`, `units`, `_FillValue`).
- Latitude / longitude coordinate variables, or projected `x` / `y`
  paired with a `grid_mapping` variable.
- Optional time / vertical CF coordinates surface as `temporal` and
  `vertical` extent metadata.

### Source types

- `provider`: `AwsS3` or `AzureBlob`. `Local` is rejected.
- `bucket`: 1-255 characters, alphanumeric plus `-`, `.`, `_`.
- `objectKey`: 1-1024 characters. Must not start with `/` and must not
  contain `..`. Control characters are rejected.

### Variable selection

- `variables`: 0-64 entries, each 1-255 characters. Permitted characters:
  alphanumeric, `_`, `-`, `.`, `/`. Duplicates are rejected.
- Empty array means "expose every CF data variable discovered when the
  reader scans the object".

### Dimensionality

The reader follow-up will support 2-D, 3-D (time or vertical), and 4-D
(time + vertical) coverage variables. Higher-rank tensors and ragged /
unlimited dimensions are out of scope for the MVP reader.

## Admin API surface

All endpoints require admin authorization and live behind
`/api/v{version}/admin/multidim-coverages`.

| Method | Path | Behavior |
|---|---|---|
| `POST` | `/api/v1/admin/multidim-coverages` | Register a source |
| `GET` | `/api/v1/admin/multidim-coverages?layerId={id}` | List by layer |
| `GET` | `/api/v1/admin/multidim-coverages/{id}` | Get one |
| `DELETE` | `/api/v1/admin/multidim-coverages/{id}` | Unregister |
| `POST` | `/api/v1/admin/multidim-coverages/{id}/refresh` | Scan metadata |

### Register payload

```json
{
  "layerId": 1,
  "name": "ghrsst-l4-daily",
  "description": "GHRSST L4 daily SST",
  "format": "NetCdf4",
  "provider": "AwsS3",
  "bucket": "noaa-sst",
  "objectKey": "ghrsst/2026/05/18.nc4",
  "variables": ["analysed_sst", "analysis_error"]
}
```

### Refresh contract (MVP)

`POST /api/v1/admin/multidim-coverages/{id}/refresh` returns
`501 Not Implemented` with the problem type
`HONUA-COV-HDF-READER-NOT-ENABLED` until a metadata reader is enabled.
Operators can register sources today and ride the upgrade when the reader
ships.

Unsupported layouts (once a reader is enabled) return
`422 Unprocessable Entity` with the problem type
`HONUA-COV-HDF-UNSUPPORTED-LAYOUT`.

## Operational guidance

### Storage classes

These are large multidimensional datasets. Recommended storage class is
`STANDARD` (or `STANDARD_IA` with lifecycle promotion before refresh) to
keep first-byte latency low for range reads. `GLACIER` and `DEEP_ARCHIVE`
will produce timeouts.

### Egress / read shape

The shared raster/coverage pipeline reads HDF5 / NetCDF4 sources with the
same `ICloudRangeReader` used by COG. Operators should expect:

- One range read for the HDF5 superblock plus a small number of B-tree /
  fractal-heap reads per variable touched (under a hundred KB for a
  well-clustered file).
- One range read per chunk that overlaps the requested spatial / temporal
  / vertical subset. Chunk size and overlap are governed by the source
  file's chunk shape, not by the server.

Storage costs scale with the number of refresh operations and the number
of subset requests. They do not scale with object size, because we never
download the full object.

### CRS expectations

The reader will resolve CRS from a CF `grid_mapping` variable. When a
source uses an EPSG that the server does not register, the registration
remains valid (so admin and STAC can still see it) but coverage delivery
will fall back to the unprojected grid until the CRS is added to the
server's CRS registry.

### Concurrency

Refresh holds the catalog row open across multiple range reads. Operators
should avoid concurrent refreshes for the same registration; the
underlying store does not yet take a row-level lock, and a clobbered
metadata write is hard to detect without forensic logging.

### Capacity

Each registration costs one row in `honua.multidim_coverage_catalog`.
Discovered metadata is persisted as JSONB. Operators registering tens of
thousands of granules should monitor JSONB index size and migrate to a
generated extent column if the catalog grows past 10⁵ rows.

## Dependency and deployment

The MVP does not ship a native HDF5 dependency in `honua-server`. The
reader strategy (Path A pure-managed vs Path B sidecar GDAL) is documented
in ADR-0039 and will be selected when the follow-up issue lands.

Until then no additional native packages or container layers are required
to operate the registration surface.

## Follow-on tickets

The MVP ships split #1 + split #2 from issue #1010. The remaining splits:

3. Canonical chunk-based subset reads (selected per ADR-0039 Path A or B).
4. Protocol surfacing — OGC API Coverages collection auto-discovery, STAC
   item generation, ImageServer / WMS hookup, admin catalog UI.
