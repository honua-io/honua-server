# ADR-0064: ImageServer Photogrammetric Tie-Point and 3D Measurement Analytics

## Status

Accepted (2026-07)

## Context

The ImageServer compatibility surface already ships Basic map-space mensuration
(#2734), DEM-backed height mensuration ([#1879](https://github.com/honua-io/honua-server/issues/1879)),
orientation-ranked `find` ([#1880](https://github.com/honua-io/honua-server/issues/1880)),
and image-coordinate-system `project` warps ([#1881](https://github.com/honua-io/honua-server/issues/1881)).
The remaining ArcGIS sensor-model analytics — `computeTiePoints`, shadow /
photogrammetric 3D height mensuration, and volume (`calculateVolume`) — are a
distinct advanced-analytics capability with materially different metadata,
compute, and operational requirements than the parity work already landed
([#2667](https://github.com/honua-io/honua-server/issues/2667), parent
[#2636](https://github.com/honua-io/honua-server/issues/2636)).

The central tension is `computeTiePoints`. A faithful ArcGIS `computeTiePoints`
runs **automatic feature detection + descriptor matching + RANSAC** across
overlapping images to *derive* tie points where none were previously known. That
is research-grade computer vision and requires a heavy CV dependency
(OpenCV/Emgu/SIFT/ORB). This repository's dependency policy sanctions **GDAL as
the only raster dependency**, and GDAL does not provide feature detection or
descriptor matching. ADR-0057 already established the boundary that heavy /
ML-shaped image analysis is **cloud-delegated, never bundled**, and that native
heavy GP leans on the deployed GDAL worker rather than new numerical
dependencies. Vendoring a CV stack to synthesize tie points would violate both.

We therefore need a decision that (a) exposes the documented operation contract
honestly, (b) returns real results only where the raster metadata genuinely
supports them **without feature matching**, and (c) refuses cleanly — never
fabricates — where it does not. This mirrors the DEM-height precedent (#1879),
which returns a real differenced height only when a DEM is modeled and otherwise
returns an actionable `501`.

This ADR records the decisions for the whole epic (#2667). The **first
implementation slice shipped the ADR plus `computeTiePoints`**; a **second slice
adds shadow / 3D height mensuration and `calculateVolume`** (§7, §8), completing
the epic's sensor-model operations.

## Decision

### 1. Supported inputs (no feature matching)

Photogrammetric analytics consume only **pre-existing, pre-registered**
georeferencing metadata already attached to the raster. Nothing is *derived* by
image analysis.

- **Pre-registered control points / ground control points (GCPs).** Tie points
  are returned **only** when the raster carries pre-registered control points
  that pair an image (pixel sample/line) location with a reference (ground /
  map) location. These are read from the `raster_sensor_metadata`
  exterior-orientation payload (see §3) — the payload block that carries the
  control the orientation was established from.
- **RPC (Rational Polynomial Coefficients).** Consumed as the **first-order
  (offset/scale affine)** image↔ground model only, consistent with the existing
  `ImageServerSensorModel` / `RpcModel` documentation (#1881). The full
  80-coefficient polynomial correction remains deferred. RPC is *not* used to
  invent tie points; it is available to the follow-up 3D-mensuration slice.
- **DEM source.** The existing `raster_sensor_metadata.dem_source` (layer id or
  named source) backs base/top elevation differencing for the height/3D
  follow-up (already used by #1879).
- **GDAL GCPs attached to the raster.** GDAL exposes per-dataset GCPs
  (`GetGCPs()`) distinct from the affine geotransform. These are a natural
  second honest source for tie-point pass-through, but GCPs are **not currently
  surfaced on `RasterInfo`** (only the affine `GeoTransform` is). Surfacing GDAL
  GCPs through the raster store is a documented follow-up seam; this slice reads
  control points from `raster_sensor_metadata` only. Documented here so the
  follow-up does not re-litigate the contract.

### 2. Dependency boundary — automatic feature-matching is OUT OF SCOPE

Automatic feature-detection/descriptor-matching/RANSAC tie-point computation is
**explicitly out of scope and will not be implemented in-process**. No new
computer-vision dependency (OpenCV, Emgu, SIFT/ORB, or equivalent) is added to
the server or SDKs. This is a hard boundary, not a deferral of convenience:
- It is consistent with the GDAL-only raster-dependency rule and with ADR-0057
  (heavy/ML image analysis is cloud-delegated, never bundled; no GPU/model
  runtime in the baseline image).
- If automatic matching is ever sanctioned, it must arrive as a **cloud-delegated
  GP lane** behind a provider-pluggable interface (per ADR-0057), surfaced
  through the async job envelope (§4), and it does not change the synchronous
  pass-through contract defined here.

The documented Esri tuning parameters that only govern automatic matching
(`minRegionSize`, `maxLevel`, `skipFactor`, `searchSize`, `similarity`) are
**accepted for wire compatibility but have no effect**, because no matching is
performed. This is documented in the operation response path and here so clients
are not misled into thinking they tune a matcher.

### 3. Exterior-orientation / control-point schema

`raster_sensor_metadata` keeps interior/exterior orientation and RPC as
extensible raw JSONB (per migration `060_AddRasterSensorMetadata.sql`), so no new
column or migration is introduced in this slice. Pre-registered control points
live inside the **exterior-orientation** payload under a `controlPoints` array
(aliases `tiePoints`, `gcps` also accepted). Each entry pairs an image point with
a reference point:

```json
{
  "controlPoints": [
    {
      "imagePoint":     { "x": 512.0, "y": 384.0 },
      "referencePoint": { "x": -117.161, "y": 32.716, "z": 104.2,
                          "spatialReference": { "wkid": 4326 } }
    }
  ]
}
```

- `imagePoint` (aliases `sourcePoint`) is in the source raster's image/pixel
  space: `x` = sample/column, `y` = line/row. It carries no spatial reference
  (pixel space).
- `referencePoint` (aliases `targetPoint`, `groundPoint`) is the ground/map
  location: `x`, `y`, optional `z`, optional `spatialReference` (defaults to the
  raster SRID).
- Entries missing either point, or with non-numeric coordinates, are skipped
  defensively; a payload that parses to zero valid pairs is treated as "no
  control points" (→ honest `501`, §5).

**Decision note (wire ambiguity):** the Esri Enterprise REST reference documents
the `computeTiePoints` response as a `tiePoints` object with parallel
`sourcePoints[]` / `targetPoints[]` arrays (each point `{x, y, spatialReference}`),
rather than a per-point `{imagePoint, referencePoint}` pairing. We adopt the
**documented dual-array response shape** (source = image points, target =
reference/ground points, index-aligned). The `{imagePoint, referencePoint}`
pairing is used only for the *stored* control-point schema, where pairing must be
explicit. This choice is called out in the PR.

### 4. Synchronous vs job execution

`computeTiePoints` in this slice is **bounded and synchronous**: it is a
pass-through of already-stored control points with no heavy compute, so it
returns inline like `measure`/`project`. It does not create a job.

The async `/rest/services/{id}/ImageServer/jobs/{jobId}` lifecycle envelope
already used by durable `exportTiles` (#2707) is the designated **seam** for any
future heavy path (e.g. cloud-delegated automatic matching, or scene-scale 3D
mensuration). Synchronous processing of full scenes is a non-goal (#2667). If a
future capability needs unbounded compute, it adapts to the canonical durable-job
runtime rather than blocking a request thread.

### 5. Honest-error contract

The operation never fabricates tie points and never leaks a `500` for a
parseable-but-unsupported request:

- **No pre-registered control points modeled** (no sensor metadata, or an
  exterior-orientation payload with zero valid control-point pairs) → **`501 Not
  Implemented`** with an actionable message: *"tie-point computation requires
  pre-registered control points / GCPs on this raster; automatic feature matching
  is not supported on this service."* This mirrors the DEM-height `501`
  discipline (#1879, #2795) exactly, so clients can distinguish "unsupported on
  this dataset" from a server fault.
- **Malformed request parameters** (e.g. non-numeric `rasterId`) → **`400`**.
- **Layer / primary raster not found** → **`404`**.
- Under the GeoServices REST convention (PA-070/PA-117), these surface as the
  Esri `{"error":{"code":N,...}}` body (HTTP 200 with body code, or a legacy
  `>= 400` status); `501` passes its code through per #2795.

### 6. Numerical-fixture expectations

- **Tie-point pass-through** is *exact*: emitted `sourcePoints`/`targetPoints`
  must equal the stored control-point coordinates verbatim (no reprojection, no
  interpolation), with `targetPoints` carrying the resolved spatial reference.
  Endpoint tests assert coordinate equality on a supported fixture.
- **Follow-up 3D / volume fixtures** (not in this slice) will assert against
  **independently generated expected results** within a documented tolerance
  (the height path already differences DEM samples to `1e-6`), consistent with
  the epic acceptance criteria.

### 7. Shadow-based height mensuration (second slice)

`esriMensurationHeightFromBaseAndTopShadow` and
`esriMensurationHeightFromTopAndTopShadow` are computed on the existing `measure`
route from the **measured ground shadow length** and the raster's **sun
elevation**: `height = shadowLength · tan(sunElevation)`. Both variants use the
same formula — the operator measures the object-to-shadow-tip length, and the
height follows from the sun elevation above the horizon.

- **Sun geometry source.** Sun elevation (and optional azimuth) is read from the
  `raster_sensor_metadata` **exterior-orientation** JSONB via
  `ImageServerSensorModel.TryReadSunGeometry` — `sunElevation` (aliases
  `sun_elevation`, `sunElevationAngle`, `solarElevation`, `sun_elevation_angle`)
  and `sunAzimuth` (aliases `sun_azimuth`, `solarAzimuth`, `sun_azimuth_angle`).
  A `{x, y, z}` illumination/look vector (`sunDirection` / `illuminationVector` /
  `sunVector`) is a fallback: elevation = `asin(|z| / |v|)`. No migration is
  introduced. The shadow length reuses the existing geodesic/planar distance
  path, so it is a true ground length (not a raw map-unit delta).
- **Honest error.** When no sun geometry is modeled (or the elevation is outside
  the open interval 0°–90°, where `tan` is degenerate), the operation returns the
  same actionable `501` as the DEM-height path — never a fabricated height. Only
  the pure-3D operations (`*3d`, which need full exterior orientation / a stereo
  model) remain gated to `501`.
- **Azimuth** is parsed for completeness but is **not required** for the height,
  because the shadow length is measured directly rather than derived from the
  shadow tip and the sun direction.

### 8. `calculateVolume` (second slice)

Cut/fill volumes are computed against the layer's DEM surface
(`raster_sensor_metadata.dem_source`, the same source the DEM-height path uses),
integrating over the DEM pixels inside each area-of-interest.

- **Wire contract.** Follows the ArcGIS Enterprise REST `Calculate Volume`
  reference (not invented): request `geometries` (array of polygon/envelope AOIs),
  `geometryType`, `baseType`, `constantZ` (base plane, required for `baseType=0`),
  `pixelSize`, `mosaicRule`, `f`; response `{ "results": [ { "area", "cut",
  "fill", "minz", "maxz", "meanz" } ] }` with one entry per AOI. `fill` is
  negative per the Esri convention. There is **no `net` field** in the reference,
  so none is emitted (the net is `cut + fill`).
- **Base plane.** Only **`baseType=0` (constant elevation plane, `constantZ`)** is
  supported. The shared clip primitive returns per-pixel band *values* with no
  pixel positions, so a best-fitting plane (`baseType=1`) or a perimeter-derived
  base (`baseType` 2/3/4) cannot be reconstructed honestly in-process; those
  return an actionable `501`. `pixelSize` is accepted for wire compatibility but
  the integration samples at the DEM's native resolution.
- **Integration.** `cut = Σ_(e>z0)(e−z0)·pixelArea` (material above the base),
  `fill = Σ_(e<z0)(e−z0)·pixelArea` (void below), `area = validPixels·pixelArea`.
  Pixel ground area is `|det|` of the DEM geotransform's linear part (handles
  rotation/skew). Volumes and area are in the DEM's **linear map units** (cubic /
  square meters for a projected metric DEM); a geographic DEM yields degree-based
  units — operators supply a projected DEM for metric results.
- **DEM pixels** are read through the shared `ReadClippedBandVectorsAsync` clip
  primitive (the same one `computeClassStatistics` uses), reusing the raster
  analytics path rather than a parallel reader.
- **Honest error + bound.** No `dem_source` (or a non-layer-id source, or a DEM
  with no usable geotransform/rasters) → `501`. An AOI whose DEM clip exceeds the
  synchronous pixel budget (`GeoServices:ImageServer:CalculateVolume:MaxPixelsPerGeometry`,
  default 4,000,000, mirroring the class-statistics precedent) → actionable `400`
  — never a truncated (wrong) volume or a `500`. The **async-job path for
  unbounded AOIs is deferred** to the durable-job seam (§4), noted here rather
  than built.

## Scope Out (this slice and epic)

- **Automatic feature-matching tie-point computation** — permanently out of the
  in-process engine (§2); only ever a future cloud-delegated lane.
- **Pure-3D height mensuration** (`esriMensuration*3d`) — still `501`; needs full
  exterior orientation / a stereo model. Shadow-based height (`*Shadow`) is now
  implemented (§7).
- **`calculateVolume` base surfaces other than the constant plane** (`baseType`
  1–4) and the **async volume job path** — deferred (§8).
- **Surfacing GDAL dataset GCPs on `RasterInfo`** — follow-up seam (§1).
- **Full RPC polynomial correction** — deferred (first-order affine only).
- **Unbounded synchronous scene processing** — non-goal; use the durable-job
  seam (§4).

## Consequences

- The documented `computeTiePoints` operation is available and honest: real
  results where control points exist, a clear actionable `501` where they do not,
  never a fabricated or CV-derived answer.
- No CV dependency enters the dependency graph; the GDAL-only raster boundary and
  ADR-0057 hold.
- Reusing the existing `raster_sensor_metadata` exterior-orientation JSONB avoids
  a schema migration in this slice; the control-point schema is documented here
  so producers (import/registration paths) have a stable target.
- The response adopts the documented Esri dual-array shape; clients that expected
  a per-point pairing must read the index-aligned arrays. The divergence between
  stored schema (paired) and wire schema (dual-array) is deliberate and
  documented.
- Follow-up slices (3D mensuration, volume, any future matching) inherit a
  settled input model, error contract, and execution boundary, so they add
  computation rather than re-deciding the boundary.

## References

- [ADR-0057: Geoprocessing Capability Boundaries](0057-geoprocessing-capability-boundaries.md)
  (heavy/ML image analysis cloud-delegated, never bundled; GDAL-only native GP)
- [ADR-0018: Source-Generated JSON Serialization](0018-source-generated-json-serialization.md)
- [ADR-0011: Testing Strategy and API Surface Coverage](0011-testing-strategy.md)
- [ADR-0054: Evidence-Based Feature Catalog](0054-evidence-based-feature-catalog.md)
- honua-io/honua-server#2667 (this epic), #2636 (parent), #1879 (DEM height),
  #1880 (orientation-ranked find), #1881 (image-CS project / RPC first-order),
  #2707 (durable job envelope), #2795 (501 pass-through)
- Esri ArcGIS REST reference: Compute Tie Points (Image Service) —
  `developers.arcgis.com/rest/services-reference/enterprise/compute-tie-points/`
