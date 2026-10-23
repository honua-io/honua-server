# Cloud-Native-Geospatial (CNG) Conformance Results

**Execution Date**: Wed Sep 16 03:31:48 UTC 2026
**Honua Server Version**: af22624
**gpq Validator Pin**: v0.24.0 (`github.com/planetlabs/gpq/cmd/gpq`)
**go-pmtiles Validator Pin**: v1.30.0
**3D Tiles Validator Pin**: 0.6.1

## Per-format results

| Format | Source | Validator | Result | Detail |
|---|---|---|---|---|
| GeoParquet 1.1.0 | FeatureServer `f=parquet` | `gpq validate` | PASS | gpq validate passed (FeatureServer f=parquet) |
| FlatGeobuf | FeatureServer `f=fgb` | `ogrinfo -al -so` | PASS | ogrinfo read-back succeeded (FeatureServer f=fgb) |
| PMTiles v3 | `PMTilesWriter` | `pmtiles verify` | PASS | pmtiles verify passed (honua PMTilesWriter) |
| 3D Tiles 1.1 | `TilesetDocumentWriter` + `GeometryTileBuilder` | `3d-tiles-validator` + `gltf_validator` | PASS | 3d-tiles-validator passed (honua tileset + GLB, 0 errors) |
| COG / Zarr consumer | `CogTiffTileEncoder` + `ZarrSubsetReader` | range-read accounting (value oracles in validate-canonical-artifacts.py) | PASS | honua transcoded honua.cog.tif and decoded a Zarr subset over 8 range request(s), 0 whole-object downloads; value oracles graded by validate-canonical-artifacts.py |

## Consumer-format validation

COG and Zarr are consumer surfaces: Honua reads them, it does not publish them.
The canonical COG and Zarr files this lane generates are therefore third-party
*inputs*. Honua reads them back through `CogMetadataExtractor` and
`ZarrSubsetReader` over HTTP range requests, and the canonical clients grade
what Honua emitted — `honua.cog.tif` and the decoded Zarr subset recorded in
`honua-consumer-evidence.json` (honua-server#4398).

- **COG** — exportImage emits plain GeoTIFF; `format=cog` is rejected. The
  certification cells validate the transcoded tile, not the input fixture.
- **Zarr / GeoZarr** — read/transcode only; the certified cell is the decoded
  subset, checked against the fixture's declared formula and xarray's own slice.
- **HDF5 / netCDF** — input-only diagnostic; no Honua-produced artifact, so its
  cells cannot pass.
- **COPC** — read/transcode only.
