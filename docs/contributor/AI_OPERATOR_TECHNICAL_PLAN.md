# AI Operator Technical Plan

**Status:** Draft  
**Date:** 2026-04-09  
**Audience:** Core contributors, standards authors, AI/runtime implementers

This document is the implementation plan for Honua's AI-first operator
architecture. It translates the high-level architecture and contract docs into:

- detailed canonical semantics
- repository boundaries
- service responsibilities
- promotion lifecycle rules
- Azure-first implementation strategy
- a chunkable work breakdown suitable for agentic development

It should be read alongside:

- [AI-First Operator Architecture](AI_OPERATOR_ARCHITECTURE.md)
- [AI Operator Contract](../developer/AI_OPERATOR_CONTRACT.md)
- [Deterministic Operator Workflow Results](../developer/DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md)
- [AI Operator Agent Handoff](AI_OPERATOR_AGENT_HANDOFF.md)
- [ADR-0026](adr/0026-ai-first-operator-contract.md)
- [ADR-0027](adr/0027-deterministic-intent-clarification-workflow.md)
- [ADR-0028](adr/0028-ai-data-editing-not-allowed.md)

## 1. Objectives

Honua's AI-first operator system should let a human or agent:

- discover what is possible
- express intent in natural language
- receive structured clarification when needed
- execute deterministic geospatial workflows
- obtain artifacts, maps, and optionally apps
- promote one-off work into reusable processes, publishing pipelines, or deployments

The target user is an operator working in one of four allowed workflow families:

- `Analyze`
- `Publish Data`
- `Build App`
- `Automate / Deploy`

`Edit Data` is explicitly not allowed or planned.

## 2. Architectural Thesis

The product is centered on one idea:

**A one-off run is promotable.**

A single natural-language interaction can become:

- a reproducible plan
- a reusable geoprocess
- a reusable publishing pipeline
- a mini app
- a deployment

That means the system must preserve intent, structure, outputs, provenance, and
promotion metadata instead of treating each run as disposable.

### 2.1 Integration Spine

The AI operator contract is the integration spine for Honua's non-destructive
workflow surface.

That means major capabilities should not live as isolated subsystems. They
should be discoverable, plannable, executable, packageable, and promotable
through the operator plane.

The target coverage is:

- catalog and capability discovery
- feature query and aggregation
- geoprocessing and multi-step analysis
- publishing and refresh pipelines
- workspaces and artifacts
- styling and map composition
- app generation
- promotion, publication, and deployment
- provenance, policy, and approvals

The first rich client/runtime focus is:

- MapLibre GL JS
- `honua-sdk-js`
- protocol-aware composition of disparate service sources in one map/app

SDK implication:

- `honua-sdk-js` should own first-party adapters for the core spatial protocols
- that first-party set should include GeoServices REST, OGC API, WFS, and WMS
- OData may be integrated through a canonical external JS library behind the
  same source-binding contract

A non-editing capability is not fully integrated until the operator contract can:

1. discover it
2. gather clarifications for it when needed
3. validate and execute it through deterministic services
4. package its outputs as artifacts plus map/app deliverables when applicable
5. promote the result into a reusable definition or deployment when applicable

## 3. System Layers

```text
Human Interface
  chat, forms, map workspace, builder workspace, approvals
    |
    v
AI Control Plane
  intent, grounding, clarification, planning, promotion, provenance
    |
    v
Deterministic Core Services
  catalog, feature, process, pipeline, workspace, render, builder, deployment
    |
    v
Runtime / Control Plane
  workflows, jobs, workers, schedulers, cloud adapters
    |
    v
Protocol Adapters
  MCP, gRPC, GPServer, OGC API, OData, Map/Tile protocols
```

### Human Interface

The human interface is responsible for:

- freeform requests
- starter templates and recipes
- clarification UX
- plan review
- map preview
- app preview
- approval actions

### AI Control Plane

The AI control plane is responsible for:

- partial intent capture
- dataset/process/template grounding
- clarification question synthesis
- plan synthesis
- promotion recommendations
- explanation and provenance assembly

### Deterministic Core

The deterministic core is responsible for:

- schema validation
- capability checks
- authorization and policy
- execution state transitions
- artifact persistence
- map packaging
- app packaging
- deployment state

## 4. Canonical Lifecycle

The operator lifecycle is unified across all workflow families.

### 4.1 Ad Hoc To Durable

```text
Request
  -> Intent
  -> Plan
  -> Execution
  -> Result Package
  -> Promotion
  -> Deployment
```

### 4.2 Promotion Targets

An executed workflow may be promoted into one or more durable artifacts:

- `ProcessDefinition`
- `PipelineDefinition`
- `MapPackage`
- `AppPackage`
- `Deployment`

### 4.3 Promotion Rules

- Analysis workflows may promote to `ProcessDefinition`, `MapPackage`,
  `AppPackage`, or `Deployment`.
- Publishing workflows may promote to `PipelineDefinition`,
  `PublishedService`, `MapPackage`, or `Deployment`.
- Builder workflows may promote to `AppPackage` and then `Deployment`.
- Automation workflows may promote directly to `Deployment`.

## 5. Canonical Domain Semantics

This section defines the objects that must exist independently of MCP, gRPC, or
 cloud/runtime choices.

### 5.1 Core References

#### 5.1.1 Versioning Strategy

All durable semantic objects must carry explicit version information.

Required version fields by object family:

- `specVersion`: version of the canonical object contract
- `definitionVersion`: revision of a durable definition such as
  `ProcessDefinition`, `PipelineDefinition`, or `AppPackage`
- `schemaVersion`: version of a parameter or output schema when distinct from the
  containing object

Rules:

- additive changes are allowed within a major `specVersion`
- breaking changes require a new major `specVersion`
- durable promoted artifacts must record the `specVersion` they were created
  against
- adapters may map older durable definitions forward, but the version boundary
  must remain explicit

#### SourceRef

Represents the origin of data or artifacts.

Supported `kind` values:

- `file`
- `database`
- `service`
- `artifact`
- `workspace`

`service` sources are first-class and include:

- GeoServices REST
- OGC API
- WFS
- OData
- another Honua instance

Conceptual shape:

```json
{
  "kind": "service",
  "provider": "geoservices_rest",
  "locator": {
    "url": "https://example.com/rest/services/parcels/FeatureServer/0"
  },
  "accessMode": "read",
  "acquisitionMode": "materialize"
}
```

`acquisitionMode` semantics:

- `proxy`: expose the source through Honua without creating a managed replica
- `materialize`: copy the source into managed storage and operate on the copy
- `sync`: maintain a managed downstream copy via one-way refresh from the source

`sync` in v1 means scheduled or event-triggered one-way refresh. It does not
mean bidirectional synchronization or source mutation.

#### DatasetRef

References a cataloged dataset or logical source.

#### LayerRef

References a concrete spatial/table layer.

#### StyleRef

References a reusable styling asset.

Supported uses:

- thematic renderer bundles
- label rules
- popup bindings
- legend configuration

#### MapTemplate

References a reusable cartographic composition template.

Typical examples:

- analysis default
- dashboard map
- review/approval map

#### ThemeSpec

Defines reusable visual tokens shared by maps and generated apps.

Typical contents:

- color ramps
- typography tokens
- panel/chrome tokens
- semantic status colors

#### SourceBinding

Defines how one `MapPackage` binds to a concrete protocol-backed source.

Required fields:

- `sourceId`
- `protocol`
- `locator`
- `querySemantics`
- `artifactBinding` when sourced from generated outputs

The same `MapPackage` must be able to include multiple heterogeneous
`SourceBinding` entries.

#### WorkspaceRef

References managed state:

- scratch workspace
- result workspace
- persistent project workspace
- temp collection

Workspace references must also carry lifecycle metadata:

- `workspaceClass`: `scratch | result | persistent`
- `retentionPolicyRef`
- `expiryAt`
- `quotaClass`
- `sharingScope`

The system must support:

- workspace quotas
- retention/expiry
- garbage collection
- explicit promotion from temporary to persistent scope
- controlled cross-workflow sharing by reference

#### ArtifactRef

Represents a produced artifact.

Supported `artifactClass` values:

- `scalar`
- `feature_layer`
- `table`
- `raster`
- `file`
- `report`
- `map`
- `app_bundle`
- `service_definition`

Artifacts should record:

- `artifactVersion`
- `producerRef`
- `workspaceRef`
- `retentionPolicyRef`
- `materializationState`

### 5.2 Intent Objects

#### AnalysisIntent

Represents ad hoc analytical intent.

Conceptual shape:

```json
{
  "intentId": "intent_analysis_123",
  "specVersion": "honua.operator.v1",
  "goal": "Find parcels within 500 meters of schools and rank by flood risk.",
  "workflowFamily": "analyze",
  "requestedOutputs": [
    "feature_layer",
    "map_package",
    "csv_export"
  ],
  "constraints": {
    "aoi": null,
    "timeWindow": null,
    "units": "meters"
  },
  "assumptionPolicy": "ask_when_material"
}
```

#### PublishingIntent

Represents a request to ingest, clean, enrich, validate, and publish data.

Key fields:

- `sourceRefs`
- `acquisitionMode`: `proxy | materialize | sync`
- `publishTargets`
- `refreshPolicy`
- `qualityPolicy`
- `requestedOutputs`

Conceptual shape:

```json
{
  "intentId": "intent_publish_123",
  "specVersion": "honua.operator.v1",
  "workflowFamily": "publish_data",
  "goal": "Load this county parcels service, normalize fields, and publish a refreshed service nightly.",
  "sourceRefs": [
    {
      "kind": "service",
      "provider": "geoservices_rest",
      "locator": {
        "url": "https://source.example.com/rest/services/parcels/FeatureServer/0"
      },
      "acquisitionMode": "sync"
    }
  ],
  "publishTargets": [
    "featureservice",
    "ogc_features"
  ],
  "refreshPolicy": {
    "mode": "scheduled",
    "cron": "0 2 * * *"
  },
  "qualityPolicy": {
    "requireSchemaValidation": true,
    "requireCrsNormalization": true
  },
  "requestedOutputs": [
    "published_service",
    "quality_report",
    "map_package"
  ]
}
```

#### BuilderIntent

Represents a request to create an app, dashboard, or operator UI around data,
processes, or prior results.

Conceptual shape:

```json
{
  "intentId": "intent_builder_123",
  "specVersion": "honua.operator.v1",
  "workflowFamily": "build_app",
  "goal": "Create a small review app for the latest flood-risk parcel analysis.",
  "sourceRefs": [
    {
      "kind": "artifact",
      "artifactRef": "artifact_candidate_parcels"
    }
  ],
  "requestedOutputs": [
    "app_package",
    "preview"
  ],
  "targetSdk": "honua-sdk-js",
  "templatePreference": "analysis_dashboard"
}
```

#### DeploymentIntent

Represents a request to publish, schedule, share, or operationalize a process,
pipeline, app, or result package.

Conceptual shape:

```json
{
  "intentId": "intent_deploy_123",
  "specVersion": "honua.operator.v1",
  "workflowFamily": "automate_deploy",
  "goal": "Publish this pipeline and run it nightly.",
  "targetRefs": [
    "pipeline_definition_abc"
  ],
  "schedule": {
    "mode": "scheduled",
    "cron": "0 2 * * *"
  },
  "publicationScope": "team"
}
```

### 5.3 Plan Objects

#### AnalysisPlan

A typed DAG for analytical execution.

Conceptual shape:

```json
{
  "planId": "plan_analysis_123",
  "specVersion": "honua.operator.v1",
  "workflowFamily": "analyze",
  "steps": [
    { "stepId": "load_parcels", "kind": "query_features" },
    { "stepId": "buffer_schools", "kind": "geoprocess" },
    { "stepId": "compose_map", "kind": "render_map" }
  ],
  "outputs": [
    "feature_layer",
    "map_package"
  ]
}
```

#### PublishingPlan

A typed DAG for publishing workflows.

Supported step categories include:

- `inspect_source`
- `infer_schema`
- `map_schema`
- `normalize_crs`
- `clean_records`
- `dedupe`
- `enrich`
- `quality_check`
- `publish_service`
- `compose_map`

Conceptual shape:

```json
{
  "planId": "plan_publish_123",
  "specVersion": "honua.operator.v1",
  "workflowFamily": "publish_data",
  "steps": [
    { "stepId": "inspect_source", "kind": "inspect_source" },
    { "stepId": "normalize_schema", "kind": "map_schema" },
    { "stepId": "quality_gate", "kind": "quality_check" },
    { "stepId": "publish_service", "kind": "publish_service" }
  ],
  "outputs": [
    "published_service",
    "quality_report",
    "map_package"
  ]
}
```

#### BuilderPlan

A typed graph for app composition.

Supported step categories include:

- `select_template`
- `bind_map_package`
- `bind_artifacts`
- `compose_widget`
- `compose_workflow`
- `generate_project`
- `preview_app`

Conceptual shape:

```json
{
  "planId": "plan_builder_123",
  "specVersion": "honua.operator.v1",
  "workflowFamily": "build_app",
  "steps": [
    { "stepId": "select_template", "kind": "select_template" },
    { "stepId": "bind_map_package", "kind": "bind_map_package" },
    { "stepId": "generate_project", "kind": "generate_project" }
  ],
  "outputs": [
    "app_package",
    "preview"
  ]
}
```

#### DeploymentPlan

A typed graph for scheduling, publishing, and operationalization.

Supported step categories include:

- `register_definition`
- `configure_schedule`
- `configure_approvals`
- `configure_runtime`
- `publish`
- `rollback`

Conceptual shape:

```json
{
  "planId": "plan_deploy_123",
  "specVersion": "honua.operator.v1",
  "workflowFamily": "automate_deploy",
  "steps": [
    { "stepId": "register_definition", "kind": "register_definition" },
    { "stepId": "configure_schedule", "kind": "configure_schedule" },
    { "stepId": "publish", "kind": "publish" }
  ],
  "outputs": [
    "deployment"
  ]
}
```

### 5.4 Durable Definitions

#### ProcessDefinition

Reusable analysis logic.

Fields:

- `processId`
- `parameterSchema`
- `defaultInputs`
- `outputContract`
- `planTemplate`
- `policyRequirements`

#### PipelineDefinition

Reusable publishing logic.

Fields:

- `pipelineId`
- `sourceSchema`
- `transformationPlan`
- `qualityGates`
- `publishContract`
- `refreshContract`

#### PublishedService

Represents a published dataset or service surface.

Fields:

- `serviceId`
- `protocolSurfaces`
- `styleRefs`
- `sourceLineage`
- `refreshStatus`

#### MapPackage

Required visualization package for workflows that produce spatial output.

Fields:

- `mapPackageId`
- `honuaMapSpec`
- `sourceBindings`
- `templateId`
- `styleRefs`
- `themeId`
- `initialView`
- `boundArtifacts`
- `legend`
- `labelBindings`
- `popupBindings`
- `previewArtifactId`

V1 styling scope should explicitly cover:

- thematic renderers and style presets
- labels and popups
- legends
- template-based map composition
- natural-language refinement of presentation
- composition of mixed protocol-backed sources in one runtime map

V1 does not need to fully replicate advanced desktop print-cartography features.

#### AppPackage

Generated app/UI package.

Fields:

- `appPackageId`
- `targetSdk`
- `templateId`
- `generatedFiles`
- `mapPackageRef`
- `boundArtifacts`
- `deploymentHints`
- `bundleArtifactRef`
- `assetManifest`
- `runtimeConfigSchema`

#### Deployment

Operationalized instance of a process, pipeline, app, or published service.

Fields:

- `deploymentId`
- `deploymentKind`
- `targetRef`
- `hostingMode`
- `routePrefix`
- `publicUrl`
- `revisionId`
- `schedule`
- `runtimeProfile`
- `deliveryArtifacts`
- `runtimeConfig`
- `visibility`
- `authPolicyRef`
- `approvalPolicy`
- `publicationState`

### 5.5 Result Objects

#### AnalysisResultPackage

Outputs from analysis workflows.

Required fields:

- `summary`
- `assumptions`
- `artifacts`
- `workspaceRefs`
- `mapPackageId` (string reference; full `MapPackage` type deferred to #730)
- `provenance`

#### PublishingResultPackage

Outputs from publishing workflows.

Required fields:

- `sourceLineage`
- `qualityReport`
- `publishedService` or `serviceDefinition`
- `mapPackage` when spatially relevant
- `provenance`

#### BuilderResultPackage

Outputs from builder workflows.

Required fields:

- `appPackage`
- `mapPackage` if applicable
- `previewArtifacts`
- `provenance`

#### DeploymentResultPackage

Outputs from deployment workflows.

Required fields:

- `deployment`
- `runtimeState`
- `approvalTrail`
- `provenance`

### 5.6 Error Model

Every stage and durable result family needs a canonical error shape.

#### ErrorDetail

Required fields:

- `errorCode`
- `category`
- `message`
- `stage`
- `stepId`
- `retryability`
- `suggestedAction`
- `details`

Example categories:

- `validation`
- `authorization`
- `policy`
- `execution`
- `artifact`
- `packaging`
- `deployment`

Conceptual shape:

```json
{
  "errorCode": "unknown_process",
  "category": "validation",
  "message": "Process 'slope_analysis' is not registered.",
  "stage": "validate_plan",
  "stepId": "run_process",
  "retryability": "fix_plan_and_retry",
  "suggestedAction": "Select a registered process or update the plan.",
  "details": {
    "processId": "slope_analysis"
  }
}
```

#### StageError

Required fields:

- `stage`
- `status`
- `error`
- `partialArtifacts`
- `partialWorkspaceRefs`

Rules:

- failed stages must emit structured `ErrorDetail`
- partial results must be explicit and typed
- retries must be policy-aware and stage-aware

### 5.7 Auth And Policy Model

Authorization and policy must be defined as transport-neutral semantics, not as
cloud-specific implementation details.

Core auth/policy objects:

- `PrincipalRef`
- `CapabilityGrant`
- `PolicyRequirement`
- `ApprovalRequirement`
- `AccessScope`

Minimum enforcement points:

- catalog discovery
- workspace access
- process/pipeline execution
- artifact promotion
- publication
- deployment

The cloud implementation may use Entra, API keys, or other providers, but the
operator contract must define who can:

- read
- execute
- promote
- publish
- deploy
- approve

## 6. Deterministic Semantics

The system is intentionally split between proposal and enforcement.

### 6.1 AI May Propose

- dataset candidates
- process candidates
- plan shapes
- map styles
- app structures
- promotion suggestions

### 6.2 Deterministic System Must Enforce

- schema validity
- capability availability
- auth and policy
- side-effect boundaries
- state transitions
- artifact persistence
- result package completeness
- provenance completeness

### 6.3 Clarification Policy

Clarification is mandatory when:

- required inputs are missing
- materially different interpretations remain plausible
- publication or deployment is requested
- policy or permissions require explicit acknowledgement
- confidence is below threshold

### 6.4 Non-Destructive Principle

All v1 workflows are non-destructive with respect to source data. AI may inspect
and recommend, but not edit source data.

### 6.5 Cost, Rate, And Backpressure Controls

Agent-driven workflows can create costly or cascading execution patterns if left
unbounded.

The deterministic platform must support:

- submission rate limits
- concurrency limits per principal/workspace/deployment
- dry-run estimation before expensive execution
- backpressure signals for overloaded services
- budget or quota policy hooks
- rejection or deferral when execution cost thresholds are exceeded

These controls apply independently of any cloud-specific rate-limiting system.

Backlog ownership:

- dry-run and estimation transport semantics belong to `honua-server#722` and
  `honua-io/geospatial-grpc#6`
- runtime rate, concurrency, cost, and backpressure enforcement belongs to
  `honua-server#739`

## 7. Service Architecture

### 7.1 CatalogService

Discovery for:

- datasets
- layers
- processes
- pipelines
- templates
- styles
- policies

This remains an architectural target for the typed execution plane. For the
current wave plan, discovery can be satisfied by `geospatial-mcp` resources and
existing server metadata surfaces until a dedicated gRPC catalog contract is
seeded.

### 7.2 FeatureService

Deterministic feature/table access:

- query
- stream
- aggregate

### 7.3 ProcessService

Geoprocessing execution:

- validate plan
- dry run / estimate
- execute sync
- submit async
- inspect job
- fetch results
- cancel

### 7.4 PipelineService

Publishing workflow execution:

- inspect source
- validate publishing plan
- execute ingest/transform/publish
- inspect refresh runs
- promote ad hoc publishing runs to `PipelineDefinition`

### 7.5 WorkspaceService

Artifact and workspace lifecycle:

- create workspace
- save artifacts
- promote temp outputs
- apply retention
- cleanup
- enforce quotas
- control cross-workflow sharing

### 7.6 RenderService

Map-oriented outputs:

- build `MapPackage`
- resolve templates, styles, and themes
- apply renderers, labels, and popup bindings
- create previews
- export static outputs
- generate legends

### 7.7 BuilderService

App generation:

- build `AppPackage`
- preview app
- export project

### 7.8 DeploymentService

Operationalization:

- register definitions
- publish
- bind hosted delivery semantics for app and map packages
- assign route and URL state
- inject runtime configuration for hosted package execution
- schedule
- observe deployment state
- pause/resume where applicable

## 8. Protocol Architecture

### 8.1 MCP

MCP is the interaction plane.

The existing `honua-sdk-js` MCP package remains the focused discovery/query MCP
surface for current open-core data access. The forward-looking operator MCP
surface for planning, execution, publishing, packaging, and deployment should be
implemented canonically in `honua-server` and aligned with the `geospatial-mcp`
standard. The SDK MCP package may proxy or federate that server-owned operator
surface later, but it is not the semantic source of truth for operator
workflows.

Use MCP for:

- discovery resources
- starter prompts and recipes
- clarification flows
- planning tools
- result-package resources
- map/app promotion and refinement

#### MCP V1 Capability Matrix

The `geospatial-mcp` standard should explicitly cover the following operator
workflows in v1 so implementations and evals do not infer scope differently.

Covered analyst flows:

- discover datasets, layers, schemas, processes, styles, themes, and templates
- inspect source metadata and existing result resources
- draft, clarify, review, and revise `AnalysisIntent`
- compile, validate, review, and promote `AnalysisPlan`
- trigger deterministic execution through typed backends
- inspect result packages, map packages, app packages, and provenance
- refine map outputs, legends, labels, popups, styles, and templates
- promote one-off analysis runs into reusable processes, app packages, or deployments

Covered publisher flows:

- inspect and profile files, databases, and existing service sources
- draft, clarify, review, and revise `PublishingIntent`
- compile, validate, review, and promote `PublishingPlan`
- inspect schema mapping, source lineage, and quality-report resources
- trigger ingest, clean, enrich, validate, publish, and refresh workflows
- inspect published services, refresh runs, deployment state, and provenance
- promote one-off publishing runs into reusable pipelines, packages, or deployments

Covered builder and deployment flows:

- inspect templates, themes, package resources, and deployment resources
- refine and preview `MapPackage` and `AppPackage` outputs
- publish result packages and deploy promoted artifacts
- inspect route, URL, revision, visibility, and publication-state resources

Explicit non-goals for MCP v1:

- direct AI data editing or geometry authoring
- desktop-parity print cartography or full 3D/scene semantics
- low-level execution internals better handled by `geospatial-grpc`
- server-specific storage layouts, build toolchains, or private planner logic

### 8.2 gRPC

gRPC is the typed execution plane.

Use gRPC for:

- feature queries and streaming
- process and pipeline execution
- workspace operations
- map/app packaging
- deployment operations

### 8.3 Compatibility Adapters

Downstream adapters project the canonical model:

- GeoServices `GPServer`
- OGC API Processes
- GeoServices / OGC / OData feature access
- GeoServices MapServer / OGC Maps / Tile services

## 9. Azure-First Reference Architecture

Honua should target Microsoft first without letting Azure define the standard.

### 9.1 Open Standard Surfaces

- `geospatial-mcp`
- `geospatial-grpc`

### 9.2 Honua ELv2 Core

- deterministic validation
- execution services
- packaging services
- provenance

### 9.3 Microsoft Layer

- Microsoft Agent Framework for orchestration
- Azure AI Foundry Agent Service as optional hosted runtime
- Azure AI Foundry guardrails for content/tool-response checks
- Entra ID + Azure RBAC
- OpenTelemetry + Application Insights
- Azure Container Apps / AKS
- Azure Database for PostgreSQL
- Redis when distributed coordination is needed

Microsoft Agent Framework is an orchestration host for the AI control plane. It
does not replace `geospatial-mcp` or `geospatial-grpc`.

Its role is to:

- host agent/workflow graphs
- manage checkpoints and human-in-the-loop turns
- coordinate tool usage against MCP
- coordinate deterministic execution against gRPC

The open standards remain the contract boundary. Microsoft provides the first
orchestration target, not the core semantics.

### 9.4 Private AI Layer

Private AI differentiates on:

- grounding quality
- clarification strategy
- plan synthesis quality
- promotion suggestion quality
- orchestration quality

### 9.5 Relationship To Existing `honua-server` Code

This plan is not a greenfield replacement of the current server.

Migration strategy:

- existing feature/query/catalog surfaces remain in place
- new canonical operator semantics are introduced in `Honua.Core`
- existing protocol slices are progressively re-pointed at the canonical core
- compatibility endpoints remain adapters, not parallel sources of truth

Expected implementation path:

1. introduce canonical nouns and contracts in `Honua.Core`
2. adapt new MCP/gRPC/operator flows to those contracts
3. progressively refactor existing geoprocessing, ETL, and packaging work to use
   the shared core
4. avoid duplicating business logic in protocol endpoint layers

### 9.6 SDK Dependency And Readiness

Builder workflows depend on stable target SDKs.

Phase 7 assumes:

- at least one supported target SDK is stable enough to scaffold against
- the package format and file layout are versioned
- generator outputs can be verified by downstream SDK tests

`honua-sdk-js` is the likely first target and should be treated as a gated
dependency for `AppPackage` implementation.

The first map runtime target should be MapLibre GL JS via `honua-sdk-js`.
`MapPackage` should therefore optimize first for direct execution in that stack.

## 10. Repository Boundaries

### 10.1 `geospatial-grpc` (Apache)

Contains:

- open proto contracts
- shared geospatial RPC semantics
- conformance tests
- open client libraries

### 10.2 `geospatial-mcp` (Apache)

Contains:

- MCP tool/resource/prompt conventions
- elicitation semantics
- result-package resource conventions
- conformance tests
- open client/server guidance

### 10.3 `honua-server` (ELv2)

Contains:

- reference implementation
- deterministic execution/runtime
- adapter implementations
- artifact and packaging engine

### 10.4 `honua-devops` (Private)

Contains:

- private orchestration intelligence
- enterprise/operator automation
- privileged DevOps flows

`honua-devops` should consume the open standards, not define them.

### 10.5 `honua-sdk-js` (Public)

Contains:

- MapLibre GL JS-first map runtime integration
- protocol-aware source adapters for web mapping
- `MapPackage` and `AppPackage` consumption
- JS builder/runtime support for operator-generated apps

## 11. Work Breakdown Principles

The implementation must be decomposable for agentic development.

### 11.1 Chunk Size

Each chunk should:

- change one repo only when possible
- touch one semantic boundary
- have a clear contract delta
- include tests or conformance fixtures
- be completable in one focused agent run

### 11.2 Good Chunk Types

- one canonical object family
- one gRPC service family
- one MCP primitive family
- one promotion step
- one runtime adapter
- one result-package subtype

### 11.3 Avoid

- mixing standards work with runtime implementation in one chunk
- mixing publishing, analysis, and app-generation semantics in one patch
- large cross-repo changes without contract freeze

### 11.4 Definition Of Done

Each chunk should end with:

- schema or interface landed
- tests/conformance added
- docs updated
- sample payloads or fixtures committed
- follow-on backlog identified

### 11.5 Evaluation Requirement

No semantic chunk is complete until it has one of:

- conformance fixtures
- deterministic contract tests
- agent eval scenarios

The operator system must be tested at three levels:

- object/schema validity
- service/protocol conformance
- agent workflow success for Claude, Codex, and the first orchestration target

## 12. Phased Delivery Plan

### Phase 0: Standards Foundation

Goal: establish public contract repos and governance.

Chunks:

1. Create `geospatial-mcp` repo and basic README/license
2. Define scope and non-goals for `geospatial-mcp`
3. Define versioning and compatibility policy for `geospatial-mcp`
4. Align `geospatial-grpc` and `geospatial-mcp` terminology
5. Add conformance/eval repo policy
6. Seed `honua-devops` backlog boundaries against the open standards

### Phase 1: Canonical Semantic Core

Goal: freeze the transport-neutral nouns and promotion lifecycle.

Chunks:

1. Define `SourceRef`, `ArtifactRef`, `WorkspaceRef`
2. Define object versioning strategy
3. Define `AnalysisIntent`, `PublishingIntent`, `BuilderIntent`, `DeploymentIntent`
4. Define `AnalysisPlan`, `PublishingPlan`, `BuilderPlan`, `DeploymentPlan`
5. Define `ProcessDefinition`, `PipelineDefinition`, `PublishedService`, `Deployment`
6. Define result-package families
7. Define canonical `ErrorDetail` / `StageError`
8. Define transport-neutral auth and policy model

### Phase 2: gRPC Execution Contracts

Goal: typed runtime surface.

Chunks:

1. `CatalogService` with auth/policy hooks
2. `FeatureService` alignment with existing `geospatial-grpc`
3. `ProcessService`
4. `PipelineService`
5. `WorkspaceService`
6. `RenderService`
7. `BuilderService`
8. `DeploymentService`
9. typed error transport and retry semantics

### Phase 3: MCP Operator Contracts

Goal: agent interaction surface.

Chunks:

1. discovery resources
2. starter prompts and recipes
3. clarification/elicitation conventions
4. planning tools
5. execution tools
6. result-package resources
7. map/app promotion tools
8. clarification/error/result review semantics

### Cross-Cutting Track: Evaluation Infrastructure

Goal: validate the operator contract against real agent behavior and deterministic
service expectations.

Chunks:

1. canonical eval task corpus for analyze/publish/build/deploy workflows
2. deterministic schema/conformance fixtures
3. Claude workflow evals
4. Codex workflow evals
5. first-orchestrator-host evals

### Phase 4: Honua Runtime Core

Goal: ELv2 reference implementation.

Chunks:

1. canonical process contract in `honua-server`
2. gRPC `ProcessService` adapter
3. MCP operator adapter
4. workspace/artifact lifecycle
5. workspace retention, quotas, and garbage collection
6. map packaging engine
7. publishing pipeline runtime
8. deployment service runtime

### Phase 5: Promotion Lifecycle

Goal: make one-off runs durable and promotable.

Chunks:

1. ad hoc run -> plan persistence
2. plan -> `ProcessDefinition`
3. plan -> `PipelineDefinition`
4. result -> `MapPackage`
5. result -> `AppPackage`
6. definition/package -> `Deployment`

### Phase 6: Publishing Workflows

Goal: treat publishers as first-class operators.

Chunks:

1. service/database/file source inspection
2. acquisition modes: proxy/materialize/sync
3. schema mapping and quality gates
4. publish target registration
5. refresh scheduling
6. publishing result package

### Phase 7: Builder Workflows

Goal: app generation and mini-app promotion.

Dependency: at least one target SDK must be stable enough to scaffold against.

Chunks:

1. app template catalog
2. `AppPackage` contract
3. map binding
4. widget/workflow binding
5. preview/export

### Phase 8: Azure-First Orchestration

Goal: first production cloud implementation target.

Role: provide the first orchestration host for the AI control plane while
preserving MCP and gRPC as the open contract surfaces.

Chunks:

1. Microsoft Agent Framework integration layer
2. Azure guardrail integration
3. tracing and eval hooks
4. Entra/RBAC alignment
5. Azure runtime deployment profile

### Phase 9: Compatibility Adapters

Goal: connect the AI-first core back to traditional GIS protocols.

For the current AI operator execution graph, the hard GP dependency is the
canonical process/runtime path plus the Honua gRPC process surface:
`honua-server#721`, `honua-server#722`, and `geospatial-grpc#6`. OGC API
Processes and GeoServices `GPServer` remain important compatibility work, but
they are not prerequisites for starting the operator-plane implementation.

Chunks:

1. OGC API Processes alignment
2. GeoServices `GPServer`
3. map/result package compatibility outputs

### Phase 10: Private Operator Intelligence

Goal: private differentiation on top of open standards.

Chunks:

1. private grounding/ranking improvements
2. promotion suggestion engine
3. private DevOps workflow synthesis in `honua-devops`
4. enterprise-only privileged operation agents

## 13. Initial Backlog Mapping

### `honua-server`

Existing seeded issues:

- `#681` durable worker/job orchestration substrate (external prerequisite for
  cloud-executor work, not owned by the current AI operator wave plan)
- `#721` canonical process contract and result package
- `#729` promotion lifecycle epic tracker
- `#730` publishing lifecycle
- `#731` packaging lifecycle
- `#732` deployment lifecycle
- `#733` transport-neutral auth and policy model
- `#734` end-to-end eval harness
- `#722` gRPC `ProcessService`
- `#723` GeoServices `GPServer`
- `#724` chaining/scheduling/DAGs
- `#725` workspace artifacts and cleanup
- `#726` approval gates
- `#727` cloud executor adapters
- `#728` MCP operator extensions
- `#735` seeded built-in process catalog from existing server capabilities
- `#738` MCP resource integration for published services and deployments
- `#739` runtime rate, concurrency, cost, and backpressure controls

Additional GP backlog outside the current operator-core dependency set:

- `#529` OGC API Processes protocol adapter
- `#736` surface, raster, and conversion process families
- `#737` generalization and data-management process families

### `geospatial-mcp`

Seeded issues:

1. `#1` epic
2. `#2` scope and non-goals for the MCP standard
3. `#3` canonical tool/resource/prompt taxonomy and elicitation conventions
4. `#4` result-package, map-package, app-package, and style/template resource conventions
5. `#5` conformance and eval framework
6. `#6` canonical dataset corpus and scenario packs

### `geospatial-grpc`

Seeded issues:

1. `#6` process and pipeline service contract
2. `#7` render, builder, and deployment contract
3. `#8` workspace and artifact service contract

### `honua-devops`

Seeded issues:

1. `#29` Azure-first orchestration integration
2. `#30` boundary and contract consumption rules
3. `#31` multi-model eval automation

### `honua-sdk-js`

Seeded issues:

- `#23` canonical shared client semantics across protocol adapters
- `#24` first-party GeoServices REST client
- `#25` first-party OGC API client
- `#27` first-party WFS client
- `#28` first-party WMS client
- `#26` OData adapter integration behind the shared client contract
- `#21` MapLibre GL JS-first runtime for `MapPackage`
- `#22` mixed-protocol composition across disparate service sources
- `#29` operator-native component architecture on the shared client/runtime

Required sequencing:

1. `#23` defines the shared JS client contract and capability model
2. `#24`, `#25`, `#27`, `#28`, and `#26` implement protocol clients/adapters
   with tests against that shared contract
3. `#21` establishes the MapLibre GL JS-first runtime target for `MapPackage`
   and generated apps
4. `#22` composes the protocol clients into mixed-source runtime maps
5. `#29` defines the operator-native component architecture over the shared
   client and runtime

Component architecture note:

- JS component/runtime work should be refactored around `#23` rather than
  continuing as ad hoc protocol-specific or compat-specific growth

## 14. Agentic Development Execution Model

For an agentic development system, the safest order is:

1. freeze nouns
2. freeze contracts
3. add conformance fixtures
4. implement one service at a time
5. add promotion flows
6. integrate cloud/runtime adapters

Suggested execution pattern:

- use one agent per repo or disjoint write set
- do standards work before implementation work
- keep every chunk vertically testable
- never mix public semantic changes with private AI heuristics in one change

## 15. Immediate Next Actions

The backlog is now seeded. Immediate next actions are:

1. Use [AI Operator Agent Handoff](AI_OPERATOR_AGENT_HANDOFF.md) as the ordered execution map.
2. Finalize the first concrete schemas:
   - `AnalysisIntent`
   - `PublishingIntent`
   - `MapPackage`
   - `SourceBinding`
   - `StyleRef`
   - `MapTemplate`
   - `AppPackage`
3. Start Wave 1 implementation work on canonical semantics, auth/policy, and shared client shape.
4. Keep `honua-server#731` and `honua-sdk-js#21` in lockstep when Wave 2 begins.
5. Treat `geospatial-mcp#6` -> `geospatial-mcp#5` -> `honua-server#734` -> `honua-devops#31` as the evaluation pipeline.
   - `ProcessService`
   - `PipelineService`
