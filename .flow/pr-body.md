test(core): cover adjacent tile-cache scheme elements
# COVERS: .NET Foundation Tests (Core)

## Issue Link
Refs #5884 (Core regression verification and coverage; the runtime repair already landed in PR #5887).
Refs #5627 (protect the tile-package parser introduced by PR #5823).

## Summary
The Core failures in full-matrix run 38135401612 came from PR #5823, landing `6ca720461ebe277a2fcec99f12fd99daa3f20fd1`. `ReadElementContentAsString()` advanced past the current element, then the surrounding `Read()` skipped the next sibling. Valid packages lost Y coordinates and LOD resolutions and failed strict scheme validation.

Current trunk already includes the fix-forward parser repair from PR #5887 (`01449eaed054a508e1fae691c768c26f0f2f19fb`). This change preserves that repair and adds eight regression cases covering adjacent authoritative WKIDs, 512-pixel tiles, both nested coordinate orders, scalar/CDATA origins, non-contiguous levels, and resolution/scale-only LODs. Existing tests and rejection checks remain intact.

Fix-forward is appropriate: five landings separate the last actual green (`96601c1`) from the packet head (`a6581e4`), exceeding the three-landing revert-first threshold, and the runtime repair has already landed on trunk.

## Changes Made
- Extend the exploded-package fixture with optional tile size and LOD XML while retaining its original defaults.
- Assert the resolved WKID, tile size, scheme, content type, and zoom range for each regression case.
- Verify only the Core family owned by this packet; retain all other shared-branch changes.

## Diagnosis Evidence
Read the [Core diagnosis comment](https://github.com/honua-io/honua-server/commit/a6581e4d332e65e1202b8d1b45956ef73b6390d7#commitcomment-205853197) and [culprit PR #5823](https://github.com/honua-io/honua-server/pull/5823) before executable bisection. The run artifact listing was checked first: the Core TRX/coverage artifacts have expired. Surviving [attempt-2 job logs](https://github.com/honua-io/honua-server/actions/runs/38135401612/job/114470640465) show exactly three failures in `EsriTileCachePackageReaderTests` (5,303 passed / 3 failed).

Focused Release verification passed all 33 `EsriTileCachePackageReaderTests`, including the eight new cases:

```bash
dotnet test tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj --configuration Release --filter FullyQualifiedName~EsriTileCachePackageReaderTests --logger 'trx;LogFileName=tile-reader-current.trx' --results-directory /tmp/trunkred-a6581e4-core-results
```

The isolated executable bisect ran the three original failing test bodies and fixtures against each commit's exact reader, descriptor, JSON context, scheme, interface, and canonical SRID predicates. It did not build the full historical Core projects.

| Commit | Reader tests |
| --- | --- |
| `96601c1` (green anchor) | 3 passed |
| `96dbdb2f7f` / PR #5822 | 3 passed |
| `722aa9a457` / PR #5824 | 3 passed |
| `6ca720461e` / PR #5823 | 3 failed: first red |
| `7a71b1ebe4` / PR #5828 | Same 3 failures |
| `a6581e4` / PR #5827 | Same 3 failures |

Every red TRX was checked for the expected `NotSupportedException` and missing-origin/resolution or WebMercatorQuad-mismatch message. Exact orchestration/validation commands:

```bash
python3 /tmp/trunkred-a6581e4-core-bisect.py
python3 /tmp/trunkred-a6581e4-core-validate-bisect.py
```

The harness executes `dotnet test /tmp/trunkred-a6581e4-core-bisect/ReaderBisect.csproj --configuration Release --filter FullyQualifiedName~EsriTileCachePackageReaderTests --logger 'trx;LogFileName=<commit>.trx' --results-directory /tmp/trunkred-a6581e4-core-bisect/results` for each table entry. TRX files and per-commit logs are under `/tmp/trunkred-a6581e4-core-bisect/`.

Pre-PR progress checkpoint: restore succeeded for the selected 105-project closure. Core, the corrected Core test assembly, database providers, protocol modules, and the sample Blazor project have compiled; additional OData, Scene, OGC API, and GeoServices component test assemblies have also compiled. The server and shared TestKit have now also compiled. The remaining test-assembly build is ongoing without reported errors. The pre-PR result and final focused rerun after the square-tile fixture correction are pending.

## Testing
- [x] Unit tests added/updated
- [ ] Integration tests added/updated
- [ ] Architecture tests pass
- [ ] Manual testing performed

## Gate Impact
- [x] PR gates (build, test, governance)
- [ ] Nightly gates (conformance, performance, security)
- [ ] Release gates (packaging, publishing)
- [ ] Deploy gates (promotion, post-apply validation)

## Docs or Contract Impact
- [x] None — no docs or contract impact

## Release/Deploy Impact
- [x] None — standard merge-and-release flow

## Breaking Changes
None.

## Self-review
One review round against the PR reviewer's rubric: correctness, dependency direction, public API documentation, security, shared pipelines, fixture defaults, and regression strength. No P0/P1 findings. One fixture consistency issue was corrected: 512-pixel test tiles now declare both rows and columns as 512. Its final verification is pending with the pre-PR check. No remaining P2/P3 findings.

## Known follow-ups
None identified in this diff.

## Pre-PR Checklist
- [ ] Ran `scripts/ci/pre-pr-check.sh --fast` and all selected checks passed
- [x] Commit messages follow conventional format
- [x] PR title matches the implementation commit
- [x] Issues linked with dispositions
- [x] Tests added for regression coverage

Staged delivery only. No PR was opened and no CI was polled.
