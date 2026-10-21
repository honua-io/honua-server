chore(ci): verify the existing MCP REST certification startup repair
# COVERS: MCP Certification (rest)

## Issue Link
Refs #5920 (this packet verifies the startup repair already landed in #5938; full live certification remains the trailing matrix responsibility).

## Summary
The historical REST job at 54053e5 failed before certification started. Current trunk already contains the fix-forward in #5938 (5873a38cbe6e5362f1a5670da4e6ad3e30b946dc). This staged handoff records verification of that existing repair; it adds no runtime change and does not claim a new live certification receipt.

## Diagnosis and culprit
Read the [diagnosis comment](https://github.com/honua-io/honua-server/commit/54053e5a0453d02cd4d0df71ecad3aa510dac279#commitcomment-205852358) and culprit [PR #5921](https://github.com/honua-io/honua-server/pull/5921) before investigating. Artifact inventory for run 38246573627 contains no MCP REST receipt/TRX. REST job 114828569367 fails in setup; the logs report that HONUA_DEV_AUTH is forbidden outside Test. Certification and seeding never start.

The diagnosis's adjacent historical runs and eight-landing source comparison establish the first red landing as 36fa28bab37e64b68b91ad4de1e6df77a8c77d39 (#5921), rather than the matrix head. Its predecessor 0e03bdd06b and the last green 44c56748 only passed through the absent-script skip. #5921 activates the pinned SDK scripts and moves HONUA_DEV_AUTH=true to job scope, contaminating the Development server process. No failing certification test exists to bisect. No redundant local landing bisect was performed.

Fix forward is appropriate because eight landings separate the last actual green and the historical red, and the repair has already landed. Reverting #5921 would also restore a skip instead of live coverage. #5938 removes the legacy bypass from certification and smoke scopes, preserves Development and explicit admin authentication, and adds a regression fixture wired into the CI router. Subsequent published SDK pin corrections remain intact.

## Changes Made
- Stage the historical diagnosis, existing repair identity, local verification, and self-review outcome for the requested branch.
- Preserve the current trunk implementation and all other lanes' changes.

## Testing

- [x] Manual verification of existing regression coverage performed
- [ ] Architecture suites completed

- `python3 scripts/ci/fixtures/validate-mcp-server-auth.py` — passed all 7 environment/mutation checks again on the merged tree.
- `python3 -m unittest scripts.certification.test_verify_mcp_live_certification` — passed 4 tests again on the merged tree.
- `dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter FullyQualifiedName~ConfigurationValidationServiceTests --logger 'trx;LogFileName=mcp-rest-configuration.trx'` — passed 30 tests, zero failures/skips, Release, on the original branch baseline.
- `scripts/ci/pre-pr-check.sh --fast` — Release build passed (36 roots, 107 projects in the evaluated closure, zero errors). The combined four-project formatter could not finish loading its MSBuild workspace within its cap; its automatic retry also exited 124. No formatting violation was reported. A separate scoped formatter admission retry exited 75 after its queue wait expired; no formatter started during that attempt. Equivalent per-project checks are being retried without changing their 20-minute execution cap. The selector’s unchanged remaining test actions are pending.

Current trunk f854f8fac0d5bf9fd9e4719a7f603463469bfb99 was merged without conflict before final verification. Its alerts and ImageServer changes are preserved. The focused configuration class will also be rerun against the merged build.

The first sandboxed .NET/preflight attempts were stopped before compilation because shared build admission could not establish live guardians. Host inspection proved three reservations belonged to a vanished PID namespace and both original locks were free; those stale reservations were reconciled, and the admitted PATH-shim retry passed. The shim retained its CPU cap and shared compilation. This packet changes no C# files. Formatting covers only the four carried-forward trunk files, in their owning projects with `--include` and a 20-minute execution cap. Shared worktree/global admission is acquired through the existing lane reservation helpers before starting that cap; nested dotnet calls retain the PATH shim, CPU cap, and shared compilation. The build reported five recovered MSB3026 copy retries; each destination matched its package source by SHA-256. No local live MCP certification run or new live receipt is claimed.

## Self-review
Review scope: this handoff diff and the existing authentication repair, using honua-flow-work/tools/lib/pr-review-rubric.md. No new endpoint, dependency, public type, authentication bypass, test relaxation, or protocol pipeline is introduced. The inherited environment fixture checks both certification and smoke against the shared Development action and rejects bypass and credential mismatch mutations. Live certification was not run locally; the trailing full matrix remains the proof of the complete family.

P0/P1: none identified. P2/P3 follow-ups: none introduced by this handoff.

## Gate Impact
- [x] Nightly gates (MCP REST certification evidence only)
- [ ] PR gates (no implementation changes)

## Docs or Contract Impact
None. This is a staged delivery handoff.

## Release/Deploy Impact
None — existing repair already landed; no new environment variables, migration, SDK pin, or deployment changes.

## Breaking Changes
None
