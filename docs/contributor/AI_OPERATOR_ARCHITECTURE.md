# AI-First Operator Architecture

**Status:** Draft  
**Date:** 2026-04-09  
**Audience:** Core contributors, SDK authors, MCP/gRPC surface designers

This document captures the forward-looking architecture for Honua as an AI-first
geospatial operator platform.

The ordering is intentional:

1. AI-first contract
2. cloud-native runtime
3. compatibility adapters

The primary product is not "a geospatial server with AI attached." The primary
product is an analyst and builder contract that lets an agent discover data,
gather requirements, plan analysis, execute geoprocessing, produce maps, and
compose applications.

AI-driven source-data editing is not allowed in the primary operator contract.
See [ADR-0028: AI-Driven Data Editing Is Not Allowed](adr/0028-ai-data-editing-not-allowed.md).

## Problem Statement

Current geospatial systems expose protocol and product surfaces:

- REST endpoints
- desktop command models
- protocol-specific task contracts
- SDKs optimized for human developers rather than agents

That is not sufficient for the target outcome:

- a Claude or Codex agent can do what a GIS analyst would do on a desktop
- the system can gather missing requirements instead of guessing silently
- every meaningful result includes a map output
- the same result can be refined into an application using Honua SDKs

## Architectural Principles

### 1. The AI Contract Is Primary

The primary contract models analyst and builder work:

- intent
- grounding
- clarification
- planning
- execution
- map composition
- app composition
- provenance

Protocols are projections of that contract.

### 1A. The Operator Contract Is Honua's Integration Spine

The operator plane should tie together most of Honua's non-destructive
capabilities, not sit beside them as a separate assistant feature.

Major capabilities should either be directly exposed through the operator
contract or reachable through deterministic services behind it, including:

- catalog and capability discovery
- feature query and aggregation
- geoprocessing
- publishing
- workspaces and artifacts
- styling and map composition
- app generation
- promotion and deployment
- provenance, policy, and approvals

A capability is not fully integrated until an operator can discover it, clarify
requirements for it, execute it safely, and receive a packaged result that can
be promoted when appropriate.

### 2. Canonical Internal Semantics Come Before Protocol Design

Honua should not make GeoServices `GPServer`, OGC API Processes, MCP, or gRPC
the internal domain model.

The internal model should define canonical geospatial semantics and execution
objects. External protocols adapt those semantics.

### 3. Deterministic Skeleton, Probabilistic Planner

LLMs are used for:

- interpreting user goals
- ranking candidate datasets and processes
- suggesting maps, layouts, and app structures

LLMs are not trusted for:

- schema correctness
- capability checks
- authorization
- execution state transitions
- persistence semantics
- result package shape

### 4. Map And App Outputs Are First-Class

The default end state of analysis is not just tabular or feature output.

Every non-trivial workflow should be able to produce:

- analysis artifacts
- a map package based on `HonuaMapSpec`
- an optional app package targeting Honua SDKs

This extends ADR-0002 rather than replacing it. MapLibre remains the canonical
style basis; the operator contract packages maps as part of the analysis result.

### 4A. MapLibre GL JS And `honua-sdk-js` Come First

The first interactive map/runtime target should be MapLibre GL JS through
`honua-sdk-js`.

That means:

- `MapPackage` should be directly runnable in the JS SDK
- app generation should target `honua-sdk-js` before other SDKs
- the first native builder/operator experience should assume a MapLibre-based web
  runtime

Other SDKs and render targets can follow, but v1 map/app packaging should be
optimized for the JS stack first.

### 5. Styling Is Part Of The Operator Contract

Styling is not a post-processing afterthought.

The operator contract must support:

- thematic styling and renderer selection
- labels, legends, and popup bindings
- reusable map templates and themes
- follow-up map refinement in natural language
- composition of mixed protocol-backed sources in one map

V1 should focus on semantically rich operational styling, not full desktop print
cartography parity.

### 6. MCP And gRPC Are Primary External Surfaces

- MCP is the agent interaction plane
- gRPC is the typed execution plane

Compatibility protocols remain important, but they are downstream adapters:

- GeoServices REST
- OGC API Processes
- OGC API Features / Maps / Tiles
- OData

### 7. Runtime Adapters Are Below The Contract Layer

Execution backends can vary:

- Honua-managed local workers
- PostgreSQL-backed queueing
- Redis-backed distributed coordination
- Kubernetes Jobs
- AWS Batch
- Azure Batch

The AI contract must not change when the runtime adapter changes.

## Layered Architecture

```text
Natural Language / UI / Agent Runtime
    |
    v
MCP Surface
  - tools
  - resources
  - prompts
  - elicitation
    |
    v
Semantic Core
  - AnalysisIntent
  - ClarificationRequest
  - AnalysisPlan
  - BuilderPlan
  - Provenance
    |
    v
Deterministic Core Services
  - CatalogService
  - FeatureService
  - ProcessService
  - WorkspaceService
  - RenderService
  - BuilderService
    |
    v
Runtime / Control Plane
  - workflow operations
  - execution jobs
  - worker routing
  - provider adapters
    |
    v
Protocol Adapters
  - GeoServices GPServer
  - OGC API Processes
  - OGC API Features / Maps / Tiles
  - OData
```

## Canonical Concept Model

The AI-first contract should stabilize a small number of nouns:

- `CapabilityCatalog`
- `DatasetRef`
- `LayerRef`
- `ProcessDefinition`
- `AnalysisIntent`
- `ClarificationRequest`
- `ClarificationResponse`
- `AnalysisPlan`
- `BuilderPlan`
- `ExecutionJob`
- `WorkspaceRef`
- `ArtifactRef`
- `StyleRef`
- `MapTemplate`
- `ThemeSpec`
- `RendererSpec`
- `LabelSpec`
- `PopupSpec`
- `SourceBinding`
- `MapPackage`
- `AppPackage`
- `AnalysisResultPackage`
- `ProvenanceRecord`

These objects must be transport-neutral.

## Workflow Families

The primary AI operator workflow families are:

- `Analyze`
- `Publish Data`
- `Build App`
- `Automate / Deploy`

`Edit Data` is explicitly not allowed or planned. AI may support QA, validation, and fix
recommendations, but not autonomous source-data mutation.

## Service Families

### CatalogService

Responsible for discovery:

- datasets
- layers
- schemas
- processes
- styles
- templates
- policy and capability metadata

### FeatureService

Responsible for deterministic feature and tabular access:

- query
- stream
- aggregate

This remains the home for the current `geospatial-grpc` feature/query model.

### ProcessService

Responsible for geoprocessing and analysis execution:

- validate plan
- execute synchronous work
- submit asynchronous jobs
- inspect status
- retrieve outputs
- cancel or retry when supported

### WorkspaceService

Responsible for intermediate and saved state:

- scratch workspaces
- temp layers
- saved layers
- artifact registration
- lifecycle and expiry

### RenderService

Responsible for map composition:

- produce `MapPackage`
- resolve reusable styles and map templates
- apply themes, renderers, labels, and popup bindings
- render preview images
- generate legends
- export static deliverables

### BuilderService

Responsible for app composition:

- produce `AppPackage`
- bind artifacts into SDK-native app scaffolds
- generate dashboards, workflows, and map-driven apps
- preview and export runnable projects

## MCP Responsibilities

MCP should present the analyst and builder contract directly.

### Resources

Resources provide context chosen by the application:

- catalog snapshots
- dataset schemas
- process definitions
- saved maps
- saved styles and themes
- saved app templates
- prior analysis result packages
- workspace manifests

### Tools

Tools should operate at the semantic level, not the desktop-command level:

- `plan_analysis`
- `ground_datasets`
- `request_clarification`
- `validate_plan`
- `execute_plan`
- `create_map_package`
- `refine_map_package`
- `preview_map_package`
- `create_app_package`
- `preview_app_package`
- `publish_artifact`

### Prompts

Prompts should represent reusable workflows:

- site selection
- hazard assessment
- permit review
- service coverage analysis
- dashboard/app scaffolding

### Elicitation

Elicitation should be used when required inputs are missing, ambiguous, risky,
or policy-sensitive. The system should not rely on freeform guessing.

## gRPC Responsibilities

gRPC should expose typed, deterministic service contracts.

It should not depend on prompt engineering for correctness.

The recommended public gRPC families are:

- `CatalogService`
- `FeatureService`
- `ProcessService`
- `WorkspaceService`
- `RenderService`
- `BuilderService`

The gRPC surface can accept partially structured semantic objects, but execution
must still pass through validation and capability checks before any side effects.

## Result Packaging

Every meaningful analysis run should be able to produce an
`AnalysisResultPackage`.

At minimum, the package should contain:

- summary
- explicit assumptions
- provenance
- output artifacts
- workspace references
- a `MapPackage`

Where requested or appropriate, it should also contain:

- an `AppPackage`
- export bundles
- publication metadata

### MapPackage

`MapPackage` should build on the `HonuaMapSpec` direction in
[SDK Native Design Vision](SDK_NATIVE_DESIGN_VISION.md) and package:

- `honua_map_spec`
- protocol-aware source bindings for mixed protocol composition
- camera or extent
- layer bindings to output artifacts
- style and template references
- label and popup bindings
- theme metadata
- legends
- preview image
- export targets

### AppPackage

`AppPackage` should package:

- target SDK
- project scaffold metadata
- map bindings
- widget and workflow bindings
- environment and dependency metadata
- generated files or file references

## Runtime And Control Plane

ADR-0025 already separates:

- workflow operations
- execution jobs

The AI-first contract should reuse that split:

- planning, approval, and publication are workflow-oriented
- geoprocessing and heavy analysis runs are execution jobs

The semantic contract must not assume a specific runtime backend.

## Compatibility Strategy

Compatibility is a projection layer, not the source of truth.

The intended mapping is:

- canonical process model -> GeoServices `GPServer`
- canonical process model -> OGC API Processes
- canonical feature model -> GeoServices / OGC / OData query surfaces
- canonical map package -> GeoServices map, OGC Maps, MapLibre SDK workflows

This keeps interop strong without letting legacy or standards surfaces define
the internal ontology.

## Evaluation Strategy

The contract is not ready until Claude and Codex can both complete the same
analyst tasks using the same MCP contract and result package shape.

The evaluation suite should measure:

- clarification quality
- grounding quality
- plan validity
- execution success
- result correctness
- `MapPackage` usefulness
- `AppPackage` usefulness
- provenance completeness
- failure recovery behavior

## Non-Goals

This document does not define:

- concrete pricing or edition gating
- a full protobuf schema
- UI implementation details for a hosted Honua app builder
- low-level runtime adapter APIs

Those are downstream design tasks.

## Related Documents

- [ADR-0002: MapLibre as Canonical Style Format](adr/0002-maplibre-canonical-style.md)
- [ADR-0009: Shared Filter AST for Multi-Protocol Support](adr/0009-shared-filter-ast.md)
- [ADR-0025: Multi-Provider Operation Architecture](adr/0025-multi-provider-operation-architecture.md)
- [ADR-0026: AI-First Operator Contract as Primary Public Contract](adr/0026-ai-first-operator-contract.md)
- [ADR-0027: Deterministic Intent, Clarification, and Plan Validation Workflow](adr/0027-deterministic-intent-clarification-workflow.md)
- [AI Operator Contract](../developer/AI_OPERATOR_CONTRACT.md)
- [Deterministic Operator Workflow Results](../developer/DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md)
