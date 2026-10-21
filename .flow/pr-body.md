test(mapserver): verify Identify CRS defaults, precedence, and limits

# COVERS: Server Tests (GeoServices Geometry VectorTile and Versioning); Server Tests (GeoServices MapServer Identify)

## Issue Link
Refs #5884 (the original repair landed in #5887; this change extends its regression coverage).

## Summary
Culprit #5835 (`3565542b17a2814b041d9a5d633efb590047bbcd`) changed omitted MapServer Identify SR from the native service CRS to the advertised EPSG:3857. Fixtures sending untagged degree coordinates then returned zero features: 15 failures in the Identify shard and one global-cap failure in the geometry/vector-tile/versioning shard at `6f7430587a1a8283f7e95de86c38908a3cb2ed9d`.

Current trunk already contains #5887's explicit `sr=4326` fixture repair and #5893's omitted-SR endpoint coverage. Preserve those fixes and add assertions for CRS defaults and precedence. This is fix-forward regression coverage: the failure spans multiple trailing heads and eleven landings separate the supplied green and red heads, so reverting the broad culprit would discard unrelated protocol corrections. No runtime change is needed.

## Changes Made
- Exercise the exact two-result global Identify cap with both explicit EPSG:4326 and omitted-SR EPSG:3857 envelopes; assert every returned geometry's CRS.
- Add four GET/POST cases proving explicit `sr` overrides geometry CRS and `latestWkid` overrides `wkid`, with exact feature coordinates and returned CRS.
- Require the existing omitted-SR GET/POST cases to return `wkid=3857` as well as projected coordinates.
- Preserve the original exact result-count assertions and the other lane's coverage.

## Diagnosis Evidence
Read the [Identify diagnosis](https://github.com/honua-io/honua-server/commit/6f7430587a1a8283f7e95de86c38908a3cb2ed9d#commitcomment-205857884), [adjacent-family diagnosis](https://github.com/honua-io/honua-server/commit/6f7430587a1a8283f7e95de86c38908a3cb2ed9d#commitcomment-205859368), and [culprit PR #5835](https://github.com/honua-io/honua-server/pull/5835) before local investigation. Artifact inventory was checked first; the seven-day shard TRX artifacts (`11710678770` and `11711346841`) have expired and are no longer downloadable.

[Retained Identify logs](https://github.com/honua-io/honua-server/actions/runs/38158644558/job/114539712938) show 50 total, 35 passed, 15 failed. The diagnosis records the same 15 failures in the culprit PR's report-only test job. [Retained adjacent-shard logs](https://github.com/honua-io/honua-server/actions/runs/38158644558/job/114539712856) show 275 passes and the global-cap failure. The culprit PR did not select that adjacent shard; its Identify results corroborate the shared CRS mechanism rather than executing the cap test.

The cap test blob is identical from the supplied green through the red head. Source comparison shows the native-service fallback through immediate predecessor `8b45637322`, then `CachedMapSpatialReferenceId` at #5835. A local replay of the original cap test at `8b45637322c1cefc088fd5917943fa3712c3633e` passed: 1 executed, 1 passed, no failures or skips; TRX `/tmp/honua-geoservices-6f74305-adjacent/8b45637322c1cefc088fd5917943fa3712c3633e.trx`. No completed local runtime replay at the culprit or full historical bisect is claimed. Two earlier baseline attempts exited 75 at build admission without starting a test. Historical replay commands used `-p:NuGetAudit=false -p:IncludeSourceRevisionInInformationalVersion=false -p:EnableSourceLink=false`; current-branch checks retain normal audit and metadata settings.

## Testing
- [x] `MapServerQueryLimitsTests`: Release, 4 passed, 0 failed, 0 skipped.
- [x] `MapServerIdentifyEndpointTests`: Release, 56 passed, 0 failed, 0 skipped, including all four new precedence cases.
- [ ] `scripts/ci/pre-pr-check.sh --fast`: Release build passed with zero warnings/errors and scoped formatting passed; retrying after the host churn governor rejected the literal test-runner argument separator.
- [x] Changed-project formatting verified by the fast check (one owning project, two included files, `timeout 20m`).
- [x] `git diff --check`.

Exact successful focused commands:

```bash
dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --filter FullyQualifiedName~MapServerQueryLimitsTests --logger 'trx;LogFileName=mapserver-query-limits.trx' --results-directory /tmp/honua-geoservices-6f74305-results
IFS= read -r HONUA_TEST_DB_URL < /tmp/mapserver-identify-sidecar/db-url
export HONUA_TEST_DB_URL
NUGET_HTTP_CACHE_PATH=/tmp/mapserver-identify-nuget dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~MapServerIdentifyEndpointTests --logger 'trx;LogFileName=identify-sidecar.trx' --results-directory /tmp/mapserver-identify-verification
```

The Identify Release dependency and test assembly build completed before the successful `--no-build` run. Its initial automatic-container attempt failed during PostgreSQL fixture initialization, before endpoint assertions. The successful run used the existing TestKit-supported `HONUA_TEST_DB_URL` with a dedicated PostGIS 18 sidecar; each test retained its isolated schema and original assertions. No fixture or runtime code changed. Results: `/tmp/honua-geoservices-6f74305-results/mapserver-query-limits.trx` and `/tmp/mapserver-identify-verification/identify-sidecar.trx`.

Required fast-check command (currently running):

```bash
IFS= read -r HONUA_TEST_DB_URL < /tmp/mapserver-identify-sidecar/db-url
export HONUA_TEST_DB_URL
export NO_PROXY="${NO_PROXY:+${NO_PROXY},}localhost,127.0.0.1,::1"
export no_proxy="${no_proxy:+${no_proxy},}localhost,127.0.0.1,::1"
export DOTNET_PROCESSOR_COUNT=4
PATH="/tmp/mapserver-identify-dotnet-compat:$PATH" NUGET_PACKAGES=/tmp/mapserver-identify-packages NUGET_HTTP_CACHE_PATH=/tmp/mapserver-identify-nuget scripts/ci/pre-pr-check.sh --fast
git diff --check
```

The fast check selects 33 roots / a 105-project closure because the staged description triggers shared-input discovery. It uses Release, warnings as errors, changed-project `dotnet format --include` under `timeout 20m`, selected unit tests, and architecture checks. The Linux-cache attempt completed the build and format check, then exited 2 because the host churn governor could not parse `-- RunConfiguration.MaxCpuCount=1`. A temporary local adapter forwards every command to the original lane shim and expresses that exact one-CPU test-runner setting through `/tmp/mapserver-identify-dotnet-compat/single-cpu.runsettings`; governor admission, the four-node build cap, tests, and assertions are retained. Local xUnit collection workers are capped at four through the same [documented runsettings mechanism](https://xunit.net/docs/config-runsettings), overriding this monolith assembly’s unlimited worker setting. Local loopback bypasses the environment proxy, and `DOTNET_PROCESSOR_COUNT=4` matches the lane CPU budget. Unbounded selected-smoke attempts encountered S3, Redis, printing, and sampler timing failures; no test budget or assertion was changed. Retry log: `/tmp/mapserver-identify-pre-pr-compat.log`. The previous own-worktree attempt encountered CS0009 memory-allocation errors opening dependencies from the Windows 9p-mounted global NuGet cache and was stopped. The retry uses a Linux-local package cache without changing source, package versions, auditing, or compiler settings. An earlier sibling-worktree gate ended without a completion result and is not counted as a pass. All managed commands use the PATH lane shim with its four-node cap and shared compilation enabled. The dedicated local PostGIS sidecar, private connection file, and Linux package cache remain available for the opening-stage rerun; none is tracked.

Staged handoff: verified source is based on `f854f8fac0d5bf9fd9e4719a7f603463469bfb99`. Trunk advanced to `581cda46c63e3bad91f85e405af6d949a4ac4cb3` during local verification. Before opening the PR, sync the then-current default branch and rerun verification as required by the packet's opening-stage instruction. This staged delivery does not open a PR or claim verification of later trunk landings.

## Self-review
Completed one round using `honua-flow-work/tools/lib/pr-review-rubric.md`. Existing endpoints and fixtures are reused; the original exact cap assertion remains. No runtime, dependency, security, or public API change. P0/P1: none found. P2/P3 follow-ups: none identified. Both focused classes passed; the required fast check remains pending.

## Gate Impact
- [x] PR gates (build, test, governance).
- [x] Nightly gates (the two named full-matrix families).

## Docs or Contract Impact
- [x] None — tests the existing advertised MapServer CRS contract.

## Release/Deploy Impact
- [x] None — standard merge-and-release flow.

## Breaking Changes
None.

## Pre-PR Checklist
- [ ] Ran `scripts/ci/pre-pr-check.sh --fast` and all checks passed.
- [x] Conventional commit messages and required author/committer identity.
- [x] Existing repair issue linked above.
- [x] No PR opened and no CI/review polling; staged delivery only.
