# GP persistent-store qualification on the re-pinned candidate 548b7a5

On 2026-09-13 the manifest-pinned image `7ba4226` failed the terminal-commit
store-loss cell: readiness stayed 200 while the store marker was absent (see
`gp-store-candidate-2026-09-13.md`). #4814 fixed readiness, and honua-release
#349 (trunk `52cc3f2c`) re-pinned the 2026.1 candidate to a nightly that
contains it. This note records the same unmodified checks on that pin, run by
the release DR gate itself.

| Identity | Observed value |
|---|---|
| Release manifest (`platform-manifest.yaml` at `52cc3f2c`) | SHA-256 `02c076be536514622c32a411e492f18413f257e9d4ff3d04c353dea591b89709` |
| Server source | `548b7a5263da5a3f2381eb43f232687cdf92b0bf` |
| Server image | `ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1` |
| Production worker | `gp-candidate-worker@sha256:25b8f6e628d8031d00985df47286611a08f2823b9d0e815869c07d2cf1272535`, built by the job from the pinned source with the unchanged `docker/worker-gdal/Dockerfile` |
| Store reference / persistence class / backup identity | `qualification` / `shared-persistent` / `gp-qualification-backup` |
| Configuration digest | `bb6a13a6b7970d85b518145569fd6470eb6940524ced24fa0994fa1d391f4041` |
| Producer | honua-release `dr-drill-local-docker` trunk run [34911391175](https://github.com/honua-io/honua-release/actions/runs/34911391175), job `gp-outputs` (success) |

The job verified the server image's GitHub provenance against source `548b7a5`
before running (`image-attestation.json.gz`), and ran
`scripts/qualification/gp-lifecycle-harness.sh` from that source.

## Output-store DR lane: 4/4 pass

`gp-store-candidate-548b7a5-2026-09-15-dr.json.gz` holds every receipt of the
lane. It declares 4 scenarios, has 4 receipts, 0 missing, 0 duplicate, 4 passed
and 0 failed.

- `output-store-attestation`: the server and the worker both log the same
  credential-free attestation (provider, store reference, configuration digest,
  persistence class, backup identity). An unattested root is neither accepted
  nor self-provisioned.
- `output-store-dr`: `gdal.ogr2ogr` stages a 52,840-byte GeoJSON output. The run
  replaces the serving hosts and the worker, then takes cold backups of
  PostgreSQL, Redis and the GP volume. It destroys the originals and restores
  them into empty stores; file inventories before and after match. The normal
  authenticated content route returns SHA-256
  `633dcf9227517dc22a218bc4cddeaf2f28ddb5f4c7f77eb03c4134cacdb85fc4` before and
  after, and the host-normalized descriptor is unchanged.

## Crash-boundary lane: 8/8 pass

`gp-store-candidate-548b7a5-2026-09-15-crash.json.gz` declares 8 scenarios
(topology, six crash cells, cleanup) and has 8 receipts, 0 missing, 0 duplicate,
8 passed and 0 failed. The terminal-commit/store-loss cell that failed on
`7ba4226` now passes. With the store marker and bytes hidden, the harness
requires `/healthz/ready` to answer 503 and the artifact content route to refuse
the read. After the store returns, the job recovers `successful` with the same
SHA-256 `633dcf92…85fc4`, and its result package is registered.

## Release binding

`gp-store-candidate-548b7a5-2026-09-15-receipt.json.gz` is the
`honua.gp-store-dr-candidate.v1` receipt that `gp-candidate-binding.py`
accepted for this pin (`pin.json.gz`). The same run's `full-platform` leg then
stopped at the DR sentinel's OGC create (HTTP 405, honua-release#343), so the
signed and published DR envelope has not been minted for this pin. That leg is
the release repository's full-platform drill, not the GP store proof: its GP
cells above ran to completion on the pinned digest.
