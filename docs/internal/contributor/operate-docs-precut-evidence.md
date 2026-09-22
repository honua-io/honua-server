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

## September 13 accepted-pin recheck

Resumed the matching PR/WIP continuation `docs/3302-protected-operate` at
`647dc3fc70f84c2974ae4a4f2ada3f13f7e7b588` and rebased it onto
`008cda56a1d584cbe7a67064d18111be77e6e2d5`. Its substantive changes already
landed through #4745; rebase conflicts retained the upstream documents and
generated inventory. The original #3385 branch was fetched again. The #4734
architecture/navigation changes and #4726 local-only SLO contract are preserved.

The accepted manifest fetched from the release repository has SHA-256
`8c71ca3c29d0a09676d579f918c1a2627556f257f7ac80454fe840ff7a6e3bec` and still
pins `7ba4226` / `sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd`.
The [fresh staging receipt](../../guides/operate/evidence/3302-candidate-staging-recheck.json)
was produced by the unchanged `scripts/qualification/metadata-release-installed.py`
with `--manifest /tmp/3302-current-platform-manifest.yaml --output /tmp/3302-installed-recheck`.
It independently checks six EPSG:4326 feature values/coordinates and the absent
new field, then fails `prior identity missing or captured after mutation`:
live revision 3 → 4, `owner_email` exposed during staging, no captured prior
revision. All lane containers and the network were removed. The later recovery
scenarios did not execute; the failed staging check was not bypassed.

A separate instance using that harness's setup produced the
[read observation](../../guides/operate/evidence/3302-candidate-read-observation.json).
Four REST reads and four MCP calls passed the recorded status/shape checks.
Independently specified fixture assertions establish empty findings/events,
disabled alerting with absent observation/success clocks, and partial aggregate
coverage on REST and MCP. No deployment-source outage or proposal attempt was
made. A subsequent [catalog replay](../../guides/operate/evidence/3302-candidate-catalog.json)
follows all five pages of the authenticated `full` view (56 descriptors), rejects
duplicates/cursor cycles, finds all five named scenario tools, and checks the
required string inputs `findingId` and `candidateId`. Descriptor hashes are retained.
This checks the scenario tool inventory, not full Admin MCP parity or actuation.
The image returns status schema `1.0`, not the corrected
`1.1` contract, and the admin version response omits `sourceRevision`; image
identity was checked through Docker's digest and OCI revision before boot.
Neither result establishes deployment protection or installed-client execution.

Release PR #342 remains draft/open and site PR #275 remains open at `012f8acb`
with its deploy check skipped. Their publication/accepted-pin obligations,
the joined deployment/recovery transcript, producer failure cases and deployment
resource-ownership negatives remain unmet. Candidate absence is not a release
reason; this accepted candidate exists and fails the recorded prerequisite.

## September 14 proposed re-pin replay

Rechecked on trunk `3259cb112c`. The release repository's `platform-manifest.yaml`
still pins `7ba4226` / `sha256:dd50cd81…`, and release PR #342 (for `9f2f16a`)
is still draft. The newest imaged nightly is `ff1a463e7d48865239bb7cb13208fc7b65c4adce`,
index `sha256:75ac7813b541af98fe471701238ff8f8ca8a2687115d0a27d4d114ad95c9f349`
(tag `nightly-aot-ff1a463`). It contains the staging fix #4663 and the status
contract #4726; `7ba4226` contains neither. The image was pulled by tag, and
the harness checked its RepoDigest and OCI revision label against a temporary
manifest carrying only that server entry.

- [Staging replay](../../guides/operate/evidence/3302-repin-ff1a463-staging.json):
  the unchanged `metadata-release-installed.py` (SHA-256 `64825397…`, the same
  bytes that failed on `7ba4226`) passes all five scenarios. At
  `ServicePublication` the live revision stays at 3 with its ETag, and the
  operation binds `priorRevision=3`. Kill/start preserves the durable operation
  without activating. Activation is atomic, and recovery restores the prior graph
  while keeping the independently seeded Harbor City population of 1000007. A
  concurrent service policy edit forces one rebase, and owned-only recovery keeps
  `allowAnonymous=false` and `allowedRoles=["recovery-reviewer"]`. The
  missing-resource, unregistered-ETL and cancelled-ETL preparations fail with
  their named blockers and leave the live graph unchanged.
- [Read replay](../../guides/operate/evidence/3302-repin-ff1a463-read-observation.json):
  produced by `scripts/qualification/operate-read-observation.py`, which retains
  the replay procedure and digest encoding in the receipt. It runs the recorded
  read procedure plus full catalog pagination in one session. Five REST reads
  and four MCP calls pass. The independently specified
  fixture expectations hold on REST and MCP separately: no findings or events,
  and `alert_dispatch` `notConfigured` with no clocks. Status returns
  `schemaVersion=1.1` with `slo.configured=false`, `slo.availability=null` and a
  `replica-local`, `isPlatformSli=false` tail. Five full-view pages hold 58
  descriptors; all five scenario tools are present, `honua_propose_operation` is
  absent, and `findingId`/`candidateId` are required. The version response now
  reports `2026.1.1` but still omits `sourceRevision`.

Both runs removed their containers and network. They are **not accepted-pin
evidence**: they show what a re-pin must reproduce, and must be replayed once
the manifest accepts an image. Neither exercises a deployment actuator,
deployment-source outage, installed CLI/DevOps client or placement. The
September 15 replay below reproduces them on the accepted pin.

Two remainders were re-examined against trunk:

- **Producer failure cases** were a code defect, not a documentation gap:
  `BuildStoreSource` returned `complete` for `workflow_operations` whenever the
  store was registered. The
  [#4840](https://github.com/honua-io/honua-server/issues/4840) fix derives the
  source from the reads the deployment rules make. A singleton ledger keeps the
  last successful collection across scoped evaluations. The real-Redis tests are
  `OpsFindingsWorkflowSourceRedisTests`:
  - A stop/restart takes the source complete → unavailable (both clocks held at
    the first collection over two evaluations) → complete.
  - An ACL-denied per-target index read publishes `partial` with the missing
    target in coverage.

  While the evidence is not actionable, both tests assert zero gateway and
  envelope calls from `ProposeAsync` and from the MCP finding proposal. The same
  finding id reaches the gateway before and after. The fix postdates `548b7a5`, so
  it counts as accepted-pin evidence only after a re-pin.
- **Deployment resource ownership** is enforced by rule, not by target owner.
  `DeployTargetDefinition` has no owner or tenant attribute, so the
  [#4842](https://github.com/honua-io/honua-server/issues/4842) fix treats
  deployment targets as platform resources. With `MultiTenancy:Enabled=true`,
  a tenant-bound principal needs a configured `MultiTenancy:MultiTenantAdminRoles`
  role (default `multi_tenant_admin`, `platform_admin`) to propose or advance
  Deploy and platform-release operations. A principal is tenant-bound when it has a
  `MultiTenancy:TenantClaimTypes` claim or an approved-operation tenant binding.
  `PlatformDeployAuthority` applies this in the MCP finding, deploy-plan,
  deploy-operation, rollback and convergence proposals, and in the REST deploy
  plan/create/submit/promote/rollback and platform-release converge handlers.
  Denials are MCP `permission_denied` with `studioAuthorizationCode`
  `platform_admin_required`, or REST 403 with `code` `platform_admin_required`.
  Single-tenant installations and admins without a tenant binding are unchanged.
  The fix postdates `548b7a5`, so it counts as accepted-pin evidence only after a
  re-pin.

## September 15 accepted-pin replay

Release PR [#349](https://github.com/honua-io/honua-release/pull/349) merged as
`52cc3f2c60ff357bcec03ce1fa3ccd2ff2b81ef1`. Its `platform-manifest.yaml`
(SHA-256 `02c076be536514622c32a411e492f18413f257e9d4ff3d04c353dea591b89709`)
pins honua-server `548b7a5263da5a3f2381eb43f232687cdf92b0bf`, image
`ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`.
That commit contains #4663, #4726 and every earlier Operate documentation change.
The unchanged manifest file was passed to each harness, with no temporary
server-only manifest. Each harness checked the RepoDigest and OCI revision label
before boot and removed its containers and network afterwards.

- [Staging](../../guides/operate/evidence/3302-accepted-548b7a5-staging.json):
  `metadata-release-installed.py`, unchanged (SHA-256 `64825397…`), passes all
  five scenarios. The fixture SHA-256 `83eb29ac…` is the one recorded for
  `7ba4226` and `ff1a463`. The two recovery scenarios end `RolledBack` with the
  prior graph and the seeded population edit of 1000007. The three rejected
  preparations fail with `metadata-release-resource-missing`,
  `metadata-release-etl-unproven-compensation` and `metadata-release-etl-failed`.
- [Read observation](../../guides/operate/evidence/3302-accepted-548b7a5-read-observation.json):
  `operate-read-observation.py --accepted-manifest` records
  `qualificationStatus=accepted-pin-observation`. Five REST reads, four MCP
  calls, the fixture assertions and the status `1.1` contract pass. The full
  view now holds 124 descriptors in 11 pages; the ff1a463 run found 58 in 5.
  Admin MCP publication ([#4876](https://github.com/honua-io/honua-server/pull/4876))
  is in `548b7a5` but not `ff1a463`. All five scenario
  tools are still present, `honua_propose_operation` is absent, and
  `findingId`/`candidateId` are still required. The harness's boot-only
  `receipt.json` lists no scenarios; `observation.json` is the receipt.
- [Approval](../../guides/operate/evidence/3302-accepted-548b7a5-approval.json):
  `scripts/certification/prove-admin-approve-candidate.py` passes the same five
  checks as the September 12 `7ba4226` receipt. This is server API approval in
  Development with a Pro dev grant, joining proposal, operation-instance and
  draft IDs.

This is accepted-pin evidence for observation, metadata service staging and
server API approval. It does not exercise a deployment actuator, a deployment
source outage, the installed CLI/DevOps client, Console or a placement.

## Acceptance disposition

| Acceptance / verification | Disposition |
|---|---|
| Cross-surface deployment/readiness scenario | Documented REST reads, MCP calls, proposal polling, separate Admin CLI approval, optional Console inspection, protected-update progress, observation-window verification and backend rollback truth. |
| Freshness, completeness, backend identity and coverage | Documented source envelopes and failure behavior. #3475's executed Windows outage receipt is linked below. The deployment adapter fixtures cover stale, partial, unavailable, unverified and not-configured envelopes; live deployment-source collection remains a distinct evidence obligation. |
| Generic model proposal-only boundary and negative authorization | Source references below; #3411, #3430, #3431 and #3474 are closed as checked September 5. Their old open-blocker claims were removed. |
| One joined deployment actuator receipt and convergence window | **Unmet.** The approval receipt below exercises a real Studio draft operation; it does not prove deployment convergence or recovery. |
| Exact Local Docker and ECS-small route/tool/CLI/rollback replay | **Partly met.** On the accepted `548b7a5` pin, the REST routes, MCP tools, status contract, installed service staging/recovery and server API approval pass in isolated Docker. The installed CLI/DevOps client, the ECS-small placement and deployment rollback have not been replayed on it. Missing final release-lock manufacture does not release this criterion. |
| GitBook/docs validation | Local link/anchor, example-surface, OKF bundle, generated capability concepts and `llms.txt` checks pass. Checker regressions (24 links, example surfaces, 17 OKF, 2 Windows package verification) and `scripts/ci/pre-pr-check.sh` pass. The pre-PR selector uses the documentation-only shell/governance path; no managed build is required. Hosted GitBook preview is separately reported by the PR checks. |
| honua-site #185 links without broadening claims | [Site PR #275](https://github.com/honua-io/honua-site/pull/275) contains both `guides/operate/scenario` and `guides/operate/metrics` links in `operations.html`, verified at `012f8acbbc8aeb427d7326e5f9cc0f6204d5dbcc`. It remains open at that head on September 15; publication is not claimed. |

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
| Real backend outage and recovery | [Executed Windows receipt](../../guides/operate/evidence/3475-windows-outage.json), source `ee4f744a66491e7aa72e3efd22bf845e11b5869a`, plus [native runner instructions](../../guides/operate/evidence-posture.md). It records complete → unavailable → complete for `honua_ops_findings.alert_dispatch`, independently seeded pending/dead-letter counts of 0/1, REST/MCP outage parity, no new proposals and unchanged dispatch rows. `candidateQualification=false`: it proves that source outage, not deployment-actuator convergence or all incomplete cases. The opt-in `EvidencePostureLiveTests.cs` remains the deployed harness contract. |
| No opaque executable model payload | `tests/dotnet/Honua.Ai.Tests/Source/McpTaxonomyAlignmentTests.cs`: `McpComposition_DoesNotExposeOpaqueOperationProposalPath`; schema-closed tools in `src/Honua.Ai/Features/Protocols/Mcp/Mcp/Tools/PlatformOpsTools.cs`. |
| Self-approval denied; narrow approval grant | `tests/dotnet/Honua.Server.Tests/Features/Admin/ProposalEndpointsTests.cs`: `ApproveProposal_BySameRequester_IsForbiddenForSeparationOfDuties`, `ApproveScopedKey_CanReadAndApproveButCannotMutateOtherAdminSurfaces`, `ReadOnlyScopedKey_ApproveNamesMissingGrant`. |
| Same actor cannot bypass tenant ownership | `tests/dotnet/Honua.Server.Tests/Features/Admin/ProposalTenantOwnershipTests.cs`: `ProposalResource_ProposerIdentityDoesNotBypassTenantOwnership`. |
| Finding-proposal actor, scope and target binding | `McpPlatformOpsReaderTests.ProposeFinding_UnauthorizedDeploymentRequest_CreatesNoProposal` calls the finding proposal itself for denied admin policy, a read-only OAuth scope under the real scope authorizer, and a mismatched deployment target. Every case asserts denial, zero proposal/direct-route calls and zero canonical acceptance. Target equality is not tenant/resource ownership; that is the next row. |
| Deploy proposal creation bound to platform authority ([#4842](https://github.com/honua-io/honua-server/issues/4842)) | `tests/dotnet/Honua.Server.Tests/Features/Admin/DeployControlPlatformAuthorityTests.cs`. `McpProposal_TenantBoundAdminWithMultiTenancy_IsDeniedWithoutProposalRouteOrAcceptance` invokes the real finding, deploy-operation, deploy-plan, rollback and convergence MCP tools. It uses a tenant-claim admin and an approved-operation tenant credential with multi-tenancy enabled. It asserts the `permission_denied`/`platform_admin_required` error and zero proposal, direct-route, canonical acceptance and findings-evaluation calls. `McpProposal_PlatformAdminUnboundAdminOrSingleTenant_SealsApprovalProposal` and `McpDeployPlan_PlatformAdminUnboundAdminOrSingleTenant_ReachesTargetLookup` cover the allowed platform admin, unbound admin and `MultiTenancy:Enabled=false` cases. `RestDeployMutation_TenantBoundAdminWithMultiTenancy_Returns403BeforeAnyDeployCall` and `RestDeployMutation_PlatformAdminUnboundAdminOrSingleTenant_ReachesDeployWorkflow` cover the six REST deploy/platform-release mutation handlers. The admin policy result and the downstream store/gateway are test seams; the authority rule is not. |

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

Since September 15 the accepted manifest pins server revision
`548b7a5263da5a3f2381eb43f232687cdf92b0bf` and image
`ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`.
Remaining receipts must use this identity, not a source build or a proposed
re-pin. The September 12 receipts below used the previous pin, `7ba4226`
(`sha256:dd50cd81…`).

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

### Previous-pin failure and unresolved dependencies

The [installed service-recovery receipt](https://github.com/honua-io/honua-server/blob/68d8d61561f3043abb328280c4f02d7166c46a7d/tests/baselines/metadata-release-installed/2026-09-12/7ba4226.receipt.json)
retained by [#4737](https://github.com/honua-io/honua-server/pull/4737) is **failed**
on the same accepted digest. Its independently checked fixture has six rows in
EPSG:4326, verified coordinates/values and no `owner_email` field before staging.
During preparation the live revision changes from 3 to 4, `owner_email` becomes
visible, and the operation lacks the prior identity needed for recovery. The
reported failure is `prior identity missing or captured after mutation`.
That breaks the protected-service promise that preparation leaves the live revision
intact and recovery binds a known prior revision. No successful recovery is inferred.

Release #349 superseded [release #342](https://github.com/honua-io/honua-release/pull/342)
and accepted `548b7a5`, which contains the fixes. The required scenarios passed there
([September 15 accepted-pin replay](#september-15-accepted-pin-replay)). [Telemetry qualification #4617](https://github.com/honua-io/honua-server/issues/4617)
and [the joined recovery certificate](https://github.com/honua-io/honua-release/issues/321)
remain distinct obligations. Neither an issue's closed state nor synthetic provider
coverage substitutes for installed verification.

On `548b7a5` the deployment finding producer still builds store completeness from
registration through `OpsFindingsService.BuildStoreSource`. The
[#4840](https://github.com/honua-io/honua-server/issues/4840) fix derives it from
real store reads and proves backend loss and partial reads against Redis; it
counts on the manifest only after a re-pin. The
[#4842](https://github.com/honua-io/honua-server/issues/4842) platform-authority
binding for deploy proposal creation also counts only after a re-pin.
Those, accepted-pin replay of the authorization negatives and the joined
deployment/placement transcript remain unmet, not waived.

Retain exact release lock/hash, image digest/architecture, package hashes,
deployment target/backend, finding/source observation window, proposal ID,
canonical operation ID, sealed policy and authority boundary, separate
approver ID, one typed actuator receipt, observed serving revision, sampled
readiness over the backend's declared verification window, audit and
correlation IDs. Keep sanitized request/response transcripts with test results.
Retain denial outcomes and before/after durable proposal/actuator counts.
Neither a screenshot, free-form `applied`, nor an empty test selection passes.
