# honua-server#4912: glibc native-AOT serving image serves GeoParquet

Candidate pin 548b7a5 (`ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`) published `docker/Dockerfile.aot` as `linux-musl-x64` on Alpine. Every `FeatureServer f=parquet` request returned the typed 501, because `ParquetSharpNative.so` is glibc-linked. This directory holds the local proof for the fix: `docker/Dockerfile.aot` now publishes `linux-x64`/`linux-arm64` on the Ubuntu `runtime-deps:10.0` base with `libatomic1`.

Per the lane packet, no nightly was dispatched on the fix branch. Every image here is either the pinned digest or a local build of this branch's Dockerfile.

## Native libraries in the shipped images (`ldd` inside each image)

| Image | Library | Result |
| --- | --- | --- |
| AOT 548b7a5 (Alpine, musl) | `ParquetSharpNative.so` | `libatomic.so.1` and `ld-linux-x86-64.so.2` missing, glibc symbols unresolved |
| AOT 548b7a5 (Alpine, musl) | `libduckdb.so` | glibc-linked, `ld-linux-x86-64.so.2` missing, symbols unresolved |
| AOT 548b7a5 (Alpine, musl) | `libSkiaSharp.so`, `alpine-librdkafka.so`, `libe_sqlite3.so` | musl builds, resolve |
| Lambda AOT 548b7a5 (`sha256:4d15f1c8…`, Ubuntu 24.04) | `ParquetSharpNative.so` | `libatomic.so.1 => not found` |

The Lambda AOT image had the same GeoParquet defect for a different reason: glibc was present but `libatomic1` was not. `docker/Dockerfile.lambda.aot` now installs `libatomic1` and link-checks the library too.

## Regression proof: the smoke fails on the shipped image

`pinned-548b7a5-musl/`: `scripts/ci/smoke-aot-geoparquet.sh` run against the pinned digest exits 1. The request returns HTTP 200 with a 502-byte body that is the GeoServices 501 envelope, not `PAR1`. The server log shows `DllNotFoundException: Unable to load shared library 'ParquetSharpNative'`.

## Regression proof: the architecture tests fail on the shipped Dockerfile shape

`architecture-tests/trunk-shape-fails.txt`: `AotNativeLibraryRuntimeTests` runs against trunk's `docker/Dockerfile.aot`, `docker/Dockerfile.lambda.aot`, `nightly-container-build.yml` and `base-image-mirrors.sh` (the same test DLL, repository root pointed at the trunk files). All four tests fail.
`architecture-tests/branch-passes.txt`: on this branch, the new tests plus `ServingImageBoundaryTests`, `DockerfileWritableStorageDirectoryTests` and `CustomBuildProfileTests` pass (20/20).
