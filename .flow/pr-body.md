docs(ci): record superseded MCP grpc-web startup repair

# COVERS: MCP Certification (grpc-web)

## Issue Link

Refs #5921 (historical culprit landing; startup repair already delivered by #5938).
Refs #5920 (live certification qualification remains separate from this startup repair).

## Summary

The historical grpc-web failure at `0f663b066900658f035c07e303f47116d99613c5` is already repaired on current trunk by `5873a38cbe6e5362f1a5670da4e6ad3e30b946dc` (#5938). This staged branch records the diagnosis and local verification; it needs no additional runtime or workflow patch.

Culprit: #5921 / `36fa28bab37e64b68b91ad4de1e6df77a8c77d39`, which first exposed job-level `HONUA_DEV_AUTH=true` to a Development server while activating actual SDK certification. Source replay at every landing from the last green through the target confirms the first rejected startup environment. The earlier green jobs skipped certification; no certification test ran in the failed target job. Its artifact inventory has no MCP receipt/TRX, and the job log confirms deliberate configuration rejection before readiness.

Fix versus revert: retain the already-landed fix forward in #5938. This packet is superseded, so reverting #5921 now would remove active certification scripts, fixture repairs and receipt validation without addressing a remaining occurrence of the diagnosed startup bug. No production guard or test is weakened.

## Changes Made

- Record the artifact-first diagnosis, culprit boundary, existing upstream repair and evidence limits.
- Preserve the current SDK pin and all other lanes' work on the shared branch.
- Stage this handoff without opening a PR, dispatching CI or polling CI.

## Testing

- [x] Manual testing performed: seven existing MCP environment regression checks and four certification-receipt unit tests pass.
- [ ] Unit tests added/updated (no test changes needed).
- [ ] Integration tests added/updated (no runtime changes).
- [ ] Architecture tests pass (no new managed-code result claimed).

Commands run from `/home/mike/honua-io/wt-trunkred-0f663b0-mcp-certification-grpc-web`:

- `python3 scripts/ci/fixtures/validate-mcp-server-auth.py` — passed all 7 checks.
- `python3 -m unittest scripts.certification.test_verify_mcp_live_certification` — passed all 4 tests.
- `python3 /tmp/trunkred-0f663b0-startup-boundary.py` — passed the source-only environment replay at the last green, all three landings, the existing repair and current HEAD. No server startup or C# execution is claimed.
- `git merge-base --is-ancestor 5873a38cbe6e5362f1a5670da4e6ad3e30b946dc HEAD` — passed.
- `git diff --check origin/trunk` — passed.
- `scripts/ci/pre-pr-check.sh --fast` — initial run failed in the unrelated early-kill timing fixture. A focused unmodified rerun reproduced it. A trace showed that the lane sleep wrapper added about 1.8 seconds to the fixture's 0.01-second poll.
- `mkdir -p /tmp/trunkred-0f663b0-tools` and `ln -s /usr/bin/sleep /tmp/trunkred-0f663b0-tools/sleep` — prepare native sleep for timing-sensitive fixtures while retaining the lane dotnet shim and its reservations/cap.
- `PATH="/tmp/trunkred-0f663b0-tools:$PATH" python3 scripts/ci/test-shard-execution-evidence.py` — passed all 8 tests; no assertions or deadlines changed.
- `PATH="/tmp/trunkred-0f663b0-tools:$PATH" scripts/ci/pre-pr-check.sh --fast > /tmp/trunkred-0f663b0-pre-pr-native-sleep.log 2>&1` — pending; the complete local pre-PR run is still active, so no pass is claimed yet.
- `timeout 20m dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter FullyQualifiedName~ConfigurationValidationServiceTests --logger 'trx;LogFileName=mcp-configuration.trx' --results-directory /tmp/trunkred-0f663b0-verification` — attempted both sandboxed and escalated, then canceled (exit 143) before execution because shared build reservations were unavailable. No restore, build or .NET test pass is claimed.

The fast check selected `CI-SHELL-ONLY` automatically for the documentation diff, before generating this staged metadata. No managed projects changed; no dotnet format run was needed. Logs remain under `/tmp/trunkred-0f663b0-pre-pr*.log` on this host.

## Gate Impact

- [x] None — documentation and staged handoff only; the existing full trailing matrix remains the live-certification authority.

## Docs or Contract Impact

- [x] Documentation updated: historical diagnosis and verification limits.
- [ ] OpenAPI, Protobuf/gRPC or control-plane SDK contract changed.

## Release/Deploy Impact

- [x] None — no new runtime, dependency, seed or deployment change.

## Breaking Changes

None.

## Self-review

Completed one review using `honua-flow-work/tools/lib/pr-review-rubric.md`, limited to this branch's diff. Checked diagnosis against the original artifacts, job steps/log and culprit PR; verified the repairing commit is an ancestor of the latest fetched trunk. No P0/P1 findings. The diff adds no protocol implementation, public API, infrastructure type or endpoint, so the rubric's dependency, XML-documentation and endpoint-test requirements introduce no additional work.

P2 follow-up: qualify the actual candidate through the full trailing grpc-web matrix and retain its live receipt. Local environment/source checks are not certification credit. P3 operational follow-up: preserve native sleep semantics for timing-sensitive fixtures in lane tooling; no lane-tool changes are included here. No P3 source-code findings.

## Pre-PR Checklist

- [x] Commit messages use conventional format and the required author/committer identity.
- [x] Culprit and upstream repair linked with dispositions.
- [x] No tests weakened or deleted.
- [x] No PR opened and no CI polling performed.
