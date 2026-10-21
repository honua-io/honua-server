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
Verification commands and results are recorded below after completion.

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
