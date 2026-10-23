# honua-server#4912 — GeoParquet on the published AOT image, replayed on candidate pin 8862065

The 2026.1 candidate is honua-server `886206527cc97bad1bbaa5fa6358910ebc45e9c0`
(honua-release `31ed9cc44ffa42f8d96a16643460bb1842a30318`, release#354). Its AOT image is
`ghcr.io/honua-io/honua-server@sha256:0b16046533e5330ecdd48255c06b5397e869191299e1e5e8cc7b4b2ded60b388`,
a two-platform index resolved here in `identity/aot-index.raw.json`:

| platform | manifest digest |
|---|---|
| linux/amd64 | `sha256:ec8d7915ca72ef3a8d4ffb4719f7711496f5046f538acf7e4fc2d7e6e2d028aa` |
| linux/arm64 | `sha256:ee9e6dc0ad87e10f7bc47328048d9b28a5e58b49d7636765b6719433fe97f06d` |

Both legs carry `org.opencontainers.image.revision=886206527cc97bad1bbaa5fa6358910ebc45e9c0`, so every
artifact below is bound to the pinned candidate and not to a branch build. The fix under replay is
#4928 (native-AOT image republished on Ubuntu 24.04 glibc with `libatomic1`) plus #4951 (the
self-contained Skia native asset that the arm64 `ldd -r` gate then exposed).

## Per-criterion disposition

| # | Criterion | Status |
|---|---|---|
| 1 | The published AOT image serves `FeatureServer f=parquet` as valid GeoParquet for the seeded `cng` layer | **met** on linux/amd64; on linux/arm64 the request-level replay is released, see below |
| 2 | The serving-image boundary check still passes, and the image keeps non-root, read-only root filesystem and no GDAL | **met**, both platforms |
| 3 | A regression in the image-build lane that fails when the AOT image cannot load the Parquet encoder | **met** — the regression is exercised in both directions here |
| 4 | After a re-pin, `cng-candidate.yml` against the new digest passes the GeoParquet cells | **met** for gpq and PyArrow; GeoPandas and GDAL remain `skip` on harness gap #4799, and are decoded directly here instead |

## Criterion 1 — GeoParquet from the published image

`regression/smoke-amd64-8862065.txt` is the repository's own
`scripts/ci/smoke-aot-geoparquet.sh` run against the pinned amd64 digest. It boots the image through
the CNG lane's `docker/cng/compose.yml` in the lane's order (migrate, stop, seed
`docker/cng/seed.sql`, restart) and fetches the seeded layer with `f=parquet`:

```
image=ghcr.io/honua-io/honua-server@sha256:ec8d7915ca72ef3a8d4ffb4719f7711496f5046f538acf7e4fc2d7e6e2d028aa
revision=886206527cc97bad1bbaa5fa6358910ebc45e9c0
HTTP 200   bytes 4947   head PAR1   tail PAR1
```

The served file is
`sha256:cfc8b09415c1d972cb4e73e37c4ac522a0fb8e90413f87177d66d7bfda7f01b9` — **byte-identical** to the
artifact the hosted CNG lane graded (`ci-run-35051635419/cng.parquet.sha256` in
`../cng-geoparquet-candidate-8862065-2026-09-16/`), so the local decode evidence grades what the lane
graded. `local-amd64/honua-server.excerpt.log` shows the request logged as
`StatusCode 200, ContentType application/vnd.apache.parquet, ContentLength 4947`, with zero
occurrences of `DllNotFoundException`, `ParquetRuntimeUnavailable`, `libatomic`, `Query failed`,
`Reflection-based serialization` or `Unhandled exception` in the whole container log.

## Criterion 2 — boundary check and runtime posture

`boundary/serving-image-boundary-{amd64,arm64}.txt` are
`scripts/ci/verify-serving-image-boundary.py --serving-image <digest>` against **each** platform
manifest of the pinned index. Both exit 0:

```
Serving image ghcr.io/honua-io/honua-server@sha256:ec8d7915… is native AOT and GDAL/PROJ/GEOS-free.
Serving image ghcr.io/honua-io/honua-server@sha256:ee9e6dc0… is native AOT and GDAL/PROJ/GEOS-free.
```

The boundary check is a static rootfs scan, so `hardened-replay.sh` additionally serves `f=parquet`
from the pinned amd64 image **while it runs in the posture the product ships** in
`docker-compose.yml` — `read_only: true`, `cap_drop: [ALL]`, `no-new-privileges`, tmpfs for the
writable paths. `hardened-amd64/runtime-posture.txt` records what the container that answered the
request actually ran with:

```
ReadonlyRootfs=true   CapDrop=[ALL]   CapAdd=[]   SecurityOpt=[no-new-privileges:true]
Privileged=false      ConfigUser=1001:1001
uid=1001(honua) gid=1001(honua) groups=1001(honua)
touch: cannot touch '/app/write-probe': Read-only file system
GDAL/PROJ/GEOS native libraries in the running container (must be none): (end of list)
```

and `hardened-amd64/http.txt` is `HTTP 200`, 4 947 bytes, `PAR1`/`PAR1` — the same
`sha256:cfc8b094…` file. The encoder therefore needs no writable root and no capabilities. The
container was still healthy after the native encoder ran (a native crash exits the process), and the
log has no `Read-only file system` line.

Four independent consumers then decode that exact file (`hardened-amd64/decode-console.txt`,
produced by `../cng-geoparquet-candidate-8862065-2026-09-16/decode.sh`, each in its own pinned
container):

- **gpq v0.24.0** (`gpq.log`) — `describe` + `validate`, every GeoParquet 1.1.0 rule passes.
- **PyArrow 25.0.1 + GeoPandas 1.1.4** (`pyarrow-geopandas.log`) — `verify-geoparquet.py`,
  **57/57 checks**, asserted against values transcribed from `docker/cng/seed.sql`, not snapshotted
  from the response.
- **GDAL** at the digest `cng-conformance.yml` pins (`ogrinfo.log`) — driver `Parquet`,
  `Feature Count: 6`, all six features with their seeded attributes and `POINT` ordinates.

## Criterion 3 — the image-build regression, exercised in both directions

The regression landed with #4928: `scripts/ci/smoke-aot-geoparquet.sh`, wired into
`nightly-container-build.yml` between "Verify nightly AOT candidate" and "Publish verified nightly
AOT architecture tags", so it gates tag publication on the exact built digest.
`AotNativeLibraryRuntimeTests.NightlyAotBuild_ShouldSmokeGeoParquetOnTheCandidateDigestBeforePublishingTags`
pins that order and forbids a `continue-on-error` escape hatch. This receipt proves the gate is not
decorative by running the same script against both images:

| image | revision | result |
|---|---|---|
| `sha256:ec8d7915…` (pinned candidate, glibc) | 8862065 | **exit 0** — `regression/smoke-amd64-8862065.txt` |
| `sha256:29974ee7…` (previous pin, musl Alpine) | 548b7a5 | **exit 1** — `regression/smoke-musl-548b7a5.txt` |

The musl run fails exactly the way this issue was filed: `HTTP 200`, 502 bytes, body framed `{"er`
… `"]}}` instead of `PAR1`, with the script reporting

```
::error::AOT GeoParquet smoke failed for …@sha256:29974ee7…: f=parquet body is not a Parquet file
(502 bytes): {"error":{"code":501,"message":"Not Implemented","details":["GeoParquet (f=parquet)
output is unavailable on this runtime image: …
```

`regression/musl-548b7a5/response-body.json` is that 501 envelope and
`regression/musl-548b7a5/honua-server.excerpt.log` the `DllNotFoundException`:
`Error loading shared library libatomic.so.1: No such file or directory (needed by
/app/ParquetSharpNative.so)`. A 200 carrying the capability envelope does not pass the gate.

On the arm64 leg the equivalent regression is inside the image build itself: `docker/Dockerfile.aot`
runs `ldd -r` over `ParquetSharpNative.so`, `libduckdb.so` and `libSkiaSharp.so` and fails the build
on `not found` / `undefined symbol`, per architecture. `arm64/native-linkage.txt` replays that gate
**inside the published arm64 rootfs** and it passes for all three natives, with
`libatomic.so.1 => /lib/aarch64-linux-gnu/libatomic.so.1` resolved — the dependency whose absence was
this issue.

## Criterion 4 — CNG qualification against this digest

`cng-candidate.yml` dispatched with `release_ref=31ed9cc44ffa42f8d96a16643460bb1842a30318`:

- run **35051635419** (harness `af22624c…`) — conclusion `success`; `ci/cng-candidate-run-35051635419.json`.
  Its artifacts are retained under `../cng-geoparquet-candidate-8862065-2026-09-16/ci-run-35051635419/`:
  `candidate-identity.json` resolves the frozen manifest to `server_image sha256:0b16046…` /
  `source_sha 8862065…`, GeoParquet 1.1.0 is `PASS` on the live `f=parquet` response, and the PyArrow
  25.0.1 geoparquet cell passes all three facets.
- run **35058225436** (harness `69f8432b`, current trunk) — re-dispatched for this issue against the
  same frozen manifest, 2026-09-16; conclusion `success`
  (`ci/cng-candidate-run-35058225436.json`). `ci/candidate-identity-35058225436.json` again resolves
  the manifest to `server_image sha256:0b16046…` / `source_sha 8862065…`, and every observation in
  `ci/geoparquet-observations-35058225436.json` carries that same `image_digest`. The lane served
  `sha256:cfc8b09415c1d972cb4e73e37c4ac522a0fb8e90413f87177d66d7bfda7f01b9` — the same bytes as both
  local replays in this receipt and as run 35051635419. `ci/cng-summary-35058225436.md` has
  `GeoParquet 1.1.0 | FeatureServer f=parquet | gpq validate | PASS` alongside FlatGeobuf, PMTiles,
  3D Tiles and COG/Zarr; `ci/gpq-validate-35058225436.log` is the validator output. The PyArrow
  25.0.1 geoparquet cell is `pass` with `budget_results.met: true`; `cell_receipt` is `passed: 6`
  over 24 governed cells with 0 failures.

**Known gap, not a candidate defect.** The GeoPandas and GDAL geoparquet cells report
`result: "skip"`, `skip_reason: "no metadata was read back from the artifact, so the geoparquet
metadata oracle is unproven"`. That is the pre-existing harness gap honua-server#4799 — the harness
never populates `observed_metadata` for those two lanes — and it was the same disposition on the two
previous runs and in #4956. Both clients do read this exact file: `hardened-amd64/ogrinfo.log` and
`hardened-amd64/pyarrow-geopandas.log` are GDAL and GeoPandas decoding the byte-identical artifact
successfully. No CNG validator, cell or skip rule is relaxed here.

## Released criterion — the arm64 request-level replay

Criterion 1 is proven by request on linux/amd64 only. On linux/arm64 the candidate is proven
structurally (boundary check green, Ubuntu 24.04 glibc rootfs, aarch64 `ParquetSharpNative.so` with
every `NEEDED` entry — `libatomic.so.1` included — resolving inside the published image, all three
natives passing the Dockerfile's `ldd -r` gate replayed in that rootfs), but no `f=parquet` request
was issued against it. `arm64/qemu-replay-limit.txt` records why:

1. This host is x86_64; the arm64 image runs only under binfmt/qemu-user.
2. Ordinary aarch64 binaries emulate fine here (`sh`, `ldd`, `uname` all ran under qemu).
3. The native-AOT server binary does not: through `docker/cng/compose.yml` it dies with
   `qemu: uncaught target signal 11 (Segmentation fault) - core dumped` before it is healthy, and
   standalone with no database involved `/app/Honua.Server --version` never returns (killed at 200 s)
   while the amd64 leg of the same index reaches configuration validation in about a second.
4. A hosted `ubuntu-24.04-arm` runner cannot close the gap either: `docker/cng/compose.yml` needs
   `postgis/postgis:16-3.4`, which is a single amd64 manifest with no manifest list, so the seed
   database has no arm64 image to boot. That is precisely why `nightly-container-build.yml` gates the
   GeoParquet smoke on `matrix.arch == 'amd64'`, with `AotNativeLibraryRuntimeTests` pinning that
   condition and its reason.

Closing that gap needs an arm64-capable CNG seed database (or arm64 hardware in the lane), which is a
harness change, not a candidate defect. It is out of scope for this replay and is left as the one
released criterion.

## Reproduce

```bash
# criterion 2, per platform manifest
python3 scripts/ci/verify-serving-image-boundary.py --serving-image ghcr.io/honua-io/honua-server@sha256:<arch-digest>

# criteria 1 and 3 (positive, then the musl image that must fail)
scripts/ci/smoke-aot-geoparquet.sh ghcr.io/honua-io/honua-server@sha256:ec8d7915… out-glibc 18922 18923
scripts/ci/smoke-aot-geoparquet.sh ghcr.io/honua-io/honua-server@sha256:29974ee7… out-musl  18924 18925  # exits 1

# criteria 1 and 2 under the shipped hardened posture, then the four decoders
docs/internal/evidence/aot-geoparquet-candidate-8862065-2026-09-16/hardened-replay.sh \
  ghcr.io/honua-io/honua-server@sha256:ec8d7915… out-hardened 18926 18927
docs/internal/evidence/cng-geoparquet-candidate-8862065-2026-09-16/decode.sh out-hardened

# criterion 4
gh workflow run cng-candidate.yml --ref trunk -f release_ref=31ed9cc44ffa42f8d96a16643460bb1842a30318
```

## Related receipts

- `../cng-geoparquet-candidate-548b7a5-2026-09-15/` — the previous pin failing, and `replay.sh`.
- `../aot-geoparquet-glibc-4912-2026-09-15/` — the #4928 fix on its branch (boundary run, architecture tests).
- `../cng-geoparquet-candidate-8862065-2026-09-16/` — #4747's replay on this pin, `decode.sh` and `verify-geoparquet.py`.
