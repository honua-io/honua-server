docs(operate): replay Operate on accepted pin 93cad86 and document rollback completion proof (#3302)
# Pull Request

## Issue Link
Refs #3302 (released: the finding-driven joined deployment transcript and an authenticated Admin CLI execution need a deployment finding producer and an installed CLI session on the candidate that this lane could not run. AWS ECS-small placement replay is billable live AWS work, carried by honua-io/honua-iac#196, now a 2026.2 follow-on. Everything else that can be executed on the accepted pin is delivered here.)

## Summary
The accepted platform pin moved to honua-server `93cad86` (`sha256:785de2ae…`, honua-release trunk `8aa2bf63`), but every Operate receipt was bound to `548b7a5`. This PR replays the three installed Operate harnesses on that pin. It also produces the first deployment-actuator evidence on an accepted pin: a Local Docker protected-recovery replay on `honua-yarp-rolling`, plus the honua-devops client against the same server.

The replay found a customer-facing gap. Since #3892 a rollback reports `RolledBack` only after a functional query proves the restored target. The parameters that configure that query (`rollback.functional_query.*`) were documented nowhere. A self-hosted operator following the docs gets every automatic rollback stuck at `RollbackRequested`, then `ManualInterventionRequired` (`rollback.functional_query_unproven`). This PR documents it.

## Changes Made
- `docs/guides/deploy/upgrade-and-rollback.md`: new **Rollback completion proof** section covering:
  - the three conditions (prior identity, healthy endpoint, functional query);
  - the `rollback.functional_query.*` and `telemetry.golden_query.*` parameters and their precedence;
  - which ones are required;
  - the rollback window;
  - the terminal reason codes.

  Two troubleshooting bullets are corrected or added.
- `docs/guides/operate/scenario.md`: the Local Docker rollback row names `honua-yarp-rolling` as a real actuator. It also states its preconditions: the host container runtime and the completion-proof parameters.
- `docs/guides/operate/README.md`: the rollback taxonomy says when `RolledBack` is reported.
- New receipts in `docs/guides/operate/evidence/`:
  - `3302-accepted-93cad86-staging.json`: 5/5 scenarios.
  - `3302-accepted-93cad86-read-observation.json`: passed, 129 descriptors.
  - `3302-accepted-93cad86-approval.json`: 5/5 checks.
  - `3302-accepted-93cad86-protected-recovery.json`: 10/11 original cells pass, plus a satisfiable wrong-body cell. DevOps live tests pass 5/5. The receipt embeds every harness adaptation as a diff and the SHA-256 of each raw transcript.
- `docs/internal/contributor/operate-docs-precut-evidence.md`: new "October 11 accepted-pin replay" section and an updated acceptance disposition.

## Testing
- [ ] Unit tests added/updated
- [ ] Integration tests added/updated
- [ ] Architecture tests pass
- [x] Manual testing performed

Installed replays on Docker Desktop for Windows. Each run pulled `ghcr.io/honua-io/honua-server@sha256:785de2ae7f72e6277ce62aa1ece4f08ee0425970b71c47f9f4843c0baae2c8db` and checked its RepoDigest and OCI revision label `93cad86ec4fb28fba3ff0b9358059d86aa55dde5` before boot. The manifest was the unmodified release `platform-manifest.yaml` (SHA-256 `bf2a63e6…`).

```
python scripts/qualification/metadata-release-installed.py --manifest <platform-manifest.yaml> --output <dir>
python scripts/qualification/operate-read-observation.py --manifest <platform-manifest.yaml> --accepted-manifest --output <dir>
python scripts/certification/prove-admin-approve-candidate.py --image ghcr.io/honua-io/honua-server@sha256:785de2ae… --source-revision 93cad86ec4fb28fba3ff0b9358059d86aa55dde5 --output <file>
# honua-devops 28a6419f certification/protected-recovery, adapted as recorded in the receipt:
PROOF_SERVER_IMAGE=<digest ref> PROOF_DOCKER_CLI=<linux docker cli> MSYS_NO_PATHCONV=1 boot.sh up
python journey.py --work $PROOF_WORK --out part1.json --cells platform-admin-cross-tenant,fenced-recovery,tenant-bound-recovery,error-rate-regression,latency-regression
python journey.py --work $PROOF_WORK --out part2.json --cells missing-telemetry,stale-telemetry,controller-crash,failed-recovery,wrong-body-regression,newer-intent
python journey.py --work $PROOF_WORK --out part3.json --cells wrong-body-regression-satisfiable
HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY=true ... dotnet test tests/Honua.DevOps.Agent.Tests --filter "FullyQualifiedName~ProtectedRecoveryLiveJourneyTests"   # 5 passed
boot.sh down
```

`wrong-body-regression` fails as written: its golden-query `expected_contains` and `forbidden_contains` are the same marker. Since #3892 those keys also bind the restored-replica proof, so recovery can never be proven. It settled `Failed` (`rollback.functional_query_failed`), which is the documented rule, not a server defect. The candidate refusal half held: it was refused before cutover and attributed to the golden-query verdict. Its satisfiable variant passes.

Docs gates, run locally:
- `python scripts/ci/check-doc-links.py` passed: 2547 targets, 12 allowlisted warnings, 13 code-referenced anchors.
- `check-doc-links.test.py` passed 26 tests.
- `check-doc-example-surfaces.py --base origin/trunk`, `check-okf-bundle.py` (268 concepts), `generate-llms-txt.py --check` and `generate-capability-concepts.py --check` all passed.
- `git diff --check` is clean.

`scripts/ci/pre-pr-check.sh` stops on Windows in `validate-testcontainer-images.py`, because Windows Python pipes CRLF into `bash -n`. That is unrelated to this docs-only change; CI runs it on Linux.

## Gate Impact
- [x] PR gates (build, test, governance)
- [ ] Nightly gates (conformance, performance, security)
- [ ] Release gates (packaging, publishing)
- [ ] Deploy gates (promotion, post-apply validation)
- [ ] None — no gate impact

## Docs or Contract Impact
- [x] Documentation updated
- [ ] OpenAPI spec changed
- [ ] Protobuf/gRPC contract changed
- [ ] Control plane SDK surface changed
- [ ] None — no docs or contract impact

## Release/Deploy Impact
- [x] None — standard merge-and-release flow
- [ ] Requires coordinated release across repos
- [ ] Requires database migration
- [ ] Requires infrastructure changes
- [ ] Requires environment variable or secret changes

## Breaking Changes
None

## Follow-ups (not in this PR)
- honua-devops `certification/protected-recovery`: add the `rollback.functional_query.*` parameters and a satisfiable `wrong-body-regression`. Make `boot.sh` take a Linux CLI path so it runs on Docker Desktop. Accept 409 `recovery_fence_protection_window_absent` for a settled replay (#4989). Without these, the harness cannot certify any image newer than #3892.
- The pinned image still omits `sourceRevision` from the admin version response.

---

## Pre-PR Checklist
- [ ] Ran `scripts/ci/pre-pr-check.sh` and all checks passed (stops on a Windows-only CRLF issue; see Testing)
- [x] Commit messages follow conventional format: `type: description (#issue)`
- [x] PR title matches main commit message
- [x] Issue number linked above
- [ ] Tests added for new functionality
- [ ] If protocol/auth behavior changed: updated compatibility contract
- [ ] If breaking admin/control-plane API changes: updated migration guide
- [ ] `OPENAPI_BREAKING_CHANGE_APPROVED` — intentional Admin API break acknowledged for this PR
