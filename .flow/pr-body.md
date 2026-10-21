chore(ci): verify the existing MCP REST certification startup repair
# COVERS: MCP Certification (rest)

## Issue Link

Refs #5920 (startup repair already landed in #5938; this handoff verifies that repair. Complete live certification remains the trailing matrix responsibility).

## Summary

The REST job at 54053e5 failed during server startup, before certification ran. Trunk already contains the fix-forward in #5938 (5873a38cbe6e5362f1a5670da4e6ad3e30b946dc). This branch records the diagnosis and local verification of that existing repair. Its only change relative to current trunk f854f8fac0d5bf9fd9e4719a7f603463469bfb99 is this staged handoff.

## Diagnosis and culprit

Read the [diagnosis comment](https://github.com/honua-io/honua-server/commit/54053e5a0453d02cd4d0df71ecad3aa510dac279#commitcomment-205852358) and culprit [PR #5921](https://github.com/honua-io/honua-server/pull/5921) first. Artifact inventory for run 38246573627 contains no MCP REST receipt/TRX. REST job 114828569367 fails in setup: HONUA_DEV_AUTH is forbidden outside Test. Seeding and certification never start.

The diagnosis's adjacent historical runs and the eight-landing source comparison establish the first red landing as 36fa28bab37e64b68b91ad4de1e6df77a8c77d39 (#5921), rather than the matrix head. Its predecessor 0e03bdd06b and last green 44c56748 passed through an absent-script skip. #5921 activates the pinned SDK scripts and moves HONUA_DEV_AUTH=true to job scope, reaching the Development server process. No failing certification test exists to bisect. No redundant local landing bisect was performed.

Fix forward applies because eight landings separate the last actual green and the historical red, and the repair has already landed. Reverting #5921 would restore a skip instead of live coverage. #5938 removes the legacy bypass from certification and smoke, retains Development with explicit admin authentication, and adds a regression fixture wired into the CI router. Published SDK pin corrections remain intact.

## Changes Made

- Record the historical diagnosis, existing repair, verification results and self-review for the staged handoff.
- Preserve all current trunk changes, including the alerts and ImageServer fixes integrated before final verification.
- Introduce no runtime changes, test changes, dependency changes or authentication exceptions.

## Testing

- [x] Manual verification of existing regression coverage performed
- [ ] Unit/integration tests added or updated (no implementation change)
- [ ] Full architecture suites completed

Passed:

- `python3 scripts/ci/fixtures/validate-mcp-server-auth.py` — 7 environment/mutation checks, rerun on the merged tree.
- `python3 -m unittest scripts.certification.test_verify_mcp_live_certification` — 4 tests, rerun on the merged tree.
- Replayed the current auth fixture against `git show 36fa28bab37e64b68b91ad4de1e6df77a8c77d39:.github/workflows/ci.yml` — rejected with the exact forbidden Development bypass assertion. Current workflow passes.
- `dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter FullyQualifiedName~ConfigurationValidationServiceTests --logger 'trx;LogFileName=mcp-rest-configuration.trx'` — 30 passed, zero failures/skips, on the original 3eddae25f2 branch baseline. The four subsequent upstream C# files do not change this class or the configuration validator. This focused test was not rerun after the merge.
- `scripts/ci/pre-pr-check.sh --fast` — its Release build passed: 36 roots, 107 projects in the evaluated closure, zero errors. Five recovered MSB3026 copy retries were checked: each destination matched its package source by SHA-256.
- `scripts/ci/pre-pr-check.sh --fast --dry-run --plan-json /tmp/mcp-rest-final-plan.json` — passed; retained the full selector plan without reducing filters or coverage.
- `PYTHONIOENCODING=utf-8 python3 scripts/ci/local-architecture-review.py` — exited 0 and skipped because this packet changes no C#; this does not represent an architecture-suite execution.
- `git diff --check` — passed.

Verification limitation: the complete fast pre-PR gate did **not** pass. Its scoped formatter lost its MSBuild RPC server while loading the workspace; the automatic retry exited 124 at the 20-minute cap, before the gate's test phase. A separate scoped retry also timed out, and a later admission attempt exited 75 without starting a formatter. The final optional queued retry was stopped before execution. No formatting violation or source modification was reported. All format attempts were limited to the four upstream C# files and their owning projects with `--include` and `timeout 20m`; no solution-wide formatter was run. No equivalent complete gate or full architecture-suite pass is claimed.

The PATH dotnet shim retained its CPU cap and shared compilation. Initial sandbox admission failed; host inspection proved three old reservations belonged to a vanished PID namespace and both original locks were free before those stale receipts were reconciled. Subsequent admitted Release build and focused test succeeded. Live reservations belonging to other lanes were preserved. No local live MCP certification run or new live receipt is claimed.

## Self-review

Reviewed the final handoff diff and the existing auth repair using `honua-flow-work/tools/lib/pr-review-rubric.md`. No endpoint, public API, dependency direction, shared protocol pipeline, authentication behavior or test assertion changes in this branch. The inherited fixture checks certification and smoke against the shared Development action and rejects bypass and credential mismatch mutations.

P0/P1: none identified in the delivery diff. P2 follow-up: repeat the complete fast pre-PR gate when shared admission and the formatter workspace are available; retain the unchecked gate result until then. P3: none. The trailing full matrix remains the proof of complete live certification.

## Gate Impact

- [x] Nightly gates (MCP REST certification evidence)
- [ ] PR gates (no implementation changes)

## Docs or Contract Impact

- [x] None — staged delivery handoff only

## Release/Deploy Impact

- [x] None — existing repair already landed; no new environment variable, migration, SDK pin or deployment change

## Breaking Changes

None

## Pre-PR Checklist

- [ ] Ran `scripts/ci/pre-pr-check.sh` and all checks passed (Release build passed; formatter RPC failure/time cap prevented completion, as detailed above)
- [x] Commit message follows conventional format
- [x] PR title matches the delivery commit message
- [x] Issue linked with disposition
