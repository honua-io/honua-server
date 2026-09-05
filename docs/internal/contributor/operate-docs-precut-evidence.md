# Operate documentation: pre-cut evidence disposition

Issue: [#3302](https://github.com/honua-io/honua-server/issues/3302).
Source reviewed: `f75a2d08f` (September 5, 2026). This is a static source review,
not a candidate transcript or a claim of executed runtime tests.

## Acceptance disposition

| Acceptance / verification | Disposition |
|---|---|
| Cross-surface deployment/readiness scenario | Documented REST reads, MCP calls, proposal polling, separate Admin CLI approval, optional Console inspection, fix-forward verification and backend rollback truth. |
| Freshness, completeness, backend identity and coverage | Documented source envelopes and zero-proposal/zero-actuator failure behavior; existing fixture assertions mapped below. Live outage/recovery remains #3475. |
| Generic model proposal-only boundary and negative authorization | Source references below; #3411, #3430, #3431 and #3474 are closed as checked September 5. Their old open-blocker claims were removed. |
| One joined typed actuator receipt and convergence window | Released to candidate replay: requires the actual registered backend, verification policy and immutable candidate. No fabricated successful receipt or generic window. |
| Exact Local Docker and ECS-small route/tool/CLI/rollback replay | Released to candidate replay: release #231 is open; the current platform manifest explicitly says “THIS IS NOT THE RELEASE CUT.” Source-build observations cannot satisfy this criterion. |
| GitBook/docs validation | Run the repository documentation link/anchor and example-surface gates. Hosted GitBook preview is separately reported by the PR checks. |
| honua-site #185 links without broadening claims | Cross-repo criterion remains open; public target pages are `/guides/operate/scenario` and `/guides/operate/metrics`. No site deployment is claimed by this server PR. |

The release promise is the bounded terminal Operate journey in the 2026.1
quality contract. This disposition preserves the must-fix-before-cut ruling;
only checks that intrinsically require exact candidate bytes are released to
candidate replay. Missing live evidence and site work remain visible rather
than being counted as passed or silently removed.

## Authorization and freshness proof map

These are independently specified existing fixtures/assertions to rerun and
join to the eventual candidate receipt, not tests executed in this docs lane.
Paths are relative to the repository root.

| Requirement | Source evidence |
|---|---|
| Stale, partial, unavailable and unverified sources make zero gateway calls | `tests/dotnet/Honua.Server.Tests/Features/Infrastructure/Monitoring/OpsFindingsServiceTests.cs`: `Propose_IncompleteRequiredSourceEvidence_BlocksWithZeroGatewayCalls`; `Propose_NotConfiguredRequiredSource_IsDistinctFromUnavailableAndBlocks`. |
| Validity and coverage are source-derived | `tests/dotnet/Honua.Server.Tests/Features/Infrastructure/Monitoring/EvidencePostureTests.cs`: fixed-time stale/future/malformed/replica/component fixtures; complete aggregate uses its oldest observation. |
| Real backend outage and recovery | `tests/dotnet/Honua.Server.Tests/Features/Infrastructure/Monitoring/EvidencePostureLiveTests.cs`; opt-in deployed harness, not an automatically passing local test. |
| No opaque executable model payload | `tests/dotnet/Honua.Ai.Tests/Source/McpTaxonomyAlignmentTests.cs`: `McpComposition_DoesNotExposeOpaqueOperationProposalPath`; schema-closed tools in `src/Honua.Ai/Features/Protocols/Mcp/Mcp/Tools/PlatformOpsTools.cs`. |
| Self-approval denied; narrow approval grant | `tests/dotnet/Honua.Server.Tests/Features/Admin/ProposalEndpointsTests.cs`: `ApproveProposal_BySameRequester_IsForbiddenForSeparationOfDuties`, `ApproveScopedKey_CanReadAndApproveButCannotMutateOtherAdminSurfaces`, `ReadOnlyScopedKey_ApproveNamesMissingGrant`. |
| Same actor cannot bypass tenant ownership | `tests/dotnet/Honua.Server.Tests/Features/Admin/ProposalTenantOwnershipTests.cs`: `ProposalResource_ProposerIdentityDoesNotBypassTenantOwnership`. |
| Narrow OAuth scope denies rollback before persistence | `tests/dotnet/Honua.Server.Tests/Features/Infrastructure/Monitoring/McpPlatformOpsReaderTests.cs`: `ProposeRollback_ReadOnlyOAuthScope_IsDeniedBeforeProposalPersistence`. |
| Wrong Studio owner and unauthenticated draft callers | `tests/dotnet/Honua.Ai.Tests/Source/Studio/StudioMcpOwnershipAuthorizationTests.cs`; retain the specific denial cases used by the replay. |

Route and operation IDs were checked against
`docs/developer/api-specs/admin-api.json`; MCP names against
`src/Honua.Ai/Features/Protocols/Mcp/Mcp/Tools/PlatformOpsTools.cs` and the
workflow view catalog. CLI operation IDs agree with OpenAPI; installed-package
syntax and generated CLI-reference checks still require the candidate client.

## Candidate receipt requirements

Retain exact release lock/hash, image digest/architecture, package hashes,
deployment target/backend, finding/source observation window, proposal ID,
canonical operation ID, sealed policy and authority boundary, separate
approver ID, one typed actuator receipt, observed serving revision, sampled
readiness over the backend's declared verification window, audit and
correlation IDs. Keep sanitized request/response transcripts with test results.
Retain denial outcomes and before/after durable proposal/actuator counts.
Neither a screenshot, free-form `applied`, nor an empty test selection passes.
