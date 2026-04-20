# AI Operator Agent Handoff

**Status:** Draft  
**Date:** 2026-04-11  
**Audience:** Agentic implementation systems, maintainers, and reviewers

This document is the execution handoff for the AI operator program. It is the
single ordered backlog map that should be loaded before an agentic development
system starts implementation work.

This handoff is a sequencing and coordination map, not the canonical source of
ticket-local requirements. Per-ticket requirement truth lives in the contract
documents under the contract-first knowledge repo model.

## 1. Canonical Inputs

Load these documents first:

- the current ticket contract document from the contract store or knowledge repo
- the target repo root `AGENTS.md` plus `CLAUDE.md` / `CODEX.md` when present
- [AI Operator Technical Plan](AI_OPERATOR_TECHNICAL_PLAN.md)
- [AI-First Operator Architecture](AI_OPERATOR_ARCHITECTURE.md)
- [AI Operator Contract](../developer/AI_OPERATOR_CONTRACT.md)
- [Deterministic Operator Workflow Results](../developer/DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md)
- [ADR-0026](../../contributor/adr/0026-ai-first-operator-contract.md)
- [ADR-0027](../../contributor/adr/0027-deterministic-intent-clarification-workflow.md)
- [ADR-0028](../../contributor/adr/0028-ai-data-editing-not-allowed.md)

For `honua-io/honua-server`, the repo root [AGENTS.md](../../../AGENTS.md)
is mandatory reading before implementation. It defines architecture and quality
gates that are easy for agents to violate accidentally:

- warnings as errors
- Native AOT and trimming constraints
- dependency limits
- vertical-slice organization
- no controllers
- internal infrastructure types
- XML docs on public types
- integration-test requirements and attributes
- `dotnet format Honua.sln` before PR creation

If this handoff document and a ticket contract diverge, the ticket contract is
authoritative for that ticket's local scope, acceptance criteria, dependencies,
constraints, and mutation history. This handoff document remains authoritative
for cross-repo sequencing, load order, and coordination rules until equivalent
program-level contract artifacts replace it.

GitHub issue bodies and comments are projection and discussion surfaces only.
They may mirror the contract, but they are not canonical requirements input for
an agentic implementation system.

Comments should be treated as proposal surfaces. Requirement truth changes only
through direct contract edits or accepted proposal application.

During the contract-first migration, a ticket is execution-ready only when a
contract document exists for it. Issue-only backlog items remain planning
placeholders and should not be treated as implementation-ready by the agentic
system.

## 2. Repo Ownership

- `honua-io/honua-server`
  - ELv2 reference implementation
  - canonical runtime semantics
  - deterministic validation, execution, packaging, and policy enforcement
- `honua-io/geospatial-mcp`
  - Apache interaction-plane standard
  - tools, resources, prompts, and elicitation semantics
- `honua-io/geospatial-grpc`
  - Apache execution-plane standard
  - process, pipeline, render, builder, and deployment service contracts
- `honua-io/honua-sdk-js`
  - MapLibre GL JS-first runtime
  - shared JS client semantics
  - first-party browser adapters and operator-facing component/runtime model
- `honua-io/honua-devops`
  - Azure-first orchestration host
  - operator workflow hosting on top of the open MCP and gRPC standards

## 3. Execution Rules

1. Do not implement downstream repos before the relevant standard or canonical
   semantics ticket is closed, or explicitly marked resolved by the sequencing
   rules in this handoff.
2. Do not redefine `geospatial-mcp` semantics in `honua-server` or
   `honua-devops`.
3. Do not redefine `geospatial-grpc` service contracts in `honua-sdk-js` or
   `honua-server`.
4. Use fully qualified cross-repo references such as
   `honua-io/honua-server#731`.
5. Treat `honua-sdk-js#23` as the shared client prerequisite for all protocol
   adapter and runtime work in the JS SDK.
6. Treat `honua-devops#30` as the boundary prerequisite before any orchestration
   host implementation.
7. Preserve the hard non-goal from ADR-0028: AI does not edit source data.
8. Do not invent a private error model. `honua-io/honua-server#721` owns the
   canonical error envelope for failed or blocked operator work. At minimum,
   the envelope must support structured error identification, stage or step
   location, retry eligibility, and partial results or artifacts.
9. Do not invent a private auth model. `honua-io/honua-server#733` owns the
   transport-neutral authorization and policy model for execution, workspaces,
   packages, and deployments.

## 4. Honua-Server Codebase Baseline

The `honua-server` operator work is not greenfield. Agents must discover and
reuse these existing foundations before introducing new abstractions.

### 4.1 Existing Infrastructure To Reuse

- distributed jobs and import orchestration
  - `src/Honua.Core/Features/Import/Abstractions/IDistributedJobQueueService.cs`
  - `src/Honua.Core/Features/Import/Abstractions/IDistributedImportJobManager.cs`
  - `src/Honua.Server/Features/Import/RedisImportJobManager.cs`
- existing gRPC service and patterns
  - `src/Honua.Server/Features/Grpc/HonuaFeatureService.cs`
  - `src/Honua.Server/Features/Grpc/GrpcServiceCollectionExtensions.cs`
  - `src/Honua.Server/Features/Grpc/GrpcExceptionInterceptor.cs`
- deploy workflow engine and durable workflow state
  - `src/Honua.Server/Features/Infrastructure/ControlPlane/DeployWorkflowService.cs`
  - `src/Honua.Server/Features/Infrastructure/ControlPlane/DeployWorkflowReconciler.cs`
  - `src/Honua.Server/Features/Infrastructure/ControlPlane/RedisWorkflowOperationStore.cs`
- progress tracking and artifact-related primitives
  - `src/Honua.Core/Features/Infrastructure/Abstractions/IUniversalProgressStore.cs`
  - `src/Honua.Server/Features/Import/UniversalProgressStore.cs`
  - `src/Honua.Core/Features/Infrastructure/Abstractions/ICloudFileStorage.cs`
  - `src/Honua.Server/Features/FileStorage/CloudFileStorageBase.cs`
- existing event infrastructure
  - `src/Honua.Server/Features/Infrastructure/Events/IFeatureChangeEventStore.cs`
  - `src/Honua.Server/Features/Infrastructure/Events/FeatureChangeEventPublisher.cs`
- existing cloud/control-plane execution surfaces
  - `src/Honua.Core/Features/ControlPlane/Domain/OperationModels.cs`
  - `src/Honua.Server/Features/Infrastructure/ControlPlane/GitOpsDeployBackends.cs`
  - `src/Honua.Server/Features/Infrastructure/ControlPlane/AwsLambdaAliasClient.cs`
  - `src/Honua.Server/Features/Infrastructure/ControlPlane/AzureFunctionsSlotClient.cs`
  - `src/Honua.Server/Features/Infrastructure/ControlPlane/AzureContainerAppsRevisionClient.cs`

### 4.2 Start-By-Reading Map For `honua-server` Tickets

This map is the minimum baseline read set for `honua-server` tickets. If the
ticket contract includes additional ticket-specific read paths or canonical
references, agents should take the union of the contract and this section
rather than choosing one over the other.

- `honua-io/honua-server#721`
  - read `src/Honua.Core/Features/Import/Abstractions/IDistributedJobQueueService.cs`, `src/Honua.Core/Features/Import/Abstractions/IDistributedImportJobManager.cs`, `src/Honua.Core/Features/ControlPlane/Domain/OperationModels.cs`, and `src/Honua.Server/Features/Import/UniversalProgressStore.cs`
- `honua-io/honua-server#722`
  - read `src/Honua.Server/Features/Grpc/HonuaFeatureService.cs`, `src/Honua.Server/Features/Grpc/GrpcServiceCollectionExtensions.cs`, and `src/Honua.Server/Features/Grpc/GrpcExceptionInterceptor.cs`
- `honua-io/honua-server#723`
  - read `src/Honua.Server/Features/PrintingTools/PrintingToolsEndpoints.cs` and `src/Honua.Server/Features/Grpc/HonuaFeatureService.cs` for existing protocol-shaping patterns
- `honua-io/honua-server#724`
  - read `src/Honua.Server/Features/Infrastructure/ControlPlane/DeployWorkflowService.cs`, `src/Honua.Server/Features/Infrastructure/ControlPlane/DeployWorkflowReconciler.cs`, and `src/Honua.Server/Features/Infrastructure/ControlPlane/RedisWorkflowOperationStore.cs`
- `honua-io/honua-server#725`
  - read `src/Honua.Core/Features/Infrastructure/Abstractions/ICloudFileStorage.cs`, `src/Honua.Server/Features/FileStorage/CloudFileStorageBase.cs`, `src/Honua.Core/Features/Infrastructure/Abstractions/IUniversalProgressStore.cs`, and `src/Honua.Server/Features/Export/ExportJobService.cs`
- `honua-io/honua-server#726`
  - read `src/Honua.Core/Features/Security/AccessPolicyEvaluator.cs`, `src/Honua.Server/Features/Infrastructure/Authentication/AccessPolicyHelpers.cs`, `src/Honua.Server/Features/Infrastructure/Authentication/ServiceDataEditorAuthorization.cs`, `src/Honua.Server/Features/Infrastructure/Authentication/RbacOptions.cs`, `tests/Honua.Server.Tests/Features/Security/ServiceRbacAuthorizationTests.cs`, `tests/Honua.Server.Tests/Features/Admin/DeployControlEndpointsTests.cs`, and `docs/developer/DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md`
- `honua-io/honua-server#733`
  - read `src/Honua.Core/Features/Security/AccessPolicyEvaluator.cs`, `src/Honua.Server/Features/Infrastructure/Authentication/AccessPolicyHelpers.cs`, `src/Honua.Server/Features/Infrastructure/Authentication/ServiceDataEditorAuthorization.cs`, `src/Honua.Server/Features/Infrastructure/Authentication/RbacOptions.cs`, `tests/Honua.Server.Tests/Features/Security/ServiceRbacAuthorizationTests.cs`, `tests/Honua.Server.Tests/Features/Admin/AdminAuthorizationTests.cs`, `tests/Honua.Server.Tests/Features/Admin/DeployControlEndpointsTests.cs`, `tests/Honua.Server.Tests/Infrastructure/Authentication/ApiKeyAuthenticationTests.cs`, and `docs/developer/DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md`
- `honua-io/honua-server#727`
  - read `src/Honua.Core/Features/ControlPlane/Domain/OperationModels.cs`, `src/Honua.Server/Features/Infrastructure/ControlPlane/GitOpsDeployBackends.cs`, `src/Honua.Server/Features/Infrastructure/ControlPlane/AwsLambdaAliasClient.cs`, `src/Honua.Server/Features/Infrastructure/ControlPlane/AzureFunctionsSlotClient.cs`, and `src/Honua.Server/Features/Infrastructure/ControlPlane/AzureContainerAppsRevisionClient.cs`
- `honua-io/honua-server#728`
  - read `docs/developer/MCP_SERVER.md`, `docs/developer/AI_OPERATOR_CONTRACT.md`, the current `geospatial-mcp` contract chain or its projected issue references, and the `honua-sdk-js` MCP package at `mcp/README.md` and `mcp/src/index.ts`
- `honua-io/honua-server#730`
  - read `src/Honua.Core/Features/Import/Abstractions/IDistributedImportJobManager.cs`, `src/Honua.Server/Features/Import/ImportEndpoints.cs`, `src/Honua.Server/Features/Import/GeoServerImportJobManager.cs`, `src/Honua.Server/Features/Import/RedisImportJobManager.cs`, and `src/Honua.Server/Features/Import/MigrationScannerEndpoints.cs`
- `honua-io/honua-server#731`
  - read `docs/contributor/SDK_NATIVE_DESIGN_VISION.md`, `src/Honua.Server/Features/PrintingTools/PrintingToolsEndpoints.cs`, and `src/Honua.Server/Features/Grpc/HonuaFeatureService.cs`
- `honua-io/honua-server#732`
  - read `src/Honua.Server/Features/Infrastructure/ControlPlane/DeployWorkflowService.cs`, `src/Honua.Server/Features/Infrastructure/ControlPlane/DeployWorkflowReconciler.cs`, `src/Honua.Server/Features/Infrastructure/ControlPlane/RedisWorkflowOperationStore.cs`, and `docs/developer/DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md`
- `honua-io/honua-server#734`
  - read `docs/developer/DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md`, `docs/developer/AI_OPERATOR_CONTRACT.md`, `tests/Honua.Server.Tests/Import/ImportEndpointTests.cs`, `tests/Honua.Server.Tests/Features/PrintingTools/PrintingToolsEndpointTests.cs`, `tests/Honua.Server.Tests/Features/Export/ExportEndpointTests.cs`, and `tests/Honua.Server.Tests/Features/Admin/DeployControlEndpointsTests.cs`
- `honua-io/honua-server#735`
  - read `src/Honua.Server/Features/GeometryService/GeometryServiceEndpoints.cs`, `src/Honua.Server/Features/GeometryService/Services/GeometryServiceHandler.cs`, `src/Honua.Server/Features/SpatialAnalytics/SpatialAnalyticsEndpoints.cs`, `src/Honua.Server/Features/SpatialAnalytics/SpatialAnalyticsRequestHandlers.BufferAggregate.cs`, `src/Honua.Server/Features/SpatialAnalytics/SpatialAnalyticsRequestHandlers.SpatialJoin.cs`, `tests/Honua.Server.Tests/Features/GeometryService/GeometryServiceBufferTests.cs`, and `tests/Honua.Server.Tests/Features/SpatialAnalytics/SpatialAnalyticsRestTests.cs`
- `honua-io/honua-server#738`
  - read `docs/developer/MCP_SERVER.md`, `docs/developer/AI_OPERATOR_CONTRACT.md`, `docs/developer/DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md`, and the current contract documents for `honua-io/geospatial-mcp#4`, `honua-io/honua-server#728`, `honua-io/honua-server#730`, `honua-io/honua-server#731`, and `honua-io/honua-server#732` once they exist; until those contracts are created, treat the linked issues as temporary projection references rather than canonical requirement sources
- `honua-io/honua-server#739`
  - read `src/Honua.Core/Features/Import/Abstractions/IDistributedJobQueueService.cs`, `src/Honua.Server/Features/Import/UniversalProgressStore.cs`, `src/Honua.Server/Features/Infrastructure/ControlPlane/DeployWorkflowService.cs`, `src/Honua.Core/Features/Security/AccessPolicyEvaluator.cs`, and `docs/developer/DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md`

## 5. Global Sequence

Waves are planning buckets, not hard barriers. A later-wave ticket may start as
soon as all of its direct prerequisites are closed and the baseline code it
builds on has been read first.

Tracker epics such as `honua-io/honua-server#729` are not implementation-wave
items by themselves. They stay open until their child tickets close and should
not be treated as standalone delivery prerequisites.

Projected issue bodies may use local repo phase labels such as `H*`, `J*`,
`S*`, and `D*`. Treat those as repo-local planning markers only. The wave
ordering in this handoff is the authoritative cross-repo execution sequence.

`honua-io/honua-server#681` is an external runtime-foundation track, not a
ticket delivered by this AI operator wave plan. Only tickets that cite `#681`
as a direct prerequisite should wait on it, and they may not treat the
dependency as satisfied until `#681` closes or the ticket contract is updated
to remove that prerequisite explicitly.

### Wave 1: Standards and canonical semantics

Wave 1 unlocks later work by stabilizing vocabulary, core semantics, auth, and
shared client shape. For `honua-sdk-js`, existing repo-foundation tickets
`honua-io/honua-sdk-js#2` and `honua-io/honua-sdk-js#5` should already be done
or completed as part of the same stream before `#23` is considered complete.

- `honua-io/geospatial-mcp#1`
- `honua-io/geospatial-mcp#2`
- `honua-io/geospatial-grpc#6`
- `honua-io/honua-server#721`
- `honua-io/honua-server#733`
- `honua-io/honua-sdk-js#23`
- `honua-io/honua-devops#30`

### Wave 2: Planning, packaging, and baseline execution

Wave 2 may start when the specific prerequisites for each ticket are closed.
In practice, the main unlocks are `honua-io/honua-server#721`,
`honua-io/honua-server#733`, `honua-io/geospatial-mcp#2`, and
`honua-io/honua-sdk-js#23`.

Within Wave 2, `honua-io/honua-server#722` and `honua-io/honua-sdk-js#24`
through `#28` can start immediately after their Wave 1 prerequisites close.
`honua-io/honua-server#725` depends on `#722`. `honua-io/honua-server#735`
depends on `#721`, `#722`, and `#725` and should land before geoprocess
catalog-driven demos or evals are treated as meaningful. `honua-io/honua-server#730`
depends on `#722`, `#725`, and `#733`; `honua-io/honua-server#731` depends on
`#722` and `#725`; and `honua-io/honua-sdk-js#21` should begin design after
`#23` but consume the packaging contract that `honua-io/honua-server#731`
stabilizes for runtime deliverables. Do not treat `honua-io/honua-sdk-js#21`
as fully unblocked for implementation deliverables until `honua-io/honua-server#731`
has made the packaging contract concrete enough to consume.

- `honua-io/geospatial-mcp#3`
- `honua-io/geospatial-mcp#4`
- `honua-io/geospatial-grpc#7`
- `honua-io/geospatial-grpc#8`
- `honua-io/honua-server#722`
- `honua-io/honua-server#725`
- `honua-io/honua-server#735`
- `honua-io/honua-server#730`
- `honua-io/honua-server#731`
- `honua-io/honua-sdk-js#21`
- `honua-io/honua-sdk-js#24`
- `honua-io/honua-sdk-js#25`
- `honua-io/honua-sdk-js#26`
- `honua-io/honua-sdk-js#27`
- `honua-io/honua-sdk-js#28`

### Wave 3: Runtime surfaces and promotion flows

Wave 3 may start when the baseline execution and packaging surfaces are stable.
The main unlocks are `honua-io/honua-server#722`, `honua-io/honua-server#725`,
`honua-io/honua-server#731`, and the adapter set in `honua-io/honua-sdk-js`.

Within Wave 3, `honua-io/honua-sdk-js#22` should land before
`honua-io/honua-sdk-js#29`, because the component architecture depends on both
the `MapPackage` runtime from `#21` and mixed-source composition from `#22`.
`honua-io/honua-devops#29` depends on stable MCP planning semantics and
package/deploy semantics. `honua-io/honua-devops#31` should not start until
`honua-io/honua-devops#29`, `honua-io/geospatial-mcp#5`,
`honua-io/geospatial-mcp#6`, and `honua-io/honua-server#734` are all closed.

- `honua-io/geospatial-mcp#6`
- `honua-io/geospatial-mcp#5`
- `honua-io/honua-server#723`
- `honua-io/honua-server#724`
- `honua-io/honua-server#728`
- `honua-io/honua-sdk-js#22`
- `honua-io/honua-sdk-js#29`
- `honua-io/honua-devops#29`

### Wave 4: Governance, deployment, and advanced runtime

Wave 4 is the hardening and operationalization layer. It depends on stable
publish/package/deploy semantics rather than only on standards work.

Within Wave 4, `honua-io/honua-server#726` may start after `#725` closes.
`honua-io/honua-server#727` may start only after the external runtime
foundation `honua-io/honua-server#681` closes, plus `#721` and `#722`.
`honua-io/honua-server#732` must wait for `#724`, `#725`, `#726`, `#730`, and
`#731`. `honua-io/honua-server#734` must wait for `#730`, `#731`, `#732`,
`#735`, `honua-io/geospatial-mcp#5`, and `honua-io/geospatial-mcp#6`.
`honua-io/honua-devops#31` must wait for `honua-io/honua-devops#29`,
`honua-io/geospatial-mcp#5`, `honua-io/geospatial-mcp#6`, and
`honua-io/honua-server#734`.

- `honua-io/honua-server#726`
- `honua-io/honua-server#727`
- `honua-io/honua-server#739`
- `honua-io/honua-server#732`
- `honua-io/honua-server#734`
- `honua-io/honua-server#738`
- `honua-io/honua-devops#31`

## 6. Repo-by-Repo Work Breakdown

### 6.1 `honua-io/geospatial-mcp`

- `#1`
  - epic for the MCP standard track
- `#2`
  - define taxonomy, responsibilities, and non-goals
  - define the authoritative v1 analyst/publisher capability matrix for the MCP standard
  - blocks all meaningful downstream MCP implementation work
- `#3`
  - define clarification, elicitation, and planning semantics
  - make analyst, publisher, builder, and deploy planning behavior explicit
  - feeds `honua-io/honua-server#728` and `honua-io/honua-devops#29`
- `#4`
  - define result, map, app, style, theme, template, and promotion resources
  - include published-service, deployment, and hosted-surface inspection semantics
  - feeds `honua-io/honua-server#731`, `honua-io/honua-server#732`, `honua-io/honua-sdk-js#21`, and `honua-io/honua-sdk-js#29`
- `#6`
  - define the canonical dataset corpus and scenario packs for operator evals
  - should land before the conformance harness becomes a real release gate
- `#5`
  - define conformance fixtures and agent eval harness
  - depends on `#6`
  - feeds `honua-io/honua-devops#29`, `honua-io/honua-devops#31`, and downstream
    implementation validation

### 6.2 `honua-io/geospatial-grpc`

- `#6`
  - define process and pipeline service extensions
  - must stay aligned with `honua-io/honua-server#721` and `honua-io/honua-server#722`
- `#7`
  - define render, builder, and deployment contracts
  - must stay aligned with `honua-io/honua-server#731`, `honua-io/honua-server#732`, and `honua-io/honua-sdk-js#21`
- `#8`
  - define workspace and artifact lifecycle contracts
  - must stay aligned with `honua-io/honua-server#725` and the package/deploy consumers that bind artifacts and workspaces

### 6.3 `honua-io/honua-server`

For the current AI operator execution graph, the hard GP dependency remains the
canonical process/runtime path plus the Honua gRPC process surface:
`honua-io/honua-server#721`, `honua-io/geospatial-grpc#6`, and
`honua-io/honua-server#722`. The older framework and protocol tickets
`honua-io/honua-server#360` and `honua-io/honua-server#529` still matter: treat
`#360` as the comparative research and target-model umbrella for Esri GPServer,
GeoServer-style process exposure, and OGC API Processes, and treat `#529` as
the downstream OGC API Processes adapter over the canonical runtime rather than
as duplicate implementation scope. GeoServices `GPServer`, OGC API Processes,
and broader built-in GP family expansion remain important backlog, but they are
not prerequisites for starting the operator-plane implementation. For meaningful
end-to-end geoprocess demos and evals, `honua-io/honua-server#735` also needs
to land so the canonical process runtime does not ship with an empty catalog.

- tracker epics
  - `#729`
    - epic for promotion lifecycle work across publish, package, and deploy
    - tracking container only; closes when `#730`, `#731`, and `#732` are done
    - do not queue this as standalone implementation work
- `#721`
  - canonical process contract and result package
  - server-side semantic anchor for downstream GP/operator work
- `#733`
  - transport-neutral auth and policy model for execution, workspaces,
    packages, and deployments
  - should be treated as a foundation ticket, not a later afterthought
- `#722`
  - gRPC `ProcessService` and typed job/result transport
  - owns dry-run and estimation transport semantics for process and pipeline work
  - implementation counterpart to `honua-io/geospatial-grpc#6`
- `#723`
  - GeoServices `GPServer` adapter over canonical runtime
- `#724`
  - chaining, scheduling, and workflow DAG execution
- `#725`
  - workspace artifacts, retention, cleanup, and result lifecycle
- `#726`
  - approval gates and policy enforcement for destructive and publish workflows
- `#727`
  - shared cloud-executor adapter boundary over the external durable-worker
    substrate tracked in `honua-io/honua-server#681`
  - defines the canonical adapter contract that concrete backends must follow
  - keeps heavy GDAL-capable worker images optional instead of contaminating the
    baseline lightweight runtime
- `#758`
  - Kubernetes Jobs executor adapter over the shared cloud-executor boundary
- `#759`
  - AWS Batch executor adapter over the shared cloud-executor boundary
- `#760`
  - Azure Batch executor adapter over the shared cloud-executor boundary
- `#728`
  - canonical server-side operator MCP surface over the canonical GP runtime
  - the existing `honua-sdk-js` MCP package remains the focused discovery/query
    surface and may proxy or federate later, but it does not own operator
    semantics
- `#730`
  - publishing lifecycle: intents, pipelines, published services, and refresh
    deployments
  - `sync` means one-way scheduled or manually triggered pull from an upstream
    source into a managed copy; bidirectional sync and CDC are out of scope for
    this ticket
- `#731`
  - packaging lifecycle: `MapPackage`, `AppPackage`, styles, themes, templates,
    and mini-app generation
- `#732`
  - deployment lifecycle: promotion, scheduling, publication, and runtime state
- `#735`
  - seeded built-in process catalog from existing geometry and spatial analytics
    capabilities
  - bridge between existing server behavior and the canonical process runtime
- `#738`
  - MCP resource integration for published services, deployment resources, and
    hosted publication surfaces
- `#739`
  - runtime rate, concurrency, cost, and backpressure controls for operator
    workflows
- `#734`
  - server-side end-to-end eval harness consuming the open fixture model and
    validating analysis, publishing, packaging, and deployment workflows

### 6.4 `honua-io/honua-sdk-js`

- existing repo-foundation tickets
  - `#2` base client and auth foundation
  - `#5` auth hardening
  - `#4` compatibility and breaking-change gate
- `#23`
  - shared JS client semantics across GeoServices, OGC, WFS, WMS, and OData
  - prerequisite for all protocol adapters and runtime work
  - do not treat this as ready unless `#2` and `#5` are complete enough to
    support real authenticated clients
- `#24`
  - first-party GeoServices REST client with tests
- `#25`
  - first-party OGC API client with tests
- `#26`
  - OData adapter integration with tests
- `#27`
  - first-party WFS client with tests
- `#28`
  - first-party WMS client with tests
- `#21`
  - MapLibre GL JS-first runtime for `HonuaMapSpec` and operator map packages
  - consumes the packaging contract stabilized by `honua-io/honua-server#731`
- `#22`
  - mixed-protocol composition for maps and operator apps
- `#29`
  - operator-native component architecture built on the shared client and map
    runtime
  - should begin only after `#21` and `#22` are both complete enough to supply
    stable runtime and composition primitives

### 6.5 `honua-io/honua-devops`

- `#30`
  - define repo boundary and contract consumption rules first
- `#29`
  - Azure-first orchestration host built on the open standards, not beside them
- `#31`
  - automated multi-model eval runner for Claude, Codex, and local Llama-class
    portability checks
  - depends on `#29` plus the shared fixture corpus and server harness
  - should consume the fixture corpus and server harness instead of inventing a
    private test universe

## 7. Parallelization Guidance

- `honua-io/geospatial-mcp#2`, `honua-io/geospatial-grpc#6`,
  `honua-io/honua-server#721`, `honua-io/honua-server#733`, and
  `honua-io/honua-sdk-js#23` can proceed in parallel if the teams coordinate on
  canonical vocabulary and existing code reuse.
- `honua-io/honua-sdk-js#24` through `#28` can run in parallel after `#23`
  because their write scopes are protocol-specific.
- `honua-io/geospatial-grpc#8` should land in close coordination with
  `honua-io/honua-server#725`, because workspace and artifact references are
  shared dependencies of process, package, and deployment flows.
- `honua-io/honua-server#735` should land after `#722` and `#725` but before
  `honua-io/honua-server#734` is treated as meaningful geoprocess evaluation
  work, because the canonical process runtime needs a non-empty starter catalog.
- `honua-io/honua-server#730` and `honua-io/honua-server#731` can run in
  parallel after `#721`, `#722`, `#725`, and `#733` are stable.
- `honua-io/honua-sdk-js#21` may begin design work after `#23`, but its
  runtime deliverables must consume the packaging contract stabilized by
  `honua-io/honua-server#731`; keep the two tickets in close coordination so
  browser runtime semantics do not drift while `#731` lands.
- `honua-io/honua-sdk-js#29` should wait for both `honua-io/honua-sdk-js#21`
  and `honua-io/honua-sdk-js#22`, because operator components need stable map
  runtime and mixed-source composition primitives rather than only the shared
  client contract.
- `honua-io/geospatial-mcp#6` should finish before `honua-io/geospatial-mcp#5`,
  `honua-io/honua-server#734`, or `honua-io/honua-devops#31` are treated as
  release-gate work, because they need a shared dataset corpus and scenario set.
- `honua-io/honua-devops#29` should wait for stable MCP semantics from
  `honua-io/geospatial-mcp#3` and package/deploy semantics from
  `honua-io/honua-server#731` and `honua-io/honua-server#732`.
- `honua-io/honua-server#738` should wait for `honua-io/honua-server#728`,
  `honua-io/honua-server#730`, `honua-io/honua-server#731`, and
  `honua-io/honua-server#732`, because publish/deploy MCP resources depend on
  both the base MCP integration and stable publish/package/deploy semantics.
- `honua-io/honua-server#739` should wait for `honua-io/honua-server#722`,
  `honua-io/honua-server#724`, and `honua-io/honua-server#733`, because
  rate/cost/backpressure controls need stable execution semantics, workflow
  orchestration, and policy hooks.
- `honua-io/honua-server#727` may not close until the external durable-worker
  substrate in `honua-io/honua-server#681` is closed or the ticket contract is
  updated to remove that prerequisite explicitly.
- `honua-io/honua-devops#31` should wait for `honua-io/honua-devops#29`,
  `honua-io/geospatial-mcp#5`, `honua-io/geospatial-mcp#6`, and
  `honua-io/honua-server#734`, because it is an automation and regression layer
  over those assets rather than a source of new semantics.

## 8. What The Agentic System Should Do Per Ticket

- load all canonical documents in the order listed in Section 1
- read the current ticket contract and any linked prerequisite contracts before
  using projected GitHub issues for discussion context
- for `honua-server`, read the “start-by-reading” paths in Section 4.2 before
  designing new abstractions
- implement only the scope named in the current ticket
- treat projected issue comments as proposal input only; do not let discussion
  mutate requirements truth until the contract is updated
- when a ticket is partially unblocked for design but still gated for final
  deliverables by an upstream contract, limit work to design artifacts,
  investigation, and disposable spikes only; do not land production
  implementation or declare the ticket complete until the gated upstream
  contract is closed or explicitly resolved
- do not silently absorb adjacent backlog items
- if the contract changes materially during active work, stop and reload the
  updated contract instead of continuing on stale assumptions
- if a prerequisite contract is missing, wrong, or insufficient for the current
  ticket, open or update a blocking issue against the prerequisite repo and
  stop; do not work around a missing contract by inventing a local substitute
- if a ticket proves larger than a single focused agent run, open a draft PR
  with the completed portion, record what remains, and stop rather than
  silently expanding scope
- preserve existing infrastructure instead of rebuilding parallel queues,
  workflow stores, gRPC patterns, file-storage abstractions, or event pipelines
- for `honua-server`, keep to minimal APIs, vertical slices, AOT-safe patterns,
  endpoint/handler dependency limits, internal infrastructure types, and XML
  docs on public types
- for `honua-server`, run `dotnet format Honua.sln` before PR creation and add
  integration tests using the repo’s `[Protocol]`, `[Operation]`, and
  `[Endpoint]` conventions where applicable
- update cross-repo references if the semantics change materially
- leave a closing note that records what contract or implementation surface
  changed and which downstream tickets need revalidation

## 9. Definition Of Ready

A ticket is ready for the agentic system when:

- the repo owner is unambiguous
- a canonical ticket contract exists
- dependencies and downstream coordination are explicit
- all direct prerequisites are closed, or explicitly marked resolved by the
  sequencing rules in this handoff, before implementation work begins
- the architecture references are named
- the contract and handoff both identify the baseline code to study first when
  reuse is expected
- the acceptance criteria can be tested within the ticket scope
- the ticket is small enough to complete without redefining adjacent contracts
