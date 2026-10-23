# CNG GeoParquet replay on candidate pin 8862065 (honua-server#4747)

Re-pin replay of #4747's candidate-bound criteria (3: execute the fixture through the published AOT
image; 4: rerun CNG qualification against the re-pinned digest) after honua-release#354 pinned the
2026.1 candidate to honua-server trunk `886206527cc97bad1bbaa5fa6358910ebc45e9c0`.

This supersedes `../cng-geoparquet-candidate-548b7a5-2026-09-15/`, which recorded the previous pin
failing for a *different* defect (musl AOT could not load `ParquetSharpNative.so`, honua-server#4912).
That fix landed as #4928 and is in this pin.

| Identity | Value |
|---|---|
| Release manifest | honua-io/honua-release@`31ed9cc44ffa42f8d96a16643460bb1842a30318` (PR #354) |
| Candidate image | `ghcr.io/honua-io/honua-server@sha256:0b16046533e5330ecdd48255c06b5397e869191299e1e5e8cc7b4b2ded60b388` (`nightly-8862065`, Ubuntu 24.04, glibc Native AOT, `docker/Dockerfile.aot` after #4928) |
| Image revision label | `886206527cc97bad1bbaa5fa6358910ebc45e9c0` (contains #4800 `f1f05252` and #4928 `95907056`) |
| Pre-fix control image | `ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd` (7ba4226, the image #4747 was filed against) |
| Fixture | `docker/cng/seed.sql`, layer `cng/FeatureServer/1000`, six Point rows |
| Request | `GET /rest/services/cng/FeatureServer/1000/query?where=1=1&outFields=*&f=parquet` |
| Served artifact | 4 947 bytes, `application/vnd.apache.parquet`, `sha256:cfc8b09415c1d972cb4e73e37c4ac522a0fb8e90413f87177d66d7bfda7f01b9` |

## Verdict

**Criteria 3 and 4 pass on the pinned image.** The local replay and the hosted qualification lane
served the *byte-identical* file (`sha256:cfc8b094…` in both `local-after-8862065/cng.parquet.sha256`
and `ci-run-35051635419/cng.parquet.sha256`).

The fixture exercises the branch the defect lived in. The seeded layer declares `geometry_type
'Point'`, so `BuildGeoParquetMetadata` takes the concrete, non-empty geometry-types path — the one
that called `JsonSerializer.Serialize` before #4800 — and the served metadata is
`"geometry_types":["Point"]`, not the literal `[]` of the empty branch. A `[]` here would mean the
replay never touched the defect, so `verify-geoparquet.py` asserts on it explicitly.

## Criterion 3 — the fixture through the published AOT image

`local-after-8862065/` is `../cng-geoparquet-candidate-548b7a5-2026-09-15/replay.sh` run against the
pinned digest: the lane's own `docker/cng/compose.yml` in the lane's order (migrate, stop, seed
`docker/cng/seed.sql`, restart), with only the image reference and a private compose project and
ports changed.

| File | What it shows |
|---|---|
| `image.txt` | The digest, the `org.opencontainers.image.revision` label `8862065…`, `amd64`. |
| `http.txt` | `HTTP 200`, 4 947 bytes. |
| `cng.parquet` | The served response, verbatim. Starts and ends with `PAR1`. |
| `honua-server.excerpt.log` | `query_parquet executed` and `200 4947 "application/vnd.apache.parquet"`. The full log has no `Reflection-based serialization`, `DllNotFoundException`, `ParquetRuntimeUnavailable` or `Query failed` line. |
| `gpq.log` | `gpq v0.24.0 describe` + `validate`: every GeoParquet 1.1.0 rule passes, including "all geometry types must be included in the `geometry_types` metadata". |
| `pyarrow-geopandas.log`, `fixture-assertion.json` | `verify-geoparquet.py`: **57/57** checks. |
| `ogrinfo.log` | GDAL `ogrinfo -al` (lane-pinned image): driver `Parquet`, `Feature Count: 6`, all six features printed with their seeded attributes and `POINT` ordinates. |

`verify-geoparquet.py` holds the expected values as literals transcribed from the seed `INSERT` — it
is not a snapshot of the server's output, and it fails if the payload drifts from the fixture. It
asserts, per row matched by `name`: the WKB point ordinates (parsed by a hand-written 21-byte WKB
reader, not by a geometry library), `category`, `population`, `ratio`, `active`, `observed_at`, and
the `bbox` covering struct the `geo` metadata advertises; plus, file-wide, the exact `geo` document,
the non-empty `geometry_types`, that `geo` round-trips as UTF-8 JSON, the six names, the GeoPandas
geometry decode, `total_bounds` `[-122.4194, 0.0, 179.5, 86.0]`, and `OGC:CRS84`.

Four independent consumers, each in its own pinned container, read the same bytes: gpq `v0.24.0`
(the lane's validator pin), PyArrow `25.0.1`, GeoPandas `1.1.4` and GDAL
`ghcr.io/osgeo/gdal@sha256:323828a5…` (the digest `.github/workflows/cng-conformance.yml` pins).

### failure-before

`local-before-7ba4226/` is the same `replay.sh` against the pre-#4800 image the issue was filed
against. `response-body.json` is the 500 GeoServices envelope, and `honua-server.excerpt.log` is
#4747's original stack: `InvalidOperationException: Reflection-based serialization has been
disabled` at `JsonSerializerOptions.ConfigureForJsonSerializer` inside
`GeoParquetFeatureWriter.BuildGeoParquetMetadata`.

## Criterion 4 — CNG qualification against that exact digest

[`cng-candidate` run 35051635419](https://github.com/honua-io/honua-server/actions/runs/35051635419),
dispatched with `release_ref=31ed9cc44ffa42f8d96a16643460bb1842a30318`, harness
`af22624c94c9d9e8ce4f0215307f5e8a2fb3e029`. **Conclusion: success.**

`ci-run-35051635419/candidate-identity.json` resolves the frozen manifest to
`server_image sha256:0b16046…` / `source_sha 8862065…`, and every observation in the fragment carries
that same `image_digest` and `source_sha`, so the result is bound to the pinned candidate.

- `cng-summary.md`: GeoParquet 1.1.0 `PASS` (`gpq validate` on the live `f=parquet` response), with
  FlatGeobuf, PMTiles, 3D Tiles and COG/Zarr also `PASS`.
- `geoparquet-observations.json`: the `geoparquet` surface cells. PyArrow `25.0.1` passes all three
  facets (`positive`, `metadata`, `media-schema`) with `budget_results.met: true`, reading back six
  features and bounds `[-122.4194, 0.0, 179.5, 86.0]`. `cell_receipt` reports `passed: 6` over 24
  governed cells with 0 failures.
- The GeoPandas and GDAL geoparquet cells are `result: "skip"`, `skip_reason: "no metadata was read
  back from the artifact, so the geoparquet metadata oracle is unproven"`. That is the pre-existing
  harness gap honua-server#4799 — the *harness* never populates `observed_metadata` for those two
  lanes — not a property of this candidate. Both clients do read this exact file: `ogrinfo.log` and
  `pyarrow-geopandas.log` above are GDAL and GeoPandas decoding it successfully, and the artifact
  they read is byte-identical to the one the lane graded.

## Reproduce

```bash
# Pinned candidate (after) and pre-#4800 control (before); private compose projects and ports.
docs/internal/evidence/cng-geoparquet-candidate-548b7a5-2026-09-15/replay.sh \
  ghcr.io/honua-io/honua-server@sha256:0b16046533e5330ecdd48255c06b5397e869191299e1e5e8cc7b4b2ded60b388 out/after 18947 18948
docs/internal/evidence/cng-geoparquet-candidate-548b7a5-2026-09-15/replay.sh \
  ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd out/before 18957 18958

# Four independent consumers + the fixture assertion over whatever cng.parquet is in that directory.
docs/internal/evidence/cng-geoparquet-candidate-8862065-2026-09-16/decode.sh out/after

# Candidate qualification against the frozen manifest.
gh workflow run cng-candidate.yml -R honua-io/honua-server --ref trunk \
  -f release_ref=31ed9cc44ffa42f8d96a16643460bb1842a30318
```
