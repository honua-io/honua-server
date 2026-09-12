# Operate documentation: pre-cut evidence disposition

Issue: [#3302](https://github.com/honua-io/honua-server/issues/3302).
Source reviewed: `3c61aa161c9d00dbf0c47e647e81191206f7b9d3` (September 12, 2026).
Resumed `docs/3302-evidence-refresh` at `d8d0ddc8d275c98802d736141cf712c42c9c543c`
(the PR and WIP refs matched), rebased onto trunk, and retained the content already
landed through [#4364](https://github.com/honua-io/honua-server/pull/4364) and
[#4467](https://github.com/honua-io/honua-server/pull/4467). The requested original
`codex/2026-1-server-release` branch from closed #3385 was fetched and compared;
its Operate docs are superseded by these follow-ups. Unrelated integrated runtime
work is not replayed into this documentation change. The documentation and SLO
changes in #4726, #4724 and #4703 are preserved.
This combines a source/receipt review with focused deployment-proposal fixtures.
The fixtures exercise the MCP adapter with explicit source envelopes and recording
gateway seams; they are not a live deployment or candidate transcript.
The [Windows test receipt](../../guides/operate/evidence/3302-proposal-boundary-windows.json)
records 49 passing proposal/cache tests with no skips, including nine new
finding-proposal cases, the tested commit and source/assembly hashes.

## Acceptance disposition

| Acceptance / verification | Disposition |
|---|---|
| Cross-surface deployment/readiness scenario | Documented REST reads, MCP calls, proposal polling, separate Admin CLI approval, optional Console inspection, protected-update progress, observation-window verification and backend rollback truth. |
| Freshness, completeness, backend identity and coverage | Documented source envelopes and failure behavior. #3475's executed Windows outage receipt is linked below. The deployment adapter fixtures cover stale, partial, unavailable, unverified and not-configured envelopes; live deployment-source collection remains a distinct evidence obligation. |
| Generic model proposal-only boundary and negative authorization | Source references below; #3411, #3430, #3431 and #3474 are closed as checked September 5. Their old open-blocker claims were removed. |
| One joined deployment actuator receipt and convergence window | **Unmet.** The approval receipt below exercises a real Studio draft operation; it does not prove deployment convergence or recovery. |
| Exact Local Docker and ECS-small route/tool/CLI/rollback replay | **Unmet.** The accepted manifest pin exists and must be tested. The installed service-staging receipt below fails on that pin; missing final release-lock manufacture does not release this criterion. |
| GitBook/docs validation | Local link/anchor, example-surface, OKF bundle, generated capability concepts and `llms.txt` checks pass. Checker regressions (24 links, example surfaces, 17 OKF, 2 Windows package verification) and `scripts/ci/pre-pr-check.sh` pass. The pre-PR selector uses the documentation-only shell/governance path; no managed build is required. Hosted GitBook preview is separately reported by the PR checks. |
| honua-site #185 links without broadening claims | [Site PR #275](https://github.com/honua-io/honua-site/pull/275) contains both `guides/operate/scenario` and `guides/operate/metrics` links in `operations.html`, verified at `012f8acbbc8aeb427d7326e5f9cc0f6204d5dbcc`. It remains open on September 12; publication is not claimed. |

The release promise is the bounded terminal Operate journey in the 2026.1
quality contract, extended by [protected rollout #319](https://github.com/honua-io/honua-release/issues/319):
checked changes, contained failures and verified recovery. This disposition preserves
the must-fix-before-cut ruling. The September 12 ruling requires the manifest-pinned
image even before final cut; the older candidate-absence release is superseded.
Missing deployment-path evidence and site publication remain unmet.

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
workflow view catalog. The manifest-pinned published `@honua/sdk-js@0.1.9-beta.0` was installed in
an isolated directory; its package-lock integrity matches the manifest's SHA-512.
`honua admin operations operate --json` includes both documented proposal operations.
Dry runs of `getOperationProposal --path id=proposal-fixture` and
`approveOperationProposal --path id=proposal-fixture --yes` produce the expected
GET `/proposals/{id}` and POST `/proposals/{id}/approve`, respectively, with the
literal path ID and `executed=false`. This verifies installed syntax and request
construction; it is not an authenticated CLI execution transcript.
[The syntax receipt](../../guides/operate/evidence/3302-installed-cli-syntax.json)
retains the commands and independently specified method/path/ID assertions.
A third dry run checks `honua admin release planDeployOperation`: POST
`/deploy/plan` with independently specified target/current/desired revisions,
with no execution. The runbook now requires this capability/parameter lookup
before approval and stops if the sealed proposal cannot be matched to the
observed prior revision and protection policy.

## Candidate receipt requirements

The accepted manifest read on September 12 pins server revision
`7ba422672e0c751843b17beb36e954a019cc19fb` and image
`ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd`.
These bytes are available locally and were used for the approval replay below.
Do not replace this identity with a source build or a proposed repin.

### Executed approval slice

[The September 12 receipt](../../guides/operate/evidence/3302-candidate-approval.json)
records five passing checks from `scripts/certification/prove-admin-approve-candidate.py`
on that exact image, using isolated real PostGIS and Redis containers. The harness
independently specifies a query draft (`population > 42`), generation 1 and its
workspace/family. It checks those values before comparing persisted state, verifies
scoped grants and denied writes, proves denied decisions change neither proposal nor
draft, then verifies that a separate approver deletes only the approved draft while
rejection preserves the other. An expired approval key cannot rotate or authenticate.
The receipt joins proposal, operation-instance and draft IDs. Containers were removed.

This is server-API approval evidence in Development with a Pro dev grant. It is not
MCP/CLI, Console, a deployment actuator, production licensing or placement recovery
qualification. The existing source fixtures still carry their narrower meanings.

### Accepted-pin failure and unresolved dependencies

The [installed service-recovery receipt](https://github.com/honua-io/honua-server/blob/68d8d61561f3043abb328280c4f02d7166c46a7d/tests/baselines/metadata-release-installed/2026-09-12/7ba4226.receipt.json)
retained by [#4737](https://github.com/honua-io/honua-server/pull/4737) is **failed**
on the same accepted digest. Its independently checked fixture has six rows in
EPSG:4326, verified coordinates/values and no `owner_email` field before staging.
During preparation the live revision changes from 3 to 4, `owner_email` becomes
visible, and the operation lacks the prior identity needed for recovery. The
reported failure is `prior identity missing or captured after mutation`.
That breaks the protected-service promise that preparation leaves the live revision
intact and recovery binds a known prior revision. No successful recovery is inferred.

The lane's proposed `9f2f16a` receipt passes five installed scenarios, but
[release #342](https://github.com/honua-io/honua-release/pull/342) must first establish
an accepted pin containing the fixes; then the required scenario must be replayed
against that accepted manifest. [Telemetry qualification #4617](https://github.com/honua-io/honua-server/issues/4617)
and [the joined recovery certificate](https://github.com/honua-io/honua-release/issues/321)
remain distinct obligations. Neither an issue's closed state nor synthetic provider
coverage substitutes for installed verification.

The deployment finding producer also still builds store completeness from registration
through `OpsFindingsService.BuildStoreSource`; the adapter's injected-envelope tests
do not prove actual partial/unverified/backend-loss collection. Finding-proposal
actor/scope/target equality assertions do not establish deployment resource ownership.
These and the joined deployment/placement transcript remain unmet, not waived.

Retain exact release lock/hash, image digest/architecture, package hashes,
deployment target/backend, finding/source observation window, proposal ID,
canonical operation ID, sealed policy and authority boundary, separate
approver ID, one typed actuator receipt, observed serving revision, sampled
readiness over the backend's declared verification window, audit and
correlation IDs. Keep sanitized request/response transcripts with test results.
Retain denial outcomes and before/after durable proposal/actuator counts.
Neither a screenshot, free-form `applied`, nor an empty test selection passes.
