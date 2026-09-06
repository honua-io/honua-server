# Operate documentation: pre-cut evidence disposition

Issue: [#3302](https://github.com/honua-io/honua-server/issues/3302).
Source reviewed: `0d0829f2d` (September 6, 2026), including the merged
[initial runbook PR #4364](https://github.com/honua-io/honua-server/pull/4364).
This combines a source/receipt review with focused deployment-proposal fixtures.
The fixtures exercise the MCP adapter with explicit source envelopes and recording
gateway seams; they are not a live deployment or candidate transcript.

## Acceptance disposition

| Acceptance / verification | Disposition |
|---|---|
| Cross-surface deployment/readiness scenario | Documented REST reads, MCP calls, proposal polling, separate Admin CLI approval, optional Console inspection, fix-forward verification and backend rollback truth. |
| Freshness, completeness, backend identity and coverage | Documented source envelopes and failure behavior. #3475's executed Windows outage receipt is linked below. The deployment adapter fixtures cover stale, partial, unavailable, unverified and not-configured envelopes; live deployment-source collection remains a distinct evidence obligation. |
| Generic model proposal-only boundary and negative authorization | Source references below; #3411, #3430, #3431 and #3474 are closed as checked September 5. Their old open-blocker claims were removed. |
| One joined typed actuator receipt and convergence window | Released to candidate replay: requires the actual registered backend, verification policy and immutable candidate. No fabricated successful receipt or generic window. |
| Exact Local Docker and ECS-small route/tool/CLI/rollback replay | Released to candidate replay: release #231 is open; the current platform manifest explicitly says “THIS IS NOT THE RELEASE CUT.” Source-build observations cannot satisfy this criterion. |
| GitBook/docs validation | Run the repository documentation link/anchor and example-surface gates. Hosted GitBook preview is separately reported by the PR checks. |
| honua-site #185 links without broadening claims | [Site PR #275](https://github.com/honua-io/honua-site/pull/275) adds the two links without widening the claim. Pages validation and CodeQL pass at `eb6bff9`; merge/deployment remain separate from this server PR. |

The release promise is the bounded terminal Operate journey in the 2026.1
quality contract. This disposition preserves the must-fix-before-cut ruling;
only checks that intrinsically require exact candidate bytes are released to
candidate replay. Missing deployment-path evidence and site work remain visible rather
than being counted as passed or silently removed.

## Authorization and freshness proof map

These are existing fixtures/assertions, the focused proposal-path additions,
and the separately executed #3475 receipt to join to the eventual candidate replay.
Paths are relative to the repository root.

| Requirement | Source evidence |
|---|---|
| Backend outage, never-succeeded, stale, fresh-attempt/stale-observation and future observations make zero gateway calls for the alert-dispatch finding | `tests/dotnet/Honua.Server.Tests/Features/Infrastructure/Monitoring/OpsFindingsServiceTests.cs`: `Propose_IncompleteRequiredSourceEvidence_BlocksWithZeroGatewayCalls` invokes `ProposeAsync` for these five fixtures and asserts no `RouteAsync` call. This is not a deployment-target replay. |
| Deployment proposal-path suppression | `tests/dotnet/Honua.Server.Tests/Features/Infrastructure/Monitoring/McpPlatformOpsReaderTests.cs`: `ProposeFinding_IncompleteDeploymentEvidence_BlocksBeforeProposalOrActuation` passes five fixed-time deployment source envelopes through `ProposeFindingAsync`. It asserts literal expected completeness and blocked outcome, retained diagnostic target, null proposal/execution IDs, zero proposal/direct-route calls and zero canonical operation acceptance calls. The source and gateway are test seams: this proves the adapter gate, not live backend collection or durable actuator behavior. |
| Complete evidence remains proposal-only | In the same file, `ProposeFinding_CompleteDeploymentEvidence_SealsOnlyTheBoundAction` asserts one approval-proposal call, zero direct-route calls, no execution ID, and the independently specified proposer, finding ID, target and desired revision. It does not substitute a recording gateway for the required durable receipt. |
| Validity and coverage are source-derived | `tests/dotnet/Honua.Server.Tests/Features/Infrastructure/Monitoring/EvidencePostureTests.cs`: fixed-time stale/future/malformed/replica/component fixtures; complete aggregate uses its oldest observation. |
| Real backend outage and recovery | [Executed Windows receipt](../../guides/operate/evidence/3475-windows-outage.json), source `ee4f744a66491e7aa72e3efd22bf845e11b5869a`, plus [native runner instructions](../../guides/operate/evidence-posture.md#native-windows-receipt). It records complete → unavailable → complete for `honua_ops_findings.alert_dispatch`, independently seeded pending/dead-letter counts of 0/1, REST/MCP outage parity, no new proposals and unchanged dispatch rows. `candidateQualification=false`: it proves that source outage, not deployment-actuator convergence or all incomplete cases. The opt-in `EvidencePostureLiveTests.cs` remains the deployed harness contract. |
| No opaque executable model payload | `tests/dotnet/Honua.Ai.Tests/Source/McpTaxonomyAlignmentTests.cs`: `McpComposition_DoesNotExposeOpaqueOperationProposalPath`; schema-closed tools in `src/Honua.Ai/Features/Protocols/Mcp/Mcp/Tools/PlatformOpsTools.cs`. |
| Self-approval denied; narrow approval grant | `tests/dotnet/Honua.Server.Tests/Features/Admin/ProposalEndpointsTests.cs`: `ApproveProposal_BySameRequester_IsForbiddenForSeparationOfDuties`, `ApproveScopedKey_CanReadAndApproveButCannotMutateOtherAdminSurfaces`, `ReadOnlyScopedKey_ApproveNamesMissingGrant`. |
| Same actor cannot bypass tenant ownership | `tests/dotnet/Honua.Server.Tests/Features/Admin/ProposalTenantOwnershipTests.cs`: `ProposalResource_ProposerIdentityDoesNotBypassTenantOwnership`. |
| Finding-proposal actor, scope and target binding | `McpPlatformOpsReaderTests.ProposeFinding_UnauthorizedDeploymentRequest_CreatesNoProposal` calls the finding proposal itself for denied admin policy, a read-only OAuth scope under the real scope authorizer, and a mismatched deployment target. Every case asserts denial, zero proposal/direct-route calls and zero canonical acceptance. Target equality is not tenant/resource ownership; the separate ownership fixtures above and eventual installed-client replay must also pass. |

Route and operation IDs were checked against
`docs/developer/api-specs/admin-api.json`; MCP names against
`src/Honua.Ai/Features/Protocols/Mcp/Mcp/Tools/PlatformOpsTools.cs` and the
workflow view catalog. CLI operation IDs agree with OpenAPI; installed-package
syntax and generated CLI-reference checks still require the candidate client.

## Candidate receipt requirements

The [release decision record](https://github.com/honua-io/honua-release/blob/trunk/docs/2026.1-release-decision-record.md)
still reports **candidate digest: not yet cut** (September 6 observation).
Release only the exact-candidate transcript, installed-client replay, joined
deployment actuator/convergence and supported placement rollback demonstration
to candidate qualification. The source-level suppression and actor/scope/target
fixtures above are pre-cut evidence. They do not prove that a deployment telemetry
producer actually reports partial/unverified/backend-loss conditions or that a
real deployment converges. Missing producer or ownership evidence cannot be
released merely because the candidate is absent.

Retain exact release lock/hash, image digest/architecture, package hashes,
deployment target/backend, finding/source observation window, proposal ID,
canonical operation ID, sealed policy and authority boundary, separate
approver ID, one typed actuator receipt, observed serving revision, sampled
readiness over the backend's declared verification window, audit and
correlation IDs. Keep sanitized request/response transcripts with test results.
Retain denial outcomes and before/after durable proposal/actuator counts.
Neither a screenshot, free-form `applied`, nor an empty test selection passes.
