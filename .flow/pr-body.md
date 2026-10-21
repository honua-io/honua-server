docs(ci): record Protocols trunk-red verification (#5633)
# COVERS: .NET Foundation Tests (Protocols)

## Issue Link
Refs #5633 (the implementation landed in #5819; the inherited Protocols assertion failure was already corrected by #5871).

## Summary
The Protocols failure at 96601c11282f04b375a72e39ec6891bc557a92e1 comes from #5819's new reflection-disabled JSON probe. Its STAC assertion required `13:05:09.123456`, while source-generated System.Text.Json emits the equivalent `13:05:09.1234560`. Current trunk already includes the fix-forward in ba3fd4021f7a7f65408109409c1c5dd5e0ffab8b (#5871): parse the serialized time invariantly and compare the complete TimeOnly value. This packet verifies that existing repair and stages the shared branch handoff; it adds no runtime or test change.

## Diagnosis
- Read the [Protocols diagnosis](https://github.com/honua-io/honua-server/commit/96601c11282f04b375a72e39ec6891bc557a92e1) and [culprit PR #5819](https://github.com/honua-io/honua-server/pull/5819) before investigating independently. The existing correction is [PR #5871](https://github.com/honua-io/honua-server/pull/5871).
- Checked run 38124910778 artifacts first: the Protocols TRX archive has expired (artifact 11698329028; seven-day retention). [Retained job 114441649495](https://github.com/honua-io/honua-server/actions/runs/38124910778/job/114441649495) logs show GeoServices 946 passed, one failed, and all seven other projects passing.
- The sole failure is `ReflectionDisabledAotJsonProbeTests.ReflectionDisabledChild_SerializesOperationsStoredQueriesEvidenceAndStacFilter`, with `STAC ids+filter property bag did not preserve attribute values`.
- The diagnosis's source-introduction bisect identifies #5819 as the first landing containing the failing probe. Earlier intermediate full families were not executed; no behavioral bisect result is claimed for them.
- Fix-forward rather than revert: six landings separate the last actual green b1955c1 from the target, exceeding the fresh-red three-landing criterion, and the exact correction already landed in #5871. Reverting #5819 now would remove the production reflection-disabled serializer repair.

## Changes Made
- Replace an inherited, unrelated staged PR description with this packet's Protocols handoff.
- Preserve the landed serialization repair, reflection canary, positive/negative filter cases, and full-value time comparison.
- Preserve other lanes' changes when synchronizing the shared branch.

## Testing
Focused regression: PASS, 1 passed / 0 failed / 0 skipped on .NET SDK 10.0.401, Release. TRX: `/tmp/trunkred-96601c1-protocols-verification/reflection-disabled.trx`. The child-process assertions confirm reflection is disabled, the reflection canary throws, all four repaired serialization paths succeed, STAC filters accept/reject the expected items, and the complete time value is preserved.

Final verification is pending after synchronizing original trunk history through f854f8fac0. The initial focused run above passed before that synchronization. Reservation waits expired before execution on earlier attempts. A pre-PR attempt reached the test phase and exposed a local command-recorder argparse bug: it rejects the VSTest `-- RunConfiguration.MaxCpuCount=1` separator.

A task-local `/tmp/trunkred-96601c1-tools/dotnet` adapter replaces only that exact trailing pair with `--settings /tmp/trunkred-96601c1-tools/single-cpu.runsettings`, then execs `/home/mike/honua-io/honua-flow-work/tools/lane-bin/dotnet`. The runsettings XML is `<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount></RunConfiguration></RunSettings>`. The recorder, reservation, MSBuild cap of four, and test parallelism setting of one are retained. No repository script or test is modified. The final focused run and adapted pre-PR run are queued using command escalation.

Exact commands:
```bash
dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --filter FullyQualifiedName~ReflectionDisabledAotJsonProbeTests --logger 'trx;LogFileName=reflection-disabled.trx' --results-directory /tmp/trunkred-96601c1-protocols-verification
dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --no-restore -p:TreatWarningsAsErrors=true --filter FullyQualifiedName~ReflectionDisabledAotJsonProbeTests --logger 'trx;LogFileName=reflection-disabled-final.trx' --results-directory /tmp/trunkred-96601c1-protocols-verification
PATH="/tmp/trunkred-96601c1-tools:$PATH" scripts/ci/pre-pr-check.sh --fast > /tmp/trunkred-96601c1-pre-pr-verified.log 2>&1
git diff --check
```
No C# project is changed, so project formatting has no applicable files. Only the focused class and checks selected by the pre-PR script are authorized for this packet.

## Self-review
One self-review round completed using the PR reviewer rubric: reviewed the staged diff, culprit-to-fix assertion change, generated serializer metadata, and the child-process assertions. No P0/P1 findings. Operational P2 follow-up: repair the local command recorder's forwarding of the VSTest `--` separator; the equivalent runsettings adapter permits verification within this packet's scope. No P3 follow-ups. This packet changes only the staged description. The complete time-value assertion, reflection-disabled state/canary, positive and negative filters, JSON property checks, and zero child exit code remain present. The focused verification result is recorded above; the required pre-PR command is pending.

## Gate Impact
- [x] PR gates (build, test, governance)
- [ ] Nightly gates (conformance, performance, security)
- [ ] Release gates (packaging, publishing)
- [ ] Deploy gates (promotion, post-apply validation)

## Docs or Contract Impact
- [x] None — no product docs or contract change

## Release/Deploy Impact
- [x] None — no runtime change

## Breaking Changes
None.

## Staged handoff
Do not open a new Protocols-only repair PR for this already-landed fix. This description records verification for the shared fix/trunkred-96601c1 branch; the opener should combine any remaining lanes' independently verified coverage. No PR was opened and no CI polling was performed by this packet.
