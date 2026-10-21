test(stac): accept the pystac-client 0.9.1 get_collection not-found contract
# COVERS: Python Integration Tests

## Issue Link
Refs trunk-red a6581e4 (Python Integration Tests family; the a6581e4 failures were already repaired on trunk by #5887/#5892, this repairs the current red at 4ff2e7c)

## Summary
The packet's original red at a6581e4 (#5827 moved GET STAC paging to `next=offset:N`; `test_nb_page_03`/`test_nb_page_05` still asserted `token=`) was fixed forward on trunk on 10-11 by #5887 and #5892, and the family ran green on trunk through 405198df54 (run 39470379129).

The family is red again at 4ff2e7c8c2 (run 39507011395, job 118976528305): `stac_client/test_cert_common_core.py::test_cert_errh_01_unknown_collection - Failed: DID NOT RAISE APIError` (1 failed, 1115 passed). No landing in 405198df54..4ff2e7c8c2 touches STAC (#6457 CI shards, #6456 alert 402 detail, #6455 a Postgres raster test). The cause is the unpinned `pystac-client>=0.8.0` requirement: the green job installed pystac-client 0.9.0, the red job 0.9.1. pystac-client 0.9.1 changed `Client.get_collection` to return `None` when the collection request answers 404 (it still re-raises every other `APIError`). Server behaviour is unchanged. This is an upstream dependency change, so a revert does not apply; this PR fixes forward.

## Changes Made
- CERT-ERRH-01 accepts either documented client contract for an unknown collection: `APIError` with `status_code == 404` (pystac-client < 0.9.1) or `None` (0.9.1+, which returns `None` only for a 404). Any other resolved value still fails.
- Adds a transport-level assertion so the "structured pystac-client error" evidence holds on every client version: `StacApiIO(max_retries=0).request(<collection url>)` must raise `APIError` with `status_code == 404` and a non-empty message (the same pattern CERT-AUTH-01 uses).
- The RFC 7807 body checks (`status == 404`, detail names the collection) are unchanged; the evidence note records which `get_collection` contract was observed.

## Testing
- [x] Integration tests added/updated
- [x] Manual testing performed

Verification (exact commands in the staged receipt):
- Stub STAC server check of the new assertion logic: pystac-client 0.9.0 `get_collection` raises APIError 404, 0.9.1 returns None; `StacApiIO(max_retries=0).request` raises APIError 404 under both.
- Locally built Release server (`dotnet build src/Honua.Server/Honua.Server.csproj --configuration Release`), `tests/python`, `HONUA_TEST_CONFIGURATION=Release`:
  - trunk's test file + pystac-client 0.9.1: `pytest stac_client/test_cert_common_core.py -k errh_01` → 1 failed, `DID NOT RAISE APIError` at line 752 (the CI red reproduced).
  - this branch + pystac-client 0.9.1 (CI's resolved version): `pytest stac_client/test_cert_common_core.py` → 55 passed.
  - this branch + pystac-client 0.9.0 (the pinned client-compat / cng-conformance version): same file → 55 passed.
- Local runs used `--timeout 700` and `HONUA_STAC_COMPAT_TIMEOUT=600` because of host load, plus a scratch wrapper outside the repo that starts the already-built `Honua.Server.dll` in place of `dotnet run --no-build` (the lane shim queues `run` for a build slot). No repo test infrastructure was changed.

## Gate Impact
- [x] PR gates (build, test, governance)

## Docs or Contract Impact
- [x] None — no docs or contract impact

## Release/Deploy Impact
- [x] None — standard merge-and-release flow

## Breaking Changes
None

## Follow-ups (P3)
- `tests/python/requirements.txt` leaves every client library unpinned (`>=`), so upstream releases can redden the trailing matrix with no trunk change. Consider a constraints file or upper bounds for the certification clients so upgrades land deliberately.
