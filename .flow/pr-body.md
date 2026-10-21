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
dotnet test tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~EsriTileCachePackageReaderTests --logger 'trx;LogFileName=tile-reader-final.trx' --results-directory /tmp/trunkred-a6581e4-core-results
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

Pre-PR progress checkpoint: the selected 105-project Release build completed with zero errors. Two MSB3026 asset-copy warnings resolved on retry; copied assets match package originals by SHA-256. The final focused reader run passed 33/33, including all eight new cases. The script stopped after its initial 20-minute format-verification timeout and successful fix pass, which changed no files. Direct scoped format verification passed with zero changed files. The full selected Core suite passed 5,678/5,678 tests, with zero skips. Subsequent selected suites found their previously built DLLs had disappeared from the worktree, so verification is restarting under one normal lane reservation spanning build, format, and selected tests. No test failure was suppressed; the missing-output runner attempt exited nonzero before AI test discovery. The lane churn wrapper rejects the script's trailing `-- RunConfiguration.MaxCpuCount=1`; the SDK-supported equivalent `-p:VSTestCLIRunSettings=RunConfiguration.MaxCpuCount=1` preserves that setting without bypassing the lane locks or build cap.

Verification recovery checkpoint (2026-10-21 06:10 UTC): the packet is based on refreshed `origin/trunk` (`f854f8fac0`), and the delivery diff remains limited to this regression fixture and staged body. The corrected driver starts from the repository root under one normal lane reservation with host PID visibility. All 105 selected projects restored successfully; the warnings-as-errors Release build is running with no reported errors. The standard PATH shim, shared compiler, worktree locks, and four-node build cap remain enabled. `HONUA_BUILD_SLOT_WAIT=10800` extends only the queue wait window. The focused 33-case reader command will run again after the complete selected pre-PR checks.

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
One review round against the PR reviewer's rubric: correctness, dependency direction, public API documentation, security, shared pipelines, fixture defaults, and regression strength. No P0/P1 findings. One fixture consistency issue was corrected: 512-pixel test tiles now declare both rows and columns as 512. The corrected fixture passed the final focused run (33/33 tests). No remaining P2/P3 findings. `PYTHONIOENCODING=utf-8 python3 scripts/ci/local-architecture-review.py` passed on the refreshed base. Its nonblocking deep-inheritance warning is a reviewed false positive: the heuristic counts `: ` text (89 occurrences already on trunk, including named arguments), while this sealed test class declares no base class.

## Known follow-ups
None identified in this diff.

## Pre-PR Checklist
- [ ] Ran `scripts/ci/pre-pr-check.sh --fast` and all selected checks passed
- [x] Commit messages follow conventional format
- [x] PR title matches the implementation commit
- [x] Issues linked with dispositions
- [x] Tests added for regression coverage

Staged delivery only. No PR was opened and no CI was polled.
