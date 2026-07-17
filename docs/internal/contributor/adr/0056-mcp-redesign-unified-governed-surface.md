# ADR-0056: MCP redesign — unified, client-agnostic, governed surface (sequencing plan)

## Status

Proposed. This ADR is the **decomposition / implementation plan** for the MCP
redesign epic [#1948](https://github.com/honua-io/honua-server/issues/1948). It
does not itself land code; it states the target architecture and sequences the
four architectural, interdependent child workstreams —
[#1949](https://github.com/honua-io/honua-server/issues/1949),
[#1950](https://github.com/honua-io/honua-server/issues/1950),
[#1951](https://github.com/honua-io/honua-server/issues/1951),
[#1952](https://github.com/honua-io/honua-server/issues/1952) — into landable
increments with dependencies and acceptance gates, and reconciles them with the
increments already in flight
([#1953](https://github.com/honua-io/honua-server/issues/1953),
[#1954](https://github.com/honua-io/honua-server/issues/1954),
[#1955](https://github.com/honua-io/honua-server/issues/1955),
[#1957](https://github.com/honua-io/honua-server/issues/1957)) and with the
adjacent authoring-grounding work
[#1759](https://github.com/honua-io/honua-server/issues/1759).

## Context

The MCP redesign assessed 2026-06-21 (#1948) called the `/mcp` surface
"well-engineered at the protocol layer" but flagged two unmet AI-first claims —
fixtured planning and unwired authoring/publishing — plus a transport split
(HTTP operator surface vs SDK stdio discovery) that left no client seeing the
full `discover → ground → plan → preview/approve → execute → publish` arc.

Since the issues were filed the tree has moved. A grounding pass over the
current `/mcp` implementation (`src/Honua.Ai/Features/Protocols/Mcp/Mcp/`)
establishes the real starting line — and several pillars are already partly
built. The four architectural workstreams cannot each become a clean standalone
code PR without first agreeing what is already true and what order the rest
lands in; that sequencing decision is this ADR's purpose.

### What is already in tree (verified 2026-06)

| Capability | Reality in `src/Honua.Ai/.../Mcp/` | Owning issue |
|---|---|---|
| Tool/resource dispatch over `POST /mcp` | `McpEndpointExtensions` (JSON-RPC, pinned MCP `2025-03-26`), `McpDataAccessSurface` | base |
| Grounding-as-tools | `GroundCandidatesTool`, `ClarifyIntentTool`, `ValidatePlanTool`, `DryRunPlanTool` exist | #1949 (partial) |
| Tool annotations + output schemas | `McpToolAnnotationSets`, `McpToolOutputSchemas`, `McpToolDescriptor.annotations` | #1953 (in flight) |
| Live planner | `LivePlanAnalysisService` on the `IPlanAnalysisService` seam, config-gated by `WorkflowGeneration:DefaultProvider` (`ShouldUseLivePlanner`), `FixturePlanAnalysisService` fallback | #1955 (in flight) |
| Discovery/query tools on HTTP `/mcp` | `MapTools/ListLayersTool`, `QueryFeaturesTool`, `RenderMapTool` — registered but **gated** on `IMetadataV2GraphProvider` / `IFeatureReader` / `IRasterMapRenderer` being composed first | #1950 (partial) |
| Publish-by-chat tool | `PublishServiceTool` (`honua_publish_service`) routing through the canonical `IOperationInvoker` → `ServicePublishExecutor`, returning a structured "unavailable" handle when no operations toolset is composed | #1951 (partial) |
| Promotion resources | `PublishedServiceResource`, `DeploymentResource`, `MapPackageResource`, `AppPackageResource`, `PromotionSurfaceIndexResource` exist; wired via `AddMcpPromotionSurface`, gated in `FeatureRegistrationExtensions` on `IPublishedServiceStore` + `IDeploymentStore` | #1951 (partial) |
| Policy decision point | `IOperationPolicyDecisionPoint` + `AllowAllPolicyDecisionPoint` (Community default), consulted in `OperationDispatcher` | #1952 (seam landed) |
| Feature-catalog grounding resource | `FeatureCatalogResource` (`honua://catalog/features`), registered in the **default** composition (ADR-0054) | #1946 (landed) |
| geospatial-mcp alignment | `McpTaxonomyAlignmentTests`, `McpGeospatialMcpSchemaConformanceTests` | #1957 (in flight) |

### What is NOT yet true (the real gaps)

- **No second transport.** `/mcp` is HTTP-POST only. There is no stdio server in
  this repo and no streamable-HTTP/SSE; the SDK stdio server (`@honua/mcp-server`
  in `honua-sdk-js`) reimplements its own catalog rather than proxying one
  source of truth. No **parity test** asserts the catalogs match. *(#1950)*
- **No single source-of-truth catalog.** Each transport assembles its own tool
  list; #1950's "SDK proxies it" is unimplemented.
- **No `resolve_entity` / `list_capabilities` tool** backed by
  `honua://catalog/features`, and no proof that a *cold* client LLM (no Honua
  system prompt) can drive an end-to-end workflow. The grounding tools exist but
  their cold-client self-sufficiency is unverified. *(#1949)*
- **Authoring tools are services, not MCP tools.** `IMapGenerationService`,
  `IAppGenerationService`, `IReportGenerationService`,
  `IDashboardGenerationService` are registered in `FeatureRegistrationExtensions`
  but **not** exposed as MCP tools (`create_map_package`, `refine_map_package`,
  `apply_style_preset`, `compose_mixed_protocol_map`, `create_app_package`,
  `report.compose`). Only `honua_publish_service` is wired. *(#1951)*
- **Workflow-as-MCP-tool is not built.** A validated operations-toolset
  descriptor (RFC #17) is not yet projectable into a first-class, typed,
  cacheable MCP tool, and the policy decision point — while it exists — is not
  surfaced as a `RequiresApproval` MCP outcome wired to the approval lane
  (#197/#200). The seam is real; the product on top of it is not. *(#1952)*
- **Authoring grounding still refuses publish/style/map/STAC** in workflow
  generation (#1759): the live planner can emit analysis graphs but the node
  grounding lacks publish/style/map/dashboard/report/STAC node types, so the
  client-LLM path (#1949) and the authoring tools (#1951) ground on an
  incomplete capability surface.

## Decision

### Target architecture

One coherent, **client-agnostic, governed** MCP surface with a single
source-of-truth catalog served symmetrically over transports, where any
MCP client's LLM — or Honua's own server-side planner — composes a complete,
**correctness-guaranteed-by-Honua** GIS workflow over the canonical pipelines.

```
                    ┌──────────────── one tool/resource catalog ────────────────┐
client LLM (#1949)  │  discover  ground   plan    author    govern   publish    │  server planner (#1955)
Claude/GPT/Cursor   │  list_     resolve_ plan_   create_   policy   publish_   │  LivePlanAnalysisService
   │                │  layers    entity   analysis map_pkg   decision result    │     │
   ▼                │  query_    ground_  validate apply_    point    promotion  │     ▼
 POST /mcp ◄────────┤  features  candid.  dry_run  style…    (#1952)  resources  ├──► console/API
 stdio    ◄─ #1950 ─┤                                                            │
 SSE/sess ◄─ #1954 ─┤  every tool: annotations + output schema (#1953)          │
                    │  every entity: grounded on honua://catalog/features (#1946)│
                    └──────────── conforms to geospatial-mcp (#1957) ────────────┘
```

Five architectural commitments:

1. **One catalog, transport-symmetric.** A single in-process catalog
   (the registered `IMcpTool` / `IMcpResource` set) is the source of truth;
   every transport — HTTP-POST, streamable-HTTP/SSE, and stdio (server-native or
   SDK-proxied) — projects *that*, never a reimplementation. A parity test is
   the invariant. *(#1950, with #1954 adding the streaming transport.)*
2. **Honua, not the model, guarantees correctness.** Invalid compositions return
   structured, actionable `isError` results a cold client LLM can recover from;
   the deterministic grounding over `honua://catalog/features` is the inspectable
   substrate both the client LLM (#1949) and the server planner (#1955) ground
   on, so neither can claim a capability without evidence. *(#1949 + #1946.)*
3. **Authoring and publishing are first-class tools.** The existing generation
   services become MCP tools over the canonical pipelines so a chat can build
   *and* ship — not just plan analysis. *(#1951.)*
4. **Every operation is a guardrail enforcement point.** A validated
   operations-toolset descriptor (RFC #17) can be published as a deterministic,
   cacheable, typed MCP tool; `IOperationPolicyDecisionPoint` is consulted on
   every invocation (allow / require-approval / dry-run-first / deny, tier- and
   role-aware, `AllowAll` default), and `RequiresApproval` surfaces as a
   structured MCP outcome wired to the approval lane. "Deterministic mode" = the
   same toolset with AI off. *(#1952.)*
5. **Conformant to an open standard.** The catalog's vocabulary is generated
   from / validated against the public `geospatial-mcp` schemas, with Honua as
   the reference implementation. *(#1957, cross-cutting.)*

### The foundational increment (build first): Increment 1 — the catalog seam

**The one thing that unblocks the other three is a single source-of-truth tool/
resource catalog abstraction.** Today each transport and the SDK assemble their
own lists; #1950's "SDK proxies it", #1949's `list_capabilities`, #1953's
per-tool metadata, and #1957's conformance check all want to read *one* catalog.
Increment 1 extracts that seam **without** adding a transport or a tool:

- Introduce an `IMcpCatalog` (or equivalent) that enumerates the registered
  `IMcpTool` / `IMcpResource` set with their descriptors, annotations (#1953),
  and output schemas — the single object `tools/list` and `resources/list`
  already project, made addressable so other transports and the conformance
  runner consume it instead of rebuilding it.
- Keep the existing capability-gating (the `services.Any(d => …)` provider
  checks in `McpServiceCollectionExtensions`) as the catalog's honesty
  invariant: a tool absent from the composition is absent from the catalog on
  *every* transport.
- Land a **catalog snapshot test** that pins the advertised tool/resource set
  (names + annotations + schema refs) so subsequent increments extend it
  visibly and the eventual cross-transport parity test (#1950) has a baseline.

This is small, mechanical, and high-trust; it is the strangler seam every later
increment hangs off, and it touches no client-visible behavior on its own.

### Increment sequence

The four architectural workstreams decompose into the following landable PRs.
Each is independently mergeable behind the catalog seam, gated, and reversible.

| # | Increment | Maps to | Depends on | Acceptance gate |
|---|---|---|---|---|
| **1** | **Catalog seam** — `IMcpCatalog` source-of-truth + snapshot test | #1950 (foundation) | — | `tools/list` / `resources/list` project the catalog; snapshot test pins the set; no behavior change |
| **2a** | **Grounding self-sufficiency + `resolve_entity` / `list_capabilities`** backed by `honua://catalog/features` | #1949 | 1, #1946 | New tools land with LLM-grade descriptions + examples + output schemas; every existing tool description audited cold-client-usable; structured `isError` recovery proven |
| **2b** | **Cold-client conformance demo** — vanilla Claude Desktop + GPT/ChatGPT MCP client drive discover→query→analyze→map end-to-end with no Honua system prompt | #1949 | 2a, #1955 | A committed certification artifact (extends #1956 machinery) records both runs green |
| **3** | **Authoring tools** — `create_map_package`, `refine_map_package`, `apply_style_preset`, `compose_mixed_protocol_map`, `create_app_package`, `report.compose` as MCP tools over the existing generation services | #1951 | 1; **#1759** (grounding coverage) | Tools advertised + gated on their generation services; each carries annotations (write/idempotent) + output schema; demo "build a map of X styled by Y and publish it" runs end-to-end through `/mcp` |
| **4** | **Workflow-as-MCP-tool + approval-as-outcome** — project a validated operations-toolset descriptor (RFC #17) into a typed, cacheable MCP tool; surface `IOperationPolicyDecisionPoint` `RequiresApproval` as a structured MCP outcome wired to the approval lane (#197/#200); deterministic-mode audit path | #1952 | 1, 3 | Identical inputs → cached identical result; policy consulted on every invocation; approval round-trips through the existing lane; deterministic mode (AI off) inspectable |
| **5** | **Second transport + parity** — stdio (server-native or documented SDK-proxy) projecting the Increment-1 catalog; cross-transport parity test asserting the tool/resource set is identical | #1950 | 1; coordinates with #1954 (SSE/sessions) | Parity test green across transports; a Claude-Desktop-style stdio client sees the full toolset |

**Dependency rationale.** Increment 1 is the keystone — 2a, 3, 4, and 5 all read
the catalog. The client-agnostic path (#1949, increments 2a/2b) and the
server-planner path (#1955, in flight) are the *intelligence* and land before
breadth so the demo in 2b has a real planner behind it. Authoring (3) precedes
governed-workflow (4) because the workflow-as-tool guardrail wraps real
side-effecting tools, and because both authoring and the client-LLM path are
blocked on the **#1759** grounding-coverage fix (publish/style/map/STAC node
types) — that fix is a hard prerequisite for 2a/2b and 3 and should land in
`WorkflowNodeRegistry` first. Transport breadth (5) is deferred last: it
multiplies surface area and is only safe once the catalog is the single source
of truth, and it co-sequences with #1954's streaming transport rather than
duplicating it.

### Reconciliation with in-flight increments (do not duplicate)

These are being implemented as separate increment PRs **now**; this plan
references and sequences around them rather than re-scoping them:

- **#1953 (safety metadata)** — annotations + output schemas. Increments 2a/3/4
  *consume* these on every new tool; do not re-land the metadata model.
- **#1954 (sessions & streaming)** — `Mcp-Session-Id`, SSE, `notifications/progress`,
  newer protocol revision. Increment 5 **co-sequences** with it: the SSE
  transport #1954 adds is one of the transports Increment 5's parity test
  covers; the stdio transport is the other. They must share the Increment-1
  catalog.
- **#1955 (server-side planner)** — `LivePlanAnalysisService`. Already on the
  `IPlanAnalysisService` seam; Increment 2b's demo runs against it. No planner
  work in this plan beyond consuming it.
- **#1957 (geospatial-mcp standard)** — schemas + conformance runner. The
  Increment-1 catalog is what #1957's conformance runner validates; keep the
  catalog's vocabulary generated-from / checked-against the standard.

### Cross-reference: #1759 (authoring grounding gap)

#1759 is **adjacent and a prerequisite**, worked separately. Its fix — exposing
publish/style/map/dashboard/report/STAC node types to the workflow-generation
grounding (`WorkflowNodeRegistry` + the generation system prompt/few-shots) — is
the capability surface both the client-LLM path (#1949) and the authoring tools
(#1951) compose over. Increments 2a/2b and 3 **reference, do not duplicate**
#1759; land #1759's grounding coverage before 3's authoring-tool demo can pass,
since today the generator correctly *refuses* those node types.

## Consequences

- **Positive.** The four interdependent workstreams become a clean, ordered set
  of landable PRs with explicit gates, each reversible behind the catalog seam
  and behind the existing composition gating. The plan is grounded in the real
  tree (several pillars already exist), so PRs extend rather than rebuild. A
  single source-of-truth catalog removes the structural cause of the
  transport/SDK split. The strangler approach keeps `/mcp` working throughout.
- **Cost / risk.** Increment 1 adds an abstraction whose only near-term consumer
  is the snapshot test; its payoff is realized in 2a/4/5. Increment 5 and #1954
  must be coordinated to avoid two divergent streaming transports — this plan
  makes that an explicit co-sequencing constraint, not an accident. The #1759
  dependency means Increment 3's *demo* gate can't pass until a sibling repo-area
  fix lands; the tool *registration* in 3 can land independently of it.
- **Scope discipline.** This ADR sequences only the four architectural children
  (#1949/#1950/#1951/#1952) plus the catalog keystone. It does **not** re-scope
  the in-flight increments (#1953/#1954/#1955/#1957), implement #1759, change the
  pinned protocol revision (that is #1954), or add a planner (that is #1955).

## References

- Epic: #1948. Architectural children: #1949, #1950, #1951, #1952.
- In-flight increments: #1953, #1954, #1955, #1957. Provability: #1956.
- Adjacent prerequisite: #1759 (authoring grounding gap). Grounding substrate:
  #1946 / ADR-0054 (feature catalog). Operations toolset + policy seam: RFC #17.
  Approval lane: #197 / #200.
- Code substrate: `src/Honua.Ai/Features/Protocols/Mcp/Mcp/` (surface, tools,
  resources, `McpServiceCollectionExtensions`),
  `src/Honua.Ai/Features/AiBuilder/Planning/` (`IPlanAnalysisService`,
  `LivePlanAnalysisService`),
  `src/Honua.Core/Features/Operations/Abstractions/IOperationPolicyDecisionPoint.cs`,
  `src/Honua.Server/Features/Infrastructure/Hosting/FeatureRegistrationExtensions.cs`
  (composition root + `AddMcpPromotionSurface` gate).
- ADR-0026 (AI-first operator contract), ADR-0027 (deterministic intent/
  clarification/plan validation), ADR-0054 (feature catalog).
