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

## Running

Run with `dotnet run -c Release --project benchmarks/Honua.Benchmarks -- --filter '*'`,
or scope by category, e.g. `--anyCategories tile` or `--anyCategories filter cache`.
Pass `--list flat` to see every benchmark without launching the runners.
