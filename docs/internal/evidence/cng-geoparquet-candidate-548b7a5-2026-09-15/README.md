# CNG GeoParquet replay on candidate pin 548b7a5 (honua-server#4747)

Re-pin replay of #4747's candidate-bound criteria (3: execute the fixture through the published AOT
image; 4: rerun CNG qualification against the re-pinned digest) after honua-release#349 pinned the
2026.1 candidate to honua-server trunk `548b7a5263da5a3f2381eb43f232687cdf92b0bf`.

| Identity | Value |
|---|---|
| Release manifest | honua-io/honua-release@`52cc3f2c60ff357bcec03ce1fa3ccd2ff2b81ef1` |
| Candidate image | `ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1` (Alpine 3.24, `linux-musl-x64` Native AOT, `docker/Dockerfile.aot`) |
| Image revision label | `548b7a5263da5a3f2381eb43f232687cdf92b0bf` (contains #4800, merge `f1f05252`) |
| Pre-fix control image | `ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd` (7ba4226, the image #4747 was filed against) |
| Fixture | `docker/cng/seed.sql`, layer `cng/FeatureServer/1000`, six Point rows |
| Request | `GET /rest/services/cng/FeatureServer/1000/query?where=1=1&outFields=*&f=parquet` |

## Verdict

**Criteria 3 and 4 fail on the pinned image**, and the reason is no longer #4747's defect.

- The #4747 defect is gone from the published image. With 7ba4226 the request dies in
  `GeoParquetFeatureWriter.BuildGeoParquetMetadata` (`Reflection-based serialization has been
  disabled`). With 548b7a5 metadata builds, and the log has no reflection error.
- The pinned image still returns no GeoParquet. The request now fails one step later, in
  `GeoParquetFeatureWriter.WriteArrowParquet`, with the typed 501 `ParquetRuntimeUnavailableException`.
  `ParquetSharpNative.so` is glibc-linked (`parquetsharpnative-needed.txt`: `libc.so.6`,
  `ld-linux-x86-64.so.2`, `libatomic.so.1`), and the musl runtime stage installs neither glibc nor
  libatomic.
- Adding packages does not fix it. `local-diag-gcompat/` is an image `FROM` the pinned digest with
  `apk add gcompat libatomic`. It loads the library and then segfaults on the first `f=parquet`
  request (exit 139, no HTTP response).

The root `Dockerfile` (glibc, installs `libatomic1`) serves valid GeoParquet. It is what the CNG
lane builds in diagnostic mode, which is why #4800's branch run 34772961370 passed while the
published AOT image fails. The runtime gap is tracked as honua-server#4912.

## Evidence

| Directory | What it shows |
|---|---|
| `ci-run-34924041757/` | `cng-candidate.yml` dispatched with `release_ref=52cc3f2c…` ([run](https://github.com/honua-io/honua-server/actions/runs/34924041757)). Candidate identity is the pinned digest and source SHA. `cng.parquet` is the 501 envelope. The GeoParquet cells fail: PyArrow and GeoPandas report "Parquet magic bytes not found", GDAL `ogrinfo` reports "not recognized as being in a supported file format", and `gpq validate` reports "file is smaller than indicated metadata size". FlatGeobuf, PMTiles, 3D Tiles and COG/Zarr pass. |
| `local-after-548b7a5/` | `replay.sh` on the pinned digest: HTTP 200 with the GeoServices 501 envelope. Log excerpt shows `DllNotFoundException … libatomic.so.1 (needed by /app/ParquetSharpNative.so)` from `WriteArrowParquet`. |
| `local-before-7ba4226/` | `replay.sh` on the pre-fix image: the 500 envelope and the `BuildGeoParquetMetadata` reflection stack, reproducing #4747's original report. The original CI failure is run 34728065714. |
| `local-diag-gcompat/` | Diagnostic `Dockerfile` and result: `container-state.txt` has `exit=139`, and `http.txt` shows no response. |
| `parquetsharpnative-needed.txt` | `readelf -d` NEEDED entries of `/app/ParquetSharpNative.so` copied out of the pinned image. |

Response bodies are stored as `response-body.json` because each served payload was a JSON error
envelope, not Parquet.

## Reproduce

```bash
# Pinned candidate (after) and pre-fix control (before); private compose projects and ports.
docs/internal/evidence/cng-geoparquet-candidate-548b7a5-2026-09-15/replay.sh \
  ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1 out/after 18747 18748
docs/internal/evidence/cng-geoparquet-candidate-548b7a5-2026-09-15/replay.sh \
  ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd out/before 18757 18758

# Candidate qualification against the frozen manifest.
gh workflow run cng-candidate.yml -R honua-io/honua-server --ref trunk \
  -f release_ref=52cc3f2c60ff357bcec03ce1fa3ccd2ff2b81ef1
```

The diagnostic image was built from `local-diag-gcompat/Dockerfile` and run through a copy of
`replay.sh`. That copy drops the digest-only guard (the image is local), uses the image ID in
`image.txt`, and records the container exit state after the request.
