# AI Operator Contract

**Status:** Draft  
**Date:** 2026-04-09  
**Scope:** Forward-looking primary contract for analyst and builder workflows

This document specifies the planned canonical contract for Honua's AI-first
operator architecture.

It complements:

- [MCP Server](MCP_SERVER.md), which documents the current open-core MCP data
  access surface
- [AI-First Operator Architecture](../contributor/AI_OPERATOR_ARCHITECTURE.md),
  which describes the high-level system design

## Goals

The contract must support:

- analyst workflows driven by agents
- publishing workflows driven by agents
- safe clarification when intent is incomplete
- deterministic execution over data and geoprocessing
- map-first result packaging
- app generation using Honua SDKs
- transport projection through MCP and gRPC

Direct AI-driven source-data editing is explicitly excluded from this contract
scope. See [ADR-0028](../contributor/adr/0028-ai-data-editing-not-allowed.md).

## Contract Design Rules

### 1. Canonical Objects Are Transport-Neutral

The internal objects must not use GeoServices, OGC, or desktop-specific naming
as their source of truth.

### 2. The Contract Must Support Partial Intent

Users do not begin with complete parameter sets. The contract must accept a
partially structured goal and support clarification.

### 3. The Contract Must Support Intermediate State

The system needs first-class references for:

- scratch workspaces
- temporary layers
- generated tables
- raster or file artifacts
- saved maps
- saved apps

### 4. The Contract Must Package Visualization

The default end state is not "data only." A valid result package should be able
to hand an agent or application a runnable map definition.

## Canonical Objects

> **Serialization note:** JSON examples below show canonical C# member names as
> identifiers. Actual wire-format serialization (casing, string vs numeric) is
> determined by each transport adapter (REST, gRPC, MCP). These examples
> illustrate the semantic contract, not a prescribed wire encoding.

### CapabilityCatalog

Describes the discoverable universe available to the operator:

- datasets and layers
- schemas
- processes
- styles and renderers
- templates
- policies and capability flags

Conceptual shape:

```json
{
  "catalogVersion": "2026-04-09",
  "datasets": [],
  "processes": [],
  "mapTemplates": [],
  "appTemplates": [],
  "policies": {
    "publishRequiresApproval": true,
    "destructiveActionsRequireApproval": true
  }
}
```

### AnalysisIntent

Represents the user's goal in partially structured form.

Conceptual shape:

```json
{
  "intentId": "intent_456",
  "goal": "Find parcels within 500 meters of schools and rank by flood risk.",
  "mode": "analysis",
  "requestedOutputs": [
    "FeatureLayer",
    "Map"
  ],
  "constraints": {
    "areaOfInterest": null,
    "timeWindowStart": null,
    "timeWindowEnd": null,
    "units": "meters"
  },
  "inputs": [],
  "assumptionPolicy": "AskWhenMaterial"
}
```

`requestedOutputs` uses `ArtifactKind` values: `Scalar`, `FeatureLayer`, `Table`,
`Raster`, `File`, `Report`, `Map`, `AppBundle`.

`assumptionPolicy` controls clarification behavior. Supported `AssumptionPolicy`
values:

- `AskAlways` — always ask the user before making assumptions
- `AskWhenMaterial` — ask only when the assumption materially affects results
  (default)
- `UseDefaults` — use sensible defaults without asking

### ClarificationRequest

Represents the minimal structured questions needed to proceed safely.

Conceptual shape:

```json
{
  "intentId": "intent_123",
  "reasonCodes": [
    "MissingRequiredInput",
    "AmbiguousDataset"
  ],
  "questions": [
    {
      "questionId": "q_dataset",
      "kind": "SingleSelect",
      "prompt": "Which school dataset should be used?",
      "options": [
        { "id": "schools_public", "label": "Public schools" },
        { "id": "schools_all", "label": "All schools" }
      ]
    }
  ]
}
```

Supported `ClarificationReasonCode` values: `MissingRequiredInput`,
`AmbiguousDataset`, `AmbiguousProcess`, `DestructiveAction`, `PublishAction`,
`PolicyBoundary`, `LowConfidence`.

Supported `ClarificationQuestionKind` values: `SingleSelect`, `MultiSelect`,
`FreeText`, `Confirmation`.

### ClarificationResponse

Captures the user's answers or accepted defaults.

Conceptual shape:

```json
{
  "intentId": "intent_123",
  "answers": {
    "q_dataset": ["schools_public"],
    "q_confirm_aoi": ["yes"]
  }
}
```

Each answer is a list of values to support multi-select questions. Single-select,
free-text, and confirmation answers use a single-element list.

### AnalysisPlan

Represents a typed, executable graph.

Conceptual shape:

```json
{
  "planId": "plan_123",
  "intentId": "intent_456",
  "steps": [
    {
      "stepId": "load_parcels",
      "kind": "QueryFeatures",
      "inputs": {
        "dataset": "parcels"
      },
      "dependsOn": []
    },
    {
      "stepId": "buffer_schools",
      "kind": "Geoprocess",
      "processId": "buffer",
      "inputs": {
        "source": "schools_all",
        "distance": "500",
        "distanceUnit": "meters"
      },
      "dependsOn": []
    },
    {
      "stepId": "rank_results",
      "kind": "Aggregate",
      "inputs": {
        "source": "candidate_parcels",
        "metric": "flood_risk_score"
      },
      "dependsOn": ["load_parcels", "buffer_schools"]
    },
    {
      "stepId": "compose_map",
      "kind": "RenderMap",
      "inputs": {
        "template": "analysis_default"
      },
      "dependsOn": ["rank_results"]
    }
  ],
  "outputs": [
    "FeatureLayer",
    "Map"
  ],
  "warnings": []
}
```

Steps form a directed acyclic graph. `dependsOn` lists step identifiers that must
complete before the step can execute. `inputs` values are strings; callers encode
structured values as string representations.

Supported step `kind` values: `QueryFeatures`, `Geoprocess`, `Aggregate`,
`RenderMap`, `Export`. Geoprocess steps should include a `processId` identifying
the operation to execute.

### BuilderPlan

Represents app-generation work.

Example responsibilities:

- choose app template
- bind datasets and artifacts
- choose widgets or panels
- define workflows and actions
- generate SDK-specific assets

### ExecutionJob

Represents durable execution state.

Conceptual shape:

```json
{
  "jobId": "job_123",
  "planId": "plan_123",
  "status": "Running",
  "progressPercent": 42,
  "currentStepId": "buffer_schools",
  "messages": [],
  "startedAt": "2026-04-09T18:00:00Z"
}
```

### ArtifactRef

References a concrete output artifact.

Conceptual shape:

```json
{
  "artifactId": "artifact_candidate_parcels",
  "kind": "FeatureLayer",
  "label": "Candidate Parcels",
  "uri": "honua://workspaces/ws_123/layers/candidate_parcels",
  "contentType": "application/geo+json",
  "metadata": {}
}
```

Supported `kind` values: `Scalar`, `FeatureLayer`, `Table`, `Raster`, `File`,
`Report`, `Map`, `AppBundle`.

### WorkspaceRef

References a managed working-state container.

Conceptual shape:

```json
{
  "workspaceId": "ws_123",
  "kind": "Scratch",
  "label": "Analysis scratch workspace",
  "uri": "honua://workspaces/ws_123",
  "expiresAt": "2026-04-10T18:00:00Z"
}
```

Supported `kind` values: `Scratch`, `Persistent`, `TempLayer`, `SavedLayer`,
`ResultCollection`.

### StyleRef

References a reusable style asset or renderer bundle.

Supported responsibilities should include:

- thematic renderer selection
- style preset reuse
- label and popup bindings
- legend generation inputs

### MapTemplate

References a reusable cartographic composition template.

Examples:

- analysis default
- dashboard map
- print-friendly review map

### ThemeSpec

Defines reusable visual tokens applied across maps and apps.

Examples:

- color ramps
- typography tokens
- spacing and panel chrome
- semantic status colors

### SourceBinding

Describes how a map package binds a layer or source to a concrete protocol-backed
data surface.

Required responsibilities:

- identify the backing protocol
- identify the endpoint or service locator
- record server-side filter/query semantics when applicable
- remain composable with other source bindings in the same map

Implementation rule:

- core spatial protocols should have first-party JS adapters in `honua-sdk-js`
- this first-party set should include GeoServices REST, OGC API, WFS, and WMS
- the operator contract should standardize the binding semantics while allowing
  selected non-core protocols to be fulfilled by wrapped canonical libraries
- OData is the clearest candidate for an external canonical JS client/library
  rather than a bespoke spatial protocol implementation

Supported source families should include:

- GeoServices feature/map services
- OGC API Features / Maps / Tiles
- OData entity collections where spatial/tabular composition is appropriate
- vector/raster tile sources
- generated artifacts and workspace outputs

### MapPackage

`MapPackage` is required for analysis workflows that produce spatial output.
The concrete `MapPackage` type is defined in downstream ticket #730; until then,
`AnalysisResultPackage.MapPackageId` is a nullable deferred reference.

Conceptual shape:

```json
{
  "mapPackageId": "map_123",
  "format": "honua_map_package.v1",
  "templateId": "analysis_default",
  "sourceBindings": [
    {
      "sourceId": "parcels",
      "protocol": "geoservices_feature_service",
      "locator": {
        "url": "https://example.com/rest/services/parcels/FeatureServer/0"
      }
    },
    {
      "sourceId": "boundaries",
      "protocol": "ogc_features",
      "locator": {
        "url": "https://example.com/ogc/collections/admin-boundaries"
      }
    }
  ],
  "styleRefs": [
    "style_candidate_parcels_choropleth"
  ],
  "themeId": "theme_flood_risk_light",
  "mapSpec": {
    "version": 8,
    "name": "Flood Risk Candidate Parcels",
    "sources": {},
    "layers": []
  },
  "initialView": {
    "bbox": [-158.2, 21.2, -157.6, 21.7],
    "crs": "EPSG:4326"
  },
  "legend": [],
  "popupBindings": [],
  "labelBindings": [],
  "previewArtifactId": "artifact_preview_png",
  "boundArtifacts": [
    "artifact_candidate_parcels"
  ]
}
```

`mapSpec` should build on the `HonuaMapSpec` direction in
[SDK Native Design Vision](../contributor/SDK_NATIVE_DESIGN_VISION.md) and
ultimately target MapLibre-based runtimes.

Styling scope for v1 should include:

- renderer selection and thematic styling
- labels
- popups
- legends
- template and theme application
- natural-language refinement of map presentation
- mixed protocol source composition in a single runnable map

It does not need full desktop print-layout parity in the first version.

### AppPackage

`AppPackage` packages a runnable Honua SDK application scaffold.

V1 should target `honua-sdk-js` first, backed by a MapLibre GL JS runtime.
That does not imply the JS SDK must natively implement every protocol client in
v1, but it should own first-party adapters for the core spatial protocols.
Adapter-backed composition remains acceptable for selected secondary protocols
such as OData as long as the runtime contract stays consistent.

Conceptual shape:

```json
{
  "appPackageId": "app_123",
  "targetSdk": "honua-sdk-js",
  "templateId": "analysis_dashboard",
  "format": "honua_app_package.v1",
  "entryPoint": "src/main.ts",
  "generatedFiles": [
    "src/main.ts",
    "src/map.ts",
    "src/widgets/resultsTable.ts"
  ],
  "bundleArtifactId": "artifact_app_bundle_tarball",
  "assetManifest": [
    {
      "path": "dist/index.html",
      "contentType": "text/html"
    }
  ],
  "mapPackageId": "map_123",
  "runtimeConfigSchema": {
    "type": "object"
  },
  "deliveryHints": {
    "hostingMode": "static_site",
    "defaultRoutePrefix": "/apps/flood-risk-review"
  },
  "boundArtifacts": [
    "artifact_candidate_parcels",
    "artifact_results_csv"
  ]
}
```

`AppPackage` should be specific enough to support both export and hosted
delivery. That means the package contract should carry:

- a versioned package format
- a bundle artifact or file manifest
- runtime configuration schema and defaults
- hosting and route hints
- map and artifact bindings required at runtime

### Deployment

`Deployment` operationalizes a process, pipeline, published service, map
package, or app package into a routable runtime surface.

Conceptual shape:

```json
{
  "deploymentId": "dep_123",
  "deploymentKind": "app_package",
  "targetRef": "app_123",
  "hostingMode": "static_site",
  "routePrefix": "/apps/flood-risk-review",
  "publicUrl": "https://honua.example.com/apps/flood-risk-review",
  "revisionId": "rev_7",
  "runtimeProfile": "browser_maplibre_js",
  "deliveryArtifacts": [
    "artifact_app_bundle_tarball"
  ],
  "runtimeConfig": {
    "apiBaseUrl": "https://honua.example.com",
    "mapPackageId": "map_123"
  },
  "visibility": "workspace_shared",
  "authPolicyRef": "policy_workspace_viewer",
  "approvalPolicyRef": "approval_publish_default",
  "publicationState": "published"
}
```

Required deployment responsibilities should include:

- define how a package becomes a hosted runtime surface
- define route, URL, and revision semantics
- define delivery artifacts and asset serving metadata
- define runtime config injection and secret-free client configuration
- define visibility and auth policy binding
- define publication state and rollout lifecycle
- remain consumable by orchestration hosts without redefining hosting semantics

### ProvenanceRecord

Audit trail recording the lineage of a geoprocessing result.

Conceptual shape:

```json
{
  "sources": [
    {
      "sourceId": "parcels",
      "version": "2026-04-09",
      "description": "City parcels dataset"
    }
  ],
  "processDefinitions": ["buffer", "spatial_join"],
  "assumptions": ["Used public schools dataset."],
  "clarificationsAsked": ["q_dataset"],
  "clarificationsAnswered": ["q_dataset"],
  "executedAt": "2026-04-09T18:05:00Z",
  "generatedArtifactIds": ["artifact_candidate_parcels"]
}
```

### AnalysisResultPackage

Final product returned to callers and agents.

Conceptual shape:

```json
{
  "resultPackageId": "result_123",
  "status": "Completed",
  "summary": {
    "title": "Candidate parcels ranked by flood risk",
    "description": "342 parcels found within 500 meters of schools."
  },
  "assumptions": [
    "Used public schools dataset."
  ],
  "artifacts": [],
  "workspaceRefs": [],
  "mapPackageId": null,
  "appPackageId": null,
  "provenance": {},
  "errors": []
}
```

`mapPackageId` and `appPackageId` are nullable deferred references. The concrete
`MapPackage` and `AppPackage` types are defined in downstream tickets (#730,
#731). In this example `mapPackageId` is null because the `MapPackage` type does
not yet exist; once #730 lands, spatial results will carry a non-null reference.

The package exposes factory methods `CreateCompleted` and `CreateFailed` for
terminal construction.

### GeoprocessingError

Structured error produced during a geoprocessing workflow.

Conceptual shape:

```json
{
  "kind": "ValidationFailed",
  "message": "Buffer distance must be positive.",
  "stepId": "buffer_schools",
  "violations": [
    {
      "code": "positive_required",
      "message": "Distance must be greater than zero.",
      "fieldPath": "inputs.distance"
    }
  ]
}
```

Supported `kind` values: `ValidationFailed`, `AuthorizationDenied`,
`UnknownDataset`, `UnknownProcess`, `ExecutionFailed`, `Timeout`, `Cancelled`,
`OutputBindingFailed`.

## gRPC Contract Families

gRPC is the typed execution plane.

Recommended public services:

### CatalogService

- `ListDatasets`
- `GetDataset`
- `ListProcesses`
- `GetProcess`
- `ListStyles`
- `GetStyle`
- `ListThemes`
- `ListTemplates`
- `ListMapSources`

### FeatureService

- `QueryFeatures`
- `QueryFeaturesStream`
- `Aggregate`

### ProcessService

- `ValidatePlan`
- `DryRunPlan`
- `ExecutePlan`
- `SubmitPlanJob`
- `GetJob`
- `GetJobResults`
- `CancelJob`

### WorkspaceService

- `CreateWorkspace`
- `GetWorkspace`
- `SaveArtifact`
- `PromoteArtifact`
- `ExpireArtifact`

### RenderService

- `CreateMapPackage`
- `RefineMapPackage`
- `ApplyStylePreset`
- `ComposeMixedProtocolMap`
- `GenerateLegend`
- `RenderPreview`
- `ExportMap`

### BuilderService

- `CreateAppPackage`
- `RefineAppPackage`
- `PreviewAppPackage`
- `ExportAppPackage`

## MCP Contract Families

MCP is the interaction and orchestration plane.

### Resources

- catalog resources
- dataset resources
- process definition resources
- style resources
- theme resources
- map template resources
- app template resources
- saved result package resources

### Tools

- `plan_analysis`
- `ground_candidates`
- `clarify_intent`
- `validate_plan`
- `execute_plan`
- `create_map_package`
- `refine_map_package`
- `apply_style_preset`
- `compose_mixed_protocol_map`
- `preview_map_package`
- `create_app_package`
- `preview_app_package`
- `publish_result`

### Prompts

Prompts are reusable workflow entrypoints:

- "site selection analysis"
- "hazard assessment"
- "permit review dashboard"
- "field operations map"

### Elicitation

Use MCP elicitation for:

- required missing inputs
- ambiguous dataset or process choices
- approval-required publish or destructive actions
- high-impact output settings

## Adapter Mapping

| Canonical concept | MCP | gRPC | Compatibility adapter |
|---|---|---|---|
| Capability discovery | resources/tools | `CatalogService` | service metadata endpoints |
| Feature access | tools | `FeatureService` | GeoServices / OGC / OData |
| Geoprocessing | tools | `ProcessService` | `GPServer`, OGC API Processes |
| Map composition | tools/resources | `RenderService` | GeoServices MapServer, OGC Maps |
| App generation | tools/resources | `BuilderService` | no direct legacy equivalent |

## Contract Boundaries

The model may propose:

- intents
- candidate datasets
- candidate plans
- style suggestions
- theme suggestions
- template selections
- app structure suggestions

The deterministic system must own:

- validation
- authorization
- state transitions
- artifact persistence
- output packaging
- provenance

## Versioning Guidance

- Canonical package names should be versioned, for example
  `honua.operator.v1`.
- Additive changes are allowed inside a major version.
- Breaking changes require a new major version and compatibility strategy.
- MCP tool contracts and gRPC schemas must evolve together from the same source
  model where practical.

## Related Documents

- [AI-First Operator Architecture](../contributor/AI_OPERATOR_ARCHITECTURE.md)
- [Deterministic Operator Workflow Results](DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md)
- [MCP Server](MCP_SERVER.md)
