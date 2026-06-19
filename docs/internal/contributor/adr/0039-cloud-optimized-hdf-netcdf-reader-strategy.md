# ADR-0039: Cloud-Optimized HDF5 / NetCDF4 Reader Strategy

## Status

Accepted. **Path selection resolved (2026-06-18, issue #1756): Path B — GDAL
sidecar NetCDF→Zarr conversion.** See "Path Selection (#1756)" below.

## Context

GitHub issue #1010 asks for MVP read-only cloud-optimized HDF5 / NetCDF4
coverage support flowing through the canonical raster/coverage pipeline
(`Honua.Core.Features.Raster`, `OGC API Coverages`, `ImageServer`, `STAC`).

The current cloud-optimized raster surface (`CloudOptimizedGeoTiff`, see
`src/Honua.Core/Features/Raster/CogParser/CogMetadataExtractor.cs`) is built on
an AOT-safe **pure managed** parser that walks IFDs through
`ICloudRangeReader` HTTP range requests. The server is published with
`<PublishAot>true</PublishAot>` and `Directory.Packages.props` does not
reference GDAL, libhdf5, libnetcdf, or any HDF5 wrapper. ADR-0034 explicitly
ships a GDAL/OGR `HONUA:` driver **out-of-tree** in a separate `honua-gdal`
repository; the server itself does not link or load GDAL natively.

HDF5 / NetCDF4 readers in .NET fall into four buckets:

1. **`HDF5.PInvoke` / `HDF.PInvoke.1.10`** – managed bindings over native
   `libhdf5`. Requires shipping `libhdf5.so` / `hdf5.dll` per platform, breaks
   trimming/AOT, and the HDF5 C library is not range-request aware – it expects
   a local file or its own ROS3/HSDS plugin chain. Pulling that in regresses
   the AOT posture and the no-native-deps stance the existing COG reader was
   built to preserve.
2. **`SharpHDF5` / community pure-managed forks** – none are production-ready,
   none support NetCDF4 enhanced model, and the codebases are unmaintained.
3. **GDAL out-of-tree** – matches ADR-0034. The `HONUA:` GDAL driver in
   `honua-gdal` is the supported path for desktop GIS / Python / R clients to
   read HDF/NetCDF *through* honua. The driver does not run in-process in
   `honua-server`.
4. **Bespoke pure-managed HDF5 superblock walker constrained to a
   CF-compliant NetCDF4 layout** – feasible in principle (HDF5 superblock +
   B-tree + chunked dataset traversal is documented), but the implementation
   surface is large (B-tree v1/v2, fractal heaps, fill values, filter
   pipelines, datatype conversion, CF coordinate variable interpretation,
   `_CoordinateAxisType`, time / vertical units, `grid_mapping` discovery).
   That is not deliverable in the MVP without compromising on the
   AOT/range-read contract that makes the COG path safe.

## Decision

**The MVP for issue #1010 ships the *server-side registration, validation,
domain model, and protocol surface* for cloud-optimized HDF5 / NetCDF4
coverage sources. It does *not* ship an in-process HDF5 reader in
`honua-server`. Metadata extraction and bounded subset reads are delivered
via a follow-up split that selects exactly one of the two viable paths
below.**

The two viable follow-up paths (one must be picked before split 3 lands):

- **Path A — Bespoke pure-managed NetCDF4 reader.** Constrain to NetCDF4 /
  HDF5 superblock v2/v3, chunked storage, deflate/shuffle filters,
  CF-1.x + COARDS coordinate convention, and a documented "cloud-optimized"
  layout where the global heap and chunk B-tree are clustered near the end of
  the file (the convention emerging from `nccopy --chunked --shuffled`
  outputs and from the H5coro/`s3fs+xarray` community). Stays AOT-safe and
  range-read driven; matches the COG reader posture. Maintenance cost is
  real but bounded by the strict scope.
- **Path B — Delegate to `honua-gdal` over a side-channel.** Same out-of-tree
  GDAL driver that ADR-0034 ships, but invoked by honua-server through a
  sidecar process (`ogrinfo --json`, `gdalmdiminfo --json`,
  `gdal_translate -of NetCDF -projwin ...`) over a controlled binary contract.
  No native code in the server, no AOT regression. Higher operational
  complexity (sidecar deployment, container layering) but no new pure-managed
  parser to maintain.

Path selection is deferred to the next ticket (#1010 follow-up:
`feat: cloud-optimized HDF/NetCDF metadata extraction`). The MVP shipped in
this PR is **path-neutral**: the registration record carries provider /
bucket / object key / declared variable names, the metadata extraction
service is wired through an interface (`IMultidimensionalCoverageMetadataReader`)
that either path can implement, and the default registration in
`AddMultidimensionalCoverageServices` registers a `NotEnabled` reader that
returns 501 Not Implemented with a stable problem code. Operators register
sources today; they read pixels once split 3 lands.

## Consequences

### Positive

- Preserves the server's AOT/no-native-deps posture established by the COG
  path until a deliberate decision is recorded for the reader.
- Lets catalog/admin/STAC integration ship now, unblocking downstream UI
  work, scene/catalog metadata, and the protocol adapter contract.
- Reuses existing `ICloudRangeReader`, `CloudStorageProvider`, and admin
  authorization / validation helpers — no new infrastructure leaks.
- Aligns with ADR-0034: GDAL stays out-of-tree; the server stays AOT.

### Negative

- Operators cannot read pixels in this MVP — only register sources and see
  catalog entries. The contract surfaces this with a stable 501 + problem
  code (`HONUA-COV-HDF-READER-NOT-ENABLED`).
- The Zarr work in #1009 will land in parallel with a similar shape. A
  shared `MultidimensionalCoverage*` abstraction may have to be re-cut once
  both readers exist. Kept the abstraction surface intentionally small (one
  interface, one record shape) so the refactor is mechanical.

### Non-goals (recorded explicitly)

- No COPC / point-cloud support.
- No general HDF5 group browsing unrelated to geospatial coverage.
- No whole-file download default path. The catalog stores the source
  reference; bytes flow only on bounded subset requests.
- No server-side scientific analysis (CDO / `xarray.compute`).
- No protocol-specific HDF5 readers that bypass the canonical raster
  pipeline.

## Path Selection (#1756)

**Decision (2026-06-18): Path B — GDAL sidecar NetCDF→Zarr conversion.**

Issue #1756 is the follow-up this ADR deferred the path choice to. After
evaluating both options against the current codebase, **Path B is selected**:

NetCDF/HDF5 sources are converted to a Zarr store by the out-of-tree GDAL
worker (`Honua.Worker.Gdal`) at metadata-scan time, and then served through
the **existing** pure-managed Zarr reader (`ZarrMetadataExtractor` /
`ZarrSubsetReader`). The server gains no native dependency; the GDAL worker
that already exists does the format work.

Evidence that decided it:

- **Server AOT posture preserved.** Path A would put a large bespoke HDF5
  binary parser (superblock v2/v3 — confirmed on a real NetCDF4 fixture —
  plus chunk B-tree, filter pipeline, dimension-scale/`DIMENSION_LIST`
  resolution, CF interpretation) into `Honua.Core`. Path B keeps all native
  format handling in the worker image, exactly per ADR-0034.
- **Reuses proven infrastructure.** The job-executor dispatch pattern
  (`GdalDispatchJobExecutor`) and the Zarr metadata+subset reader already
  work end-to-end. Path B is a new GDAL executor plus mapping glue, not a new
  parser.
- **Bounded scope / risk.** HDF5 correctness is owned by GDAL upstream.

Accepted cost (the "container layering" complexity this ADR anticipated):

- **The GDAL worker image must move from `osgeo/gdal:ubuntu-small-*` to
  `ubuntu-full-*`.** Verified 2026-06-18: the `ubuntu-small` image does **not**
  ship the NetCDF/HDF5/Zarr drivers (`gdalmdiminfo` reports "not recognized as
  being in a supported file format"); `ubuntu-full` does. This enlarges the
  worker image and is a deployment change tracked with the worker Dockerfile
  update.
- A derived Zarr copy is produced per registered source (storage cost +
  staleness on source change; re-scan re-converts).
- The coverage metadata-scan path now depends on the GDAL worker being
  available. The serverless/demo profile must account for this (the demo
  cube for devops#101 is batch-seeded, so the convert runs at seed time, not
  on the browse path).

Implementation shape: a `gdalmdiminfo --json` → `MultidimensionalCoverageMetadata`
mapper (pure, in `Honua.Core`), a `GdalNetCdfToZarrConvertJobExecutor` in the
worker (runs `gdalmdiminfo` + `gdal_translate -of Zarr`), registered in
`GdalDispatchJobExecutor`; the refresh/scan endpoint dispatches the job and the
result populates the registration metadata. `raster.multidim-coverage` Pro
entitlement gates the surface.
