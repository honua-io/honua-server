# Honua.Benchmarks

BenchmarkDotNet harness for the server hot paths flagged by the pre-release
hardening audit (#1144). Each class is decorated with `[MemoryDiagnoser]`
and a `[BenchmarkCategory(...)]` so allocation regressions are caught
alongside ns/op and runs can be filtered by category.

## Benchmarks

| Class | Category | What it measures |
| --- | --- | --- |
| `TileDecompressorBenchmarks` | `tile` | COG tile DEFLATE decode and JPEG passthrough dispatch. |
| `StacSearchPathBenchmarks` | `stac` | `StacFilterHelpers.ParseBbox` across 2D, 3D, and antimeridian inputs. |
| `OgcParameterParsingBenchmarks` | `ogc` | `OgcTemporalFilterParser.TryParseRange` on instants and open/closed intervals. |
| `Wfs20ParseBenchmarks` | `ogc`, `filter` | `Fes20Parser.ParseFilter` on simple, nested, and spatial WFS filter XML. |
| `GeoJsonSerializationBenchmarks` | `ogc`, `serialization` | Source-generated `FeatureCollection` serialization at 10 / 1k / 10k features. |
| `CacheKeyHashBenchmarks` | `cache` | `MetadataCacheKeyBuilder` Build / BuildKey / fingerprint hot paths plus a batched-burst case. |
| `RasterMosaicBenchmarks` | `raster`, `tile` | `RasterMosaicUtilities.ResolveMergeStrategy` token / JSON paths plus `TileMath` Web Mercator and CRS84Quad bounds. |
| `SpecParserBenchmarks` | `spec` | `SpecParser.Parse` across small, medium (canonical fixture) and large (16 sources, 32 compute steps) documents. |
| `ConnectionPoolBenchmarks` | `connection-pool` | `QueryConcurrencyGate` uncontended WaitAsync/Release fast path, saturate-and-drain burst, and adaptive Release(TimeSpan) controller path. |

## Running

Run with `dotnet run -c Release --project benchmarks/Honua.Benchmarks -- --filter '*'`,
or scope by category, e.g. `--anyCategories tile` or `--anyCategories filter cache`.
Pass `--list flat` to see every benchmark without launching the runners.

## Raster storage protocol

RAST-015 adds a separate environment benchmark command for storage-layout
decisions. It is not a BenchmarkDotNet microbenchmark and none of its adapters
are registered in a production host. The versioned protocol covers monolithic
PostGIS `EXTERNAL`, benchmark-only tiled PostGIS, object COG, hybrid COG/PostGIS,
and object Zarr across alignment, ingest, tile/export/identify/statistics,
mosaic/reproject/surface/zonal, backup/restore/vacuum, and concurrent-tenant
workloads.

Generate the full fixture, metric, threshold, and support matrix:

```powershell
dotnet run -c Release --project benchmarks/Honua.Benchmarks -- `
  raster-storage describe --output artifacts/raster-storage/protocol-v1.json
```

Run the database layouts only against an isolated benchmark database:

```powershell
$env:HONUA_RASTER_BENCHMARK_CONNECTION = '<isolated benchmark connection>'
dotnet run -c Release --project benchmarks/Honua.Benchmarks -- `
  raster-storage run-postgis --fixture small-raster --samples 10 `
  --output artifacts/raster-storage/postgis-small.json
```

Run the currently supported bounded COG tile cell against a short-lived signed
HTTP URL with byte-range support:

```powershell
dotnet run -c Release --project benchmarks/Honua.Benchmarks -- `
  raster-storage run-cog --url '<signed URL>' --fixture large-scene `
  --output artifacts/raster-storage/cog-large.json
```

Validate a result with `raster-storage validate --input <results.json>`.

The matrix deliberately marks unsupported COG, hybrid, and Zarr workload cells
instead of treating a range read or parser as shared-raster-store parity. The
PostGIS adapter records per-query database CPU as unavailable unless an
external sampler supplies it; core PostgreSQL statistics cannot provide that
measurement honestly. See the
[capacity-planning guide](../../docs/guides/deploy/raster-storage-capacity-planning.md)
and ADR-0072 before interpreting a run.
