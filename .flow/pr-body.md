docs(ci): record Protocols trunk-red verification at 96601c1
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
Verification results will be finalized after the local commands complete.

Exact commands:
```bash
dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --filter FullyQualifiedName~ReflectionDisabledAotJsonProbeTests --logger 'trx;LogFileName=reflection-disabled.trx' --results-directory /tmp/trunkred-96601c1-protocols-verification
scripts/ci/pre-pr-check.sh --fast
git diff --check
```
No C# project is changed, so project formatting has no applicable files. Only the focused class and checks selected by the pre-PR script are authorized for this packet.

## Self-review
One self-review round completed using the PR reviewer rubric: reviewed the staged diff, culprit-to-fix assertion change, generated serializer metadata, and the child-process assertions. No P0/P1 findings; no P2/P3 follow-ups. This packet changes only the staged description. The complete time-value assertion, reflection-disabled state/canary, positive and negative filters, JSON property checks, and zero child exit code remain present. Final verification results are recorded above once commands complete.

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
