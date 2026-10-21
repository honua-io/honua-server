test(mapserver): verify Identify CRS defaults, precedence, and limits

# COVERS: Server Tests (GeoServices Geometry VectorTile and Versioning); Server Tests (GeoServices MapServer Identify)

## Issue Link
Refs #5884 (the original repair landed in #5887; this adds regression coverage).

## Summary
Culprit #5835 (`3565542b17a2814b041d9a5d633efb590047bbcd`) changed omitted MapServer Identify SR from the native service CRS to advertised EPSG:3857. Fixtures sending untagged degree coordinates then returned zero features: 15 Identify failures and one global-cap failure at `6f7430587a1a8283f7e95de86c38908a3cb2ed9d`.

The verified trunk base already contains #5887's explicit `sr=4326` fixture repair and #5893's omitted-SR endpoint coverage. Preserve those fixes and strengthen their regression coverage. Fix forward: this red spans multiple trailing heads and eleven landings separate last actual green `93f6f9aeb3eabdb7d184e40c9849e6c0708e7ba4` and the red head; reverting the broad culprit would discard unrelated protocol corrections. No runtime change is needed.

## Changes Made
- Exercise the exact two-result global Identify cap with explicit EPSG:4326 and omitted-SR EPSG:3857 envelopes, including every returned geometry's CRS.
- Add four GET/POST cases proving explicit `sr` overrides geometry CRS and `latestWkid` overrides `wkid`, with exact feature coordinates and returned CRS.
- Require the omitted-SR GET/POST cases to return `wkid=3857` as well as projected coordinates.
- Retain the original exact result-count assertions and the other lane's coverage.

## Diagnosis Evidence
Read the [Identify diagnosis](https://github.com/honua-io/honua-server/commit/6f7430587a1a8283f7e95de86c38908a3cb2ed9d#commitcomment-205857884), [adjacent-family diagnosis](https://github.com/honua-io/honua-server/commit/6f7430587a1a8283f7e95de86c38908a3cb2ed9d#commitcomment-205859368), and [culprit PR #5835](https://github.com/honua-io/honua-server/pull/5835) before local investigation. Artifact inventory was checked first; the shard TRX artifacts (`11710678770` and `11711346841`) have expired and are no longer downloadable.

[Retained Identify logs](https://github.com/honua-io/honua-server/actions/runs/38158644558/job/114539712938) show 50 total, 35 passed, 15 failed. The diagnosis records the same 15 failures in the culprit PR's report-only test job. [Retained adjacent-shard logs](https://github.com/honua-io/honua-server/actions/runs/38158644558/job/114539712856) show 275 passes and the global-cap failure. The culprit PR did not select that adjacent shard; its Identify failures corroborate the CRS mechanism rather than executing the cap test.

The original cap test blob is unchanged from the supplied green through the red head. Source comparison shows the native-service fallback through immediate predecessor `8b45637322`, then `CachedMapSpatialReferenceId` at #5835. A local replay of the original cap test at `8b45637322c1cefc088fd5917943fa3712c3633e` passed: 1 executed, 1 passed, no failures or skips; TRX `/tmp/honua-geoservices-6f74305-adjacent/8b45637322c1cefc088fd5917943fa3712c3633e.trx`. No completed local runtime replay at the culprit or full historical bisect is claimed. Historical replay used restore/debug-metadata overrides; current-branch checks retain normal auditing and metadata settings.

## Testing
- [x] `MapServerQueryLimitsTests`: Release, 4 passed, 0 failed, 0 skipped.
- [x] `MapServerIdentifyEndpointTests`: Release, 56 passed, 0 failed, 0 skipped, including all four new precedence cases.
- [x] Retained `Honua.Server.Tests` `Tier=Fast` smoke: Release, 4,164 passed, 0 failed, 0 skipped.
- [ ] Full `scripts/ci/pre-pr-check.sh --fast` result: Release build passed with zero warnings/errors and scoped formatting passed; retained Fast smoke running, curated units and architecture checks pending.
- [x] Changed-project formatting: one owning project, two included files, `timeout 20m`; the completed check emitted a generic workspace-loading warning.
- [x] `git diff --check`.

Exact focused verification commands:

```bash
dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --filter FullyQualifiedName~MapServerQueryLimitsTests --logger 'trx;LogFileName=mapserver-query-limits.trx' --results-directory /tmp/honua-geoservices-6f74305-results
IFS= read -r HONUA_TEST_DB_URL < /tmp/mapserver-identify-sidecar/db-url
export HONUA_TEST_DB_URL
NUGET_HTTP_CACHE_PATH=/tmp/mapserver-identify-nuget dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~MapServerIdentifyEndpointTests --logger 'trx;LogFileName=identify-sidecar.trx' --results-directory /tmp/mapserver-identify-verification
```

The Identify Release dependency and test assembly build completed before the successful `--no-build` run. Verification uses the supported `HONUA_TEST_DB_URL` with a dedicated PostGIS 18 sidecar, retaining each test's isolated schema. The sidecar and private connection file remain local for the opening-stage rerun. No fixture code, assertion, or test budget changed. TRX evidence: `/tmp/honua-geoservices-6f74305-results/mapserver-query-limits.trx`, `/tmp/mapserver-identify-verification/identify-sidecar.trx`, and `/tmp/mapserver-identify-verification/server-fast-bounded.trx`.

Exact selected-smoke and complete fast-gate commands:

```bash
IFS= read -r HONUA_TEST_DB_URL < /tmp/mapserver-identify-sidecar/db-url
export HONUA_TEST_DB_URL
export NO_PROXY="${NO_PROXY:+${NO_PROXY},}localhost,127.0.0.1,::1"
export no_proxy="${no_proxy:+${no_proxy},}localhost,127.0.0.1,::1"
export DOTNET_PROCESSOR_COUNT=4
PATH="/tmp/mapserver-identify-dotnet-compat:$PATH" NUGET_PACKAGES=/tmp/mapserver-identify-packages NUGET_HTTP_CACHE_PATH=/tmp/mapserver-identify-nuget dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --no-build --no-restore --configuration Release --filter 'Tier=Fast' --logger 'trx;LogFileName=server-fast-bounded.trx' --results-directory /tmp/mapserver-identify-verification -- RunConfiguration.MaxCpuCount=1
PATH="/tmp/mapserver-identify-dotnet-compat:$PATH" NUGET_PACKAGES=/tmp/mapserver-identify-packages NUGET_HTTP_CACHE_PATH=/tmp/mapserver-identify-nuget dotnet msbuild obj/mapserver-pre-pr-reserved.proj -t:Verify -p:Configuration=Release
git diff --check
```

The normal fast script selects 33 roots / a 105-project closure, with Release warnings-as-errors build, scoped formatting, selected Fast units, full catalogue/proof architecture tests, source guards, and local architecture review. Reserved-pipeline log: `/tmp/mapserver-identify-pre-pr-reserved.log`.

Local host compatibility settings are explicit: a Linux NuGet cache avoids the global Windows 9p cache's CS0009 allocation failures. The temporary PATH adapter always forwards to the original lane shim, retaining governor admission, the four-node build cap, and Roslyn shared compilation. It represents the literal `-- RunConfiguration.MaxCpuCount=1` setting through XML because the host governor's argument parser rejects that separator. `/tmp/mapserver-identify-dotnet-compat/default-single-cpu.runsettings` contains only that original one-test-host setting. For `Honua.Server.Tests`, `/tmp/mapserver-identify-dotnet-compat/server-fast.runsettings` additionally applies [xUnit's documented `MaxParallelThreads=4` option](https://xunit.net/docs/config-runsettings), bounding the monolith's otherwise unlimited local collection workers. Other projects retain their existing xUnit settings. The adapter preserves requested test filters, assertions, and time budgets and skips no test cases.

The ignored generated runner holds one owned reservation for the normal script and propagates its exit status. Recreate `obj/mapserver-pre-pr-reserved.proj` if needed:

```xml
<Project>
  <Target Name="Verify">
    <Exec Command="bash scripts/ci/pre-pr-check.sh --fast"
          WorkingDirectory="$(MSBuildThisFileDirectory).."
          IgnoreStandardErrorWarningFormat="true" />
  </Target>
</Project>
```

Staged handoff: verified source is based on `f854f8fac0d5bf9fd9e4719a7f603463469bfb99`. Before opening the PR, merge the then-current `origin/trunk` and repeat verification as required by the opening-stage packet. This staged delivery does not claim verification of later trunk landings.

## Self-review
Completed one round using `honua-flow-work/tools/lib/pr-review-rubric.md`. Existing endpoints and fixtures are reused, the exact cap assertion remains, and there is no runtime, dependency, security, or public API change. P0/P1: none found. P2/P3 follow-ups in this diff: none identified.

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
- [ ] Required fast gate completed successfully.
- [x] Conventional commit messages and required author/committer identity.
- [x] Existing repair issue linked above.
- [x] No PR opened and no CI/review polling; staged delivery only.
