# Terminal documentation: pre-cut evidence disposition

Issue: [#3364](https://github.com/honua-io/honua-server/issues/3364).
Source reviewed: `f75a2d08f`, September 5, 2026. No candidate installation,
cloud apply, model canary or publication success is claimed in this receipt.
The September 1 source-build claims inherited from the previous draft are
removed because they were not accompanied by retained candidate evidence.

## Acceptance disposition

| Criterion | Status and required evidence |
|---|---|
| Primary one-terminal local journey | Documentation stages supplied; packaged execution remains incomplete. The full install → style/render → GP → saved map/dashboard → governed publish path cannot currently be claimed. |
| Same journey after AWS ECS handoff | Released to exact-candidate replay with release #123/#129: requires pinned published artifacts, verified endpoint/auth/profile/proxy discovery and retained cloud receipt. No credentials or approved target were supplied to this docs lane. |
| Separate principals, denial of self-approval | CLI command maps to `approveOperationProposal` in Admin OpenAPI; existing `ProposalEndpointsTests.ApproveProposal_BySameRequester_IsForbiddenForSeparationOfDuties` asserts denial. Candidate transcript still required. |
| Tool/profile/catalog/package/CLI pins | Source names checked against MCP descriptors and Admin OpenAPI. Exact installed versions, counts and generated CLI reference remain released to the signed lock/candidate replay; release #231 is open and the manifest is explicitly not the release cut. |
| Every command clean-machine replayed, all identities joined | Not met. Do not replace execution with this static source review. The final replay must retain the full identity chain specified below. |
| Optional Studio/Console | Documented as independent readers of the same IDs; not terminal prerequisites. Witness-mode compatibility and the focused key receipt remain #3365. |
| Docs links/secret scan and generated CLI reference | Local documentation gates and focused example/secret checks are reported in the PR. Installed candidate CLI-reference verification is still required. No hosted GitBook build is claimed without its PR check. |

## Concrete implementation blockers

These are pre-cut implementation gaps, not criteria released merely because
the candidate is absent:

- [#3304](https://github.com/honua-io/honua-server/issues/3304) remains open.
  `src/Honua.Ai/Features/Protocols/Mcp/Mcp/Studio/StudioProposePublicationTool.cs`
  explicitly calls the draft update path only. It never calls
  `CreatePublicationRequestAsync` or `SaveDraftAsVersionAsync`.
  `tests/dotnet/Honua.Ai.Tests/Source/Studio/StudioMcpToolContractTests.cs`,
  `ProposePublication_NeverCallsVersionOrPointerMovingLifecycleMembers`,
  independently asserts that boundary. A draft-intent result cannot be passed
  off as the requested immutable-version publication proposal.
- [#3429](https://github.com/honua-io/honua-server/issues/3429) remains open for
  dashboard composition eligibility. The complete map/dashboard save,
  get-version and reopen sequence needs candidate-discovered lifecycle
  routing; dedicated tool names for those missing steps were not invented.
- [#3428](https://github.com/honua-io/honua-server/issues/3428) remains open for
  the terminal-model proof. Source view mechanics do not certify a model run.

#3411, #3430, #3431 and #3474 are closed as checked September 5. Their previous
open-blocker descriptions have been removed. SDK #1401 owns the actual
profile/catalog/style-render receipt; SDK #1397/#1398 own discovery routing
and lifecycle client integration. Their receipts must be attached rather
than inferred from tool discovery.

## Source checks and replay contract

- MCP names and style/render argument keys: `MapToolSchemas.cs`,
  `StudioDraftLifecycleTools.cs`, `StudioCompositionTools.cs` and
  `McpWorkflowViewCatalog.cs` under
  `src/Honua.Ai/Features/Protocols/Mcp/Mcp/`.
- CLI operation IDs: `docs/developer/api-specs/admin-api.json`.
  This verifies operation naming, not that an arbitrary installed CLI version
  supports every command group. Candidate package/reference checks remain open.
- GP execution and entitlement path:
  [Geoprocessing with AI](../../guides/query-analyze/geoprocessing-with-ai.md).
  The bounded walkthrough does not narrow the amended whole-catalog GP or
  four-cloud-native-format GA contracts. Preview boundaries are explicit.

The final receipt must join release lock/hash, all installed package hashes,
deployment/backend/endpoint, source connection, fixture revision, service and
layer, style and rendered artifact, GP job/result, map and dashboard draft
generations, saved version IDs/hashes, reopened bodies, proposal, operation,
policy, distinct proposer/approver, audit/correlation and verified final URL.
Use independently computed fixture expectations for attribute values,
geometry/ordinates/CRS and nodata/metadata where applicable. Retain negative
authorization results and zero unauthorized actuation, not snapshots of
current output or a count of discovered tools.

The release promise is the terminal setup-to-governed-publication journey.
This PR does not close #3364: only exact-candidate checks are released to
candidate replay; missing implementation remains a visible pre-cut blocker.
