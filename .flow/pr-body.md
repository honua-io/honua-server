test(geoservices): parallelize FeatureServer query-parameter tests
## Issue Link
Related to honua-io/honua-server#3204 (CI efficiency plan, Phase 6.1).

## Summary
Move `FeatureServerQueryParameterTests` from the serial `Database` collection to the sibling `Database.GeoServicesFeatureServerQuery` collection. The current test project is `Honua.Protocols.GeoServices.Tests`; the source moved there after the original packet was written.

## Changes Made
- Add a parallelizable collection definition using the existing shard-collection template, with no collection fixture.
- Change only the target class's collection attribute. Retain its per-class `IClassFixture<WebAppFixture>` and every test body, attribute, case and assertion.
- The class only reads its own fixture seed through GET/POST query requests. Fixture schema routing and the schema-partitioned metadata graph provide the isolation it needs.

## Testing
Fresh Windows verification after merging trunk `a58c6b5a73` is in progress. The baseline build passed with zero warnings/errors in 1197.023 s; the completed baseline shard passed 375/375 cases in 3097.524 s, with zero deadlock signatures in the test and PostgreSQL logs. The target class passed 60/60 cases. The migrated-state build also passed with zero warnings/errors in 768.024 s. Byte comparison against trunk passed: only the collection attribute differs in the complete target source. After-run #1 is running: 250 cases passed so far, including all 60 target cases in the parallel phase, with no failure or deadlock signature; do not publish this intermediate draft as a verified handoff.

The native helper acquires `Global\HonuaBuildLock`, sets `DOTNET_PROCESSOR_COUNT=2`, builds Release with warnings as errors, and uses a fresh CI-pinned PostGIS 16 container for each run. The before build temporarily restores only `[Collection("Database")]` and restores the source bytes in `finally`. Run timings exclude build and container startup.

Exact invocations (PowerShell):
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/mike/honua-io/query-parameters-resume-20261022.ps1 -Phase BeforeBuild
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/mike/honua-io/query-parameters-resume-20261022.ps1 -Phase Before
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/mike/honua-io/query-parameters-resume-20261022.ps1 -Phase Build
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/mike/honua-io/query-parameters-resume-20261022.ps1 -Phase After1
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/mike/honua-io/query-parameters-resume-20261022.ps1 -Phase After2
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/mike/honua-io/query-parameters-resume-20261022.ps1 -Phase Format
python C:/Users/mike/honua-io/query-parameters-summary-20261022.py
python C:/Users/mike/honua-io/query-parameters-preserve-20261022.py
```

- [x] Integration test wiring updated; test verification pending.

## Gate Impact
- [x] PR gates: collection scheduling only. No gate, tier, timeout or test selection changes.

## Docs or Contract Impact
- [x] None.

## Release/Deploy Impact
- [x] None.

## Breaking Changes
None.

## Self-review
Applied the PR reviewer rubric in `.github/workflows/claude-review.yml` and the architecture checklist in `AGENTS.md` to the complete diff and fixture lifecycle. No P0/P1 code finding: collection registration is unique, per-class fixtures remain intact, there is no global mutation in the class, and XML documentation follows the template. Runtime verification remains pending.

## Follow-ups
- P3: prior attempts encountered `FileBackedLicenseService.StopAsync` teardown cancellation in other shard classes. Keep any reproduction visible; do not weaken tests to hide it.

Refs honua-io/honua-server#3204
