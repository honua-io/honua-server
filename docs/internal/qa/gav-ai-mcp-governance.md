# GA vector hunt: ai-mcp-governance

Tracker: honua-io/honua-release#272. Baseline: honua-server `f16b248dd7` (origin/trunk, 2026-09-04). Isolated worktree: `/home/mike/honua-io/wt-hunt-gav-ai-mcp-governance`; pushed branch: `hunt/gav-ai-mcp-governance-20260904`.

## F1 — MCP feature queries omit the REST resource-access gate

- severity: P0
- location: `src/Honua.Ai/Features/Protocols/Mcp/Mcp/MapTools/QueryFeaturesTool.cs:68-80,111-135`; `MapToolLayerResolver.cs:62-96`; `ListLayersTool.cs:98-116`.
- description: The tool checks generic `Process.Read`, resolves a routable publication, and invokes `IFeatureReader` without checking the selected resource/service AccessPolicy or per-resource permission resolver. The REST query twin calls `AccessPolicyHelpers.RequireResourceAccessAsync` before reading. List-layers likewise enumerates routable publications without filtering by caller access. This violates `docs/guides/connect/ai-agents-mcp.md:3` (same authorization as every other protocol).
- repro: In the checkpoint branch run `HONUA_MSBUILD_NODE_CAP=4 dotnet test tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj --filter FullyQualifiedName~Gav_QueryFeatures_DeniedLayerPolicy`. The fixture has a layer restricted to `restricted-data-reader`, a caller without that role, and a count reader returning 42. The real REST access helper must deny; MCP must also deny before reaching the reader. Regression probe committed at `5a4246fe81`; runtime verification did not reach test execution within the cold-build and 285-second incremental attempts. No red/green result claimed.
- evidence: QueryFeaturesTool calls `EnsureCallerAuthorizedAsync(principal, Process, Read)` and then `reader.CountAsync(layer.StorageLayerId, query, ...)` / `QueryAsync(...)`. REST `src/Honua.Protocols.GeoServices/FeatureServer/FeatureServerQueryHandler.cs:172` checks the selected resource/service. The generic GP authorizer creates an authorization request with resource type and operation only (`GeoprocessingJobAuthorizer.cs:215-222`).
- verification: Traced dispatcher common authentication/tenantless-bearer checks → tool → resolver → registered Postgres reader. `PostgresFeatureStore.ApplyPermanentFilterAsync:939-967` applies RLS/permanent filters and field masks, not resource AccessPolicy; these independent protections do not close this gap. Metadata snapshot providers return the graph, not a caller-policy-filtered graph. No cross-tenant data disclosure is claimed from this trace.
- clientImpact: A principal allowed to use generic MCP queries can obtain counts/records from a layer for which the REST query is forbidden; layer discovery also exposes restricted metadata.
- confidence: 0.98
- disposition: filed https://github.com/honua-io/honua-server/issues/4255; targeted recent open/closed dedup found no concrete match.

## F2 — MCP query and describe bypass connection-bound provider routing

- severity: P1
- location: `src/Honua.Ai/Features/Protocols/Mcp/Mcp/MapTools/QueryFeaturesTool.cs:111-135`; `DescribeLayerTool.cs:157`; `MapToolLayerResolver.cs:80-96`.
- description: A publication's storage-binding context is resolved but never passed to the provider router. MCP always takes the DI-default IFeatureReader. The canonical REST query executor routes storage-bound publications to their actual connection/provider. MCP's advertised query/describe workflow therefore reads the managed feature partition instead of the published source table.
- repro: Publish a nonempty connection-bound table as a FeatureServer layer, with no corresponding rows in the managed `features` partition. Compare the REST layer query/count with `honua_query_features` (`returnCountOnly:true`, then a normal page) and `honua_describe_layer`. Expected: source rows/count; actual MCP path: default-reader managed rows/count (typically zero). Regression: register distinct default and routed readers, dispatch these tools for a storage-bound publication, assert router selected and only routed reader called.
- evidence: `FeatureServerQueryExecutor.V2.cs:423-461` calls `IFeatureProviderQueryRouter.ResolveReaderAsync` for a publication with a storage binding. Both MCP tools directly resolve `IFeatureReader`. `src/Honua.Db/Postgres/Features/FeatureStore/ServiceCollectionExtensions.cs:103` registers that interface as `PostgresFeatureStoreRefactored`; its QueryAsync/CountAsync do not route to the publication's connection.
- verification: Full source call-path comparison, including DI and default-reader methods. No inferred field-mask/RLS bypass is included: the default reader has its own protections. This is a distinct MCP adapter omission from prior OData #4217 and tile/H3 #4107 findings.
- clientImpact: The documented terminal publish → inspect/query journey gives empty or unrelated managed results for a valid source-backed layer, misleading model reasoning and downstream analysis.
- confidence: 0.98
- disposition: filed https://github.com/honua-io/honua-server/issues/4251; targeted recent open/closed dedup found no concrete match.

## F3 — Applying an MCP style preset writes live metadata outside governance

- severity: P0
- location: `src/Honua.Ai/Features/Protocols/Mcp/Mcp/MapTools/ApplyStylePresetTool.cs:93-98,140-147`.
- description: The live-layer style tool checks generic `PublishedService.Publish`, then directly changes the style association and active metadata graph. It never requests approval, consults an operation policy, or uses the proposal/operation runtime. Generic publisher authorization is also not the REST style-authoring admin authorization. This violates the packet's model-mutation approval floor and the shared control-plane mutation boundary in ADR-0062.
- repro: Configure approval-required governance; call `honua_apply_style_preset` with an existing style and layer as a caller with `PublishedService.Publish`. The tool directly calls `IStyleCatalog.AssociateLayerAsync` and `IMetadataV2StyleGraphSync.SyncLayerStylesAsync` and returns `applied:true` without a proposal. Existing focused probe: `--filter FullyQualifiedName~ToolsCall_ApplyStylePreset_BindsPresetAndSyncsGraph`. Add a deny/approval-required evaluator or gateway spy and assert zero catalog/graph writes until separately approved.
- evidence: `ApplyStylePresetTool.cs:143-146` makes the two writes directly. `PostgresStyleCatalog.cs:332-350` executes an INSERT/UPSERT with no authorization/approval. `PostgresMetadataV2StyleGraphSync.cs:83-102` reconciles the active graph. REST authoring routes in `AdminLayerStyleEndpoints.cs:35-40` and `src/Honua.Protocols.OgcApi/Styles/OgcStylesEndpoints.cs:115-137` require admin authorization.
- verification: Traced dispatcher → tool → generic GP authorizer (authorization/scope only) → SQL catalog write → metadata graph activation. Neither downstream service implements proposal/approval checks. Existing test explicitly expects both direct writes; no product changes made.
- clientImpact: A model-triggered call changes a published layer's appearance immediately despite an approval-required policy; a publisher grant can reach live style writes that REST protects as administration.
- confidence: 0.98
- disposition: filed https://github.com/honua-io/honua-server/issues/4253; targeted recent open/closed dedup found no concrete match.

## F4 — Applying a new preset can leave the old preset primary while reporting success

- severity: P1
- location: `src/Honua.Ai/Features/Protocols/Mcp/Mcp/MapTools/ApplyStylePresetTool.cs:140-157`; `src/Honua.Db/Postgres/Features/Styling/PostgresStyleCatalog.cs:92-100,332-350`.
- description: Applying a preset inserts/upserts only the requested `(layer_id, style_id)` at ordinal 0. It never moves the existing primary away from ordinal 0. The schema permits multiple zero-ordinal rows; reads order ties by style_id. Applying a lexically later style therefore reports `applied:true` while the old style remains primary. The descriptor promises that the requested preset becomes the primary/default and that subsequent render resolves it.
- repro: Associate existing style `a-old` with layer 42 at ordinal 0, then invoke `honua_apply_style_preset` on that layer with existing style `z-new`. Inspect `IStyleCatalog.GetStylesForLayerAsync(42)` or the following `honua_render_map` style caption: `a-old` still wins, although apply returned `styleId:z-new, applied:true`. Regression should seed both real catalog styles, apply the second through MCP, and require the new style to be first and selected by render/graph resolution.
- evidence: `PostgresStyleCatalog.AssociateLayerAsync` uses `ON CONFLICT (layer_id, style_id) DO UPDATE SET ordinal = EXCLUDED.ordinal`; `GetStylesForLayerAsync` orders `r.ordinal, s.style_id`. Migration `src/Honua.Server/Migrations/042_CreateStyleCatalog.sql:44-57` has only the pair primary key and a nonunique ordinal index. `PostgresMetadataV2StyleGraphSync.cs:85-102` copies this order into StyleResourceIds; `RenderMapTool.cs:328-330` chooses `styles[0]`.
- verification: Full adapter → concrete SQL write → SQL ordered read → metadata graph and render selection trace. No DB execution claimed. Existing apply test substitutes the catalog and only verifies the new association call, so it cannot catch duplicate primary ordinals. Exact recent open/closed searches for `style ordinal` found no matching issue.
- clientImpact: The GA style/render workflow claims a style change succeeded while users and models continue seeing the previous style; ordering of preset identifiers determines behavior.
- confidence: 0.99
- disposition: filed https://github.com/honua-io/honua-server/issues/4254; targeted recent open/closed dedup found no concrete match.

## coverageNotes

- Prior hunt titles skimmed for all four specified labels (saved locally in `/tmp/gav-ai-prior-issues.json`). Recent MCP titles and targeted open/closed issue searches also read. #3887 is a broad test-denominator task, not the concrete F1 defect; #3474 owns the separately named proposal tools, not F3's live style tool.
- Published-operation catalog: publication filters compatibility-only descriptors, requires exactly one approval mapper where applicable, and filters missing executors. OperatorGate calls fail closed before the invoker. Broad-catalog functional enablement remains outside this GA hunt.
- #3269's six direct analysis verbs are 2026.2; no functional finding filed for their absence. BYOM docs explicitly label the provider proxy Preview.
- Provider #3885: source now uses one linked deadline for error-body reads and caps those bodies at 4096 bytes; terminal/disposal focused tests identified for verification.
- Session binding includes canonical actor, issuer, tenant, and bearer credential fingerprint; POST/GET/DELETE validate binding. Unknown-session fallback discards session state and uses fresh request authorization. No confirmed isolation finding.
- GP execution and cancellation reach the shared approval authorizer/dispatcher; an initial adapter-only suspicion was dropped after downstream tracing.
- Potential cached-read policy bypass was dropped as a shipped-path finding: production admin descriptors are RuntimeDynamic; the sole deterministic built-in operation is service.publish, a mutation excluded from this tool source.
- Tool result limits: query row limit is 1000; descriptor views and resource reads have separate budgets. `McpToolHelpers.SuccessResult` summarizes text above 1024 characters but retains the full structured payload. No global tool-result byte ceiling was found; no stress probe or undocumented maximum is asserted as a defect. A large-feature transport receipt remains a coverage gap.
- Model data handling: server planning/NL paths are deterministic fixtures/plan compilation after ADR-0076; the opt-in Preview Studio proxy forwards caller-supplied role-tagged messages and streams events without dispatching tools itself. No server-owned recursive model/tool loop found. Prompt, request, response/event, tool-argument and provider-token limits exist; aggregate client conversation/cost budgets and a live model reaction to feature attributes/descriptions/file names were not certified. No PII transmission/control defect is asserted without a documented missing control and executable automatic egress path.
- Focused test initially failed before compilation due to sandbox MSBuild IPC SocketException (permission denied); escalated cold build stopped near five minutes (no test execution), then one incremental retry bounded to 285 seconds. The incremental retry exited 124 at its 285-second timeout, still building dependencies; neither attempt executed tests. No further builds or hosted-CI fallback were run. Lane dotnet shim and HONUA_MSBUILD_NODE_CAP=4 retained, shared compilation unchanged.

## Disposition summary

| Filed | Duplicates | Dropped hypotheses |
| ---: | ---: | ---: |
| 4 (2 P0, 2 P1) | 0 | 2 |

All four findings are source-verified over their complete relevant call paths. No production code was changed; the checkpoint carries the report and a focused access-policy regression probe.

## Verification receipts

| Check | Result |
| --- | --- |
| Initial focused Honua.Ai.Tests regression | MSBuild MSB1025 / SocketException (13), IPC permission denied before compilation |
| Escalated cold build, same filter | Stopped near five minutes (exit 1), still compiling dependencies; zero tests executed |
| Escalated incremental retry, access regression + existing style and three provider-adapter test classes | `timeout --signal=INT --kill-after=10s 285s`; exit 124, zero tests executed |
| Flow dashboard self-test | `self-test: ok` |
| Flow dashboard generation | Passed with network escalation: generated ignored site/index.html (7 cycles, 7 repositories) |
| Finding record fields and whitespace | Four complete records; `git diff --check` passed |

Runtime verification is incomplete, not a passing release receipt. The committed regression is an expected-denial test intended to fail before F1 is fixed; it was not observed running. Other issues specify concrete regression scenarios for their fixes. Test-attempt logs are preserved on the checkpoint branch in `docs/internal/qa/gav-ai-mcp-test-attempts.txt`. Provider outage conclusions are source-only: this hunt does not replace #3885's runtime matrix.
