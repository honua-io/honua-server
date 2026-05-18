# Contributor Documentation

This section is for people **building or extending** Honua (core contributors, agents, and external devs).

## Getting Started

- **Agent Guidelines** — see `AGENTS.md` in the repository root
- [Development Setup](development/getting-started.md) — prerequisites, installation, and first run
- [Contributing Guide](development/contributing.md) — code style, architecture rules, PR process
- [Local Helm Testing](development/k3d-helm.md) — handoff to the `honua-helm` repository

## Architecture

- [Architecture Overview](ARCHITECTURE.md) — system design and component interaction
- [Architecture Diagrams](ARCHITECTURE_DIAGRAMS.md) — visual system diagrams
- [ADRs](adr/README.md) — architectural decisions and rationale
- [Architecture Review Criteria](architecture-criteria.md) — PR review quality gates
- [Package and Module Governance](package-and-module-governance.md) — central package versions and optional module boundaries
- [Honua Manifesto](HONUA_MANIFESTO.md) — core principles
- Metadata v2 architecture artifacts:
  - [Backlog Index](architecture/metadata-v2-backlog.md) — local issue index and sequencing guide for `#1035`
  - [Roadmap](architecture/metadata-v2-roadmap.md) — milestone grouping derived from the GitHub issues
  - [Release Readiness](architecture/metadata-v2-release-readiness.md) — non-authoritative release gates derived from the GitHub issues
  - [Admin UI Information Model](architecture/metadata-v2-admin-ui-information-model.md) — Claude Design handoff for `#1046`
  - [Admin Operator Workflows](architecture/admin-operator-workflows.md) — Claude Design handoff for server management workflows tracked by `#1057`

## Design Patterns

- [Code Model Optimization](CODE_MODEL_OPTIMIZATION.md) — shared model classes across protocols
- [Adaptive Sampling](ADAPTIVE_SAMPLING.md) — dynamic trace sampling

## Testing

- [TestKit (C#)](testkit.md) — fixtures, builders, assertions, parallel execution, and the operator eval harness/report contract
- [Public Interface Quality Model](public-interface-quality-model.md) — canonical proof ledger, release evidence rules, and ticket reconciliation for public surfaces
- [Python Integration Tests](testing-python.md) — pytest OGC and FeatureServer tests
- [JavaScript Integration Tests](testing-javascript.md) — Vitest protocol coverage plus Playwright Esri Leaflet browser compatibility tests
- [Shared Seed Data](test-seed-data.md) — YAML seed format for cross-language tests
- [OGC Certification Path](ogc-certification-path.md) — formal certification decision, evidence taxonomy, and current baseline results
- [CITE OGC Features](cite-conformance-testing.md) — OGC API Features conformance
- [CITE OGC Tiles](cite-tiles-conformance-testing.md) — OGC API Tiles conformance
- [OGC API Maps Conformance](ogc-maps-conformance-testing.md) — OGC API Maps conformance gate
- [CITE WMS 1.3](cite-wms-conformance-testing.md) — OGC WMS conformance
- [CITE WMTS 1.0](cite-wmts-conformance-testing.md) — OGC WMTS conformance
- [MCP Certification](mcp-certification.md) — cross-repo MCP certification testing, seed data, and CI jobs

## CI/CD

- [CI Gate Model](../ci/gate-model.md) — five-tier quality gate definitions and governing rules
- [CI Workflow Inventory](../ci/workflow-inventory.md) — every workflow, its tier, triggers, and merge-blocking status
- [CI Config Conventions](../ci/config-conventions.md) — env vars, cache keys, artifact naming, and composite actions
- [CI Quality Gates](CI_QUALITY_GATES.md) — automated quality enforcement
- [Release Checklist](RELEASE_CHECKLIST.md) — required compatibility/client/caveat updates per release
- [LLM Architecture Review](development/llm-review-setup.md) — automated PR review

## Roadmaps

- [GeoETL Roadmap](geoetl-roadmap.md) — pipeline architecture, child-ticket decomposition, and runtime boundary for `#361`
- [GeoETL Strategy Spike](geoetl-spike.md) — competitor evaluation and positioning from `#682`
- [Metadata v2 Roadmap](architecture/metadata-v2-roadmap.md) — milestone grouping for `#1035` and child issues

## Project Operations

- [Weekly Backlog Review](BACKLOG_REVIEW_CADENCE.md) — triage, scope gate, and done/close hygiene cadence
