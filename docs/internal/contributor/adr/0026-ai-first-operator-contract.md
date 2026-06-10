# ADR-0026: AI-First Operator Contract as Primary Public Contract

## Status

Accepted (2026-05). The canonical types
(`AnalysisIntent`, `ClarificationRequest`, `AnalysisPlan`,
`AnalysisResultPackage`, `MapPackage`, `ProvenanceRecord`, etc.) live in
`src/Honua.Core/Features/Geoprocessing/Domain/` and are surfaced through
the MCP tool catalog (`src/Honua.Server/Features/Protocols/Mcp/Tools`).
Compatibility surfaces (GeoServices, OGC API Processes, OData) remain
projections of this canonical contract.

## Context

Honua is moving toward an AI-first geospatial product strategy:

1. AI-first contract
2. cloud-native runtime
3. compatibility adapters

The current documentation and public surfaces are still largely organized around:

- GIS protocols
- server runtime details
- SDK transport choices

That is useful for compatibility, but it does not provide a primary contract for
agent-native analyst and builder workflows.

Honua needs a public contract that lets an agent:

- discover data and capabilities
- gather missing requirements
- plan geospatial analysis
- execute deterministic feature and geoprocessing workflows
- produce map outputs
- generate SDK-native app outputs

At the same time, Honua must preserve compatibility with GeoServices, OGC, and
other external standards.

## Decision

Honua will treat the AI-first operator contract as the primary public contract.

### Primary Canonical Concepts

The primary contract will be defined around transport-neutral semantic objects:

- `AnalysisIntent`
- `ClarificationRequest`
- `AnalysisPlan`
- `ExecutionJob`
- `WorkspaceRef`
- `ArtifactRef`
- `MapPackage`
- `AppPackage`
- `AnalysisResultPackage`
- `ProvenanceRecord`

### Primary External Surfaces

The primary external surfaces will be:

- MCP for agent interaction
- gRPC for typed execution

### Secondary Compatibility Surfaces

Compatibility protocols are projections of the canonical contract:

- GeoServices REST
- OGC API Processes
- OGC API Features / Maps / Tiles
- OData

These surfaces remain supported, but they do not define the internal ontology.

### Map And App Outputs

Maps and apps are first-class outputs of the operator contract.

Non-trivial analysis should produce:

- output artifacts
- explicit provenance
- a `MapPackage`

When requested or appropriate, the same workflow may also produce an
`AppPackage` for Honua SDK-based app generation.

### Style Foundation

`MapPackage` will build on the existing ADR-0002 decision: MapLibre remains the
canonical style basis. Honua-specific packaging extends that style model rather
than replacing it.

## Consequences

### Positive

- Honua's public model better matches the product strategy.
- MCP and gRPC can be designed around analyst and builder work instead of
  protocol wrappers.
- GeoServices and OGC remain important without constraining the internal model.
- Result packaging becomes strong enough to support a "GIS desktop killer"
  workflow rather than tool-by-tool automation.

### Negative

- Honua now has to own and version a richer semantic contract, not just
  transport bindings.
- The public surface area grows beyond query and simple MCP data access.
- More evaluation and compatibility testing is required to keep MCP, gRPC, and
  adapter projections aligned.

### Follow-On Work

- Define the detailed AI operator contract for MCP and gRPC.
- Define deterministic workflow and result package specifications.
- Add evaluation suites that run the same analyst tasks in Claude and Codex.
- Add adapter tickets for GeoServices `GPServer` and OGC API Processes based on
  the canonical process model.
