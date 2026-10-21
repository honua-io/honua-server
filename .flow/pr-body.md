fix(studio): give publishable export fixture stable layer IDs

## Issue Link

Refs #3429 (dashboard acceptance remains scoped as documented in #4376; this repairs the trailing Server Tests regression only).

## Summary

Fix forward the **Server Tests (Server Features Analytics Studio Export and Reporting)** family from trailing CI run [33995298561](https://github.com/honua-io/honua-server/actions/runs/33995298561). Its TRX contains 265 passed and two failed tests; both fail during publication setup with `studio.composition.item.id.required`, before reaching their export authorization assertions.

The culprit is **#4376**, `1fbe91f2821311038901283dbb658b4b8deb483f` (dashboard composition validation), rather than the red head's #4378. Its new collection validation enforces the already-required stable string layer IDs. The publishable export fixture supplied only layer titles.

**Fix versus revert:** fix forward. The shard was already red at the previous trailing head `916e0486`; there are 13 first-parent landings between the last green trailing head `3463bf38` and `3acc113`, outside the fresh-red three-landing rule. The correction is two stable IDs in the fixture, preserving the composition validation and dashboard functionality.

## Changes Made

- Give the publishable map fixture's Parcels and Roads layers stable, distinct composition IDs.
- Document the publication requirement beside that fixture. Preserve every publication, export, ownership, version-pinning, PNG, and denial-audit assertion.

## Testing

- [x] Artifact-first diagnosis: last-green run [33989879534](https://github.com/honua-io/honua-server/actions/runs/33989879534) has 267/267 passed; red run 33995298561 has 265 passed and the two publication-setup failures.
- [x] Native Windows boundary reproduction: the two failing export tests pass at `05a54d264` (#4371, immediate parent), then both fail at `1fbe91f28` (#4376) with the exact CI diagnostic; zero skips in either run.

The same two tests were run at the last green head and each first-parent landing through the red head:

| Revision | Landing | Passed / failed |
| --- | --- | --- |
| `3463bf38f` | Last green trailing head | 2 / 0 |
| `88212f79a` | #3995 | 2 / 0 |
| `3a1ce3c41` | #4136 | 2 / 0 |
| `a7422bf14` | #4184 | 2 / 0 |
| `02f29aaf7` | #4193 | 2 / 0 |
| `9720c8350` | #4238 | 2 / 0 |
| `6abf928ea` | #4337 | 2 / 0 |
| `83cd5b8a3` | #4351 | 2 / 0 |
| `643d113fe` | #4357 | 2 / 0 |
| `825195ebb` | #4364 | 2 / 0 |
| `05a54d264` | #4371 | 2 / 0 |
| `1fbe91f28` | **#4376 â€” first red** | **0 / 2** |
| `916e0486b` | #4374 | 0 / 2 |
| `3acc113d1` | #4378 | 0 / 2 |

The tests are `ExportDeliverable_FlagOn_OwnerCanExportTheCurrentUnpublishedVersion` and `ExportDeliverable_FlagOn_NonOwnerCanExportOnlyThePublishedVersion` in `StudioPackageEndpointsTests`. No tests were skipped.

- [x] Final native Windows Release rebuild of `Honua.Server.Tests`: `TreatWarningsAsErrors=true`, `RunAnalyzers=true`, zero warnings and zero errors. Dependency build also passed with zero warnings and errors.
- [x] All 37 `StudioPackageEndpointsTests` passed, zero failed and zero skipped, including both original failures. Command: `dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~StudioPackageEndpointsTests`.
- [x] Changed-file `dotnet format --verify-no-changes --no-restore` passed; `git diff --check` passed.

The post-landing trailing matrix remains the family-wide proof.

All commands run through Windows PowerShell and the Windows .NET 10 SDK under the native `Global\HonuaBuildLock`, with Docker Desktop for Postgres. Historical builds use Release, warnings as errors, and disabled analyzers to reduce bisect cost; the final touched-project build explicitly enables analyzers.

## Gate Impact

- [x] PR gates (build, test, governance)
- [x] Nightly gates (Server Tests family in the trailing full matrix)

## Docs or Contract Impact

- [x] None â€” fixture correction only.

## Release/Deploy Impact

- [x] None â€” standard merge-and-release flow.

## Breaking Changes

None.

## Pre-PR Checklist

- [x] Ran `scripts/ci/pre-pr-check.sh` and all checks passed â€” native Windows equivalent: affected-project Release analyzer build, affected endpoint tests, and changed-file format verification. The host rule prohibits invoking Bash.
- [x] Integration-test fixture updated; all existing assertions retained.
- [x] Commit messages follow conventional format.
- [x] PR title matches the repair commit.
- [x] Issue disposition linked above.

## Recovered completion receipt (2026-10-21)

This repair already merged as #4385 on 2026-09-06 at 3bb02e8bea3b44ffdc88e2a570800c101c0c8189. Do not open a duplicate PR. The source correction remains on current origin/trunk. This recovery commit only archives the requested staged body.

The first post-merge trailing run 34006677885 passed Server Tests (Server Features Analytics Studio Export and Reporting), job 101419881519. The same family passed run 34022872366, job 101460065823; that entire run succeeded.

Verification evidence is preserved under C:/Users/mike/honua-io/trunkred-3acc113-server-tests-evidence. The tested source ba5ffb0f6dc5b448d6fd2c8b3aa89208aeb8999e and delivered source f742af80a46c134f536606cf28d1f41c5f17a166 both have tree 9a4006ac808c6cc0522d599dc3621fbf73035edf. Reused the completed native verification; no new build/test run during receipt recovery, because no code changed. The historical verification commands (run from the native bisect worktree) are:

```powershell
dotnet build tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj -c Release -maxcpucount:4 /p:TreatWarningsAsErrors=true /p:UseSharedCompilation=false /p:RunAnalyzers=false
dotnet build tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj -c Release --no-restore -t:Rebuild -maxcpucount:4 /p:BuildProjectReferences=false /p:TreatWarningsAsErrors=true /p:RunAnalyzers=true /p:UseSharedCompilation=false
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~StudioPackageEndpointsTests --logger 'trx;LogFileName=fix-focused.trx' --results-directory C:/Users/mike/honua-io/trunkred-3acc113-server-tests-evidence
dotnet format tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --no-restore --include tests/dotnet/Honua.Server.Tests/Features/Studio/StudioPackageEndpointsTests.cs --verify-no-changes
```

Self-review applied the PR reviewer rubric in .github/workflows/claude-review.yml and AGENTS.md: correctness, security, dependency direction, public API documentation, shared pipelines, endpoint coverage, and scope. P0/P1: none. P2/P3: none. The only source diff adds required stable IDs and an explanatory fixture comment; all existing assertions remain. Historical verification has 37 passed, zero failed/skipped; Release analyzer build has zero warnings/errors.

The daemon UNC receipt destination conflicts with the packet's hard Windows workspace-only host rule. A local JSON receipt is saved in the evidence directory; no WSL filesystem access was attempted.