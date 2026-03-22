# Contributor Documentation

This section is for people **building or extending** Honua (core contributors, agents, and external devs).

## Getting Started

- **Agent Guidelines** — see `AGENTS.md` in the repository root
- [Development Setup](development/getting-started.md) — prerequisites, installation, and first run
- [Contributing Guide](development/contributing.md) — code style, architecture rules, PR process
- [K3d + Helm Development](development/k3d-helm.md) — local Kubernetes development

## Architecture

- [Architecture Overview](ARCHITECTURE.md) — system design and component interaction
- [Architecture Diagrams](ARCHITECTURE_DIAGRAMS.md) — visual system diagrams
- [Esri Migration Platform Plan](ESRI_MIGRATION_PLATFORM_PLAN.md) — JS-first migration architecture and phased SDK strategy
- [Pilot Evidence Kit](migration/README.md) — Scorecards, checklists, and readout templates for lighthouse migration pilots
- [ADRs](adr/README.md) — architectural decisions and rationale
- [Architecture Review Criteria](architecture-criteria.md) — PR review quality gates
- [Honua Manifesto](HONUA_MANIFESTO.md) — core principles

## Design Patterns

- [Code Model Optimization](CODE_MODEL_OPTIMIZATION.md) — shared model classes across protocols
- [GIS Crosscutting Concerns](GIS_CROSSCUTTING_CONCERNS.md) — spatial data handling patterns
- [Adaptive Sampling](ADAPTIVE_SAMPLING.md) — dynamic trace sampling

## Testing

- [TestKit (C#)](testkit.md) — fixtures, builders, assertions, parallel execution
- [Python Integration Tests](testing-python.md) — pytest OGC and FeatureServer tests
- [JavaScript Integration Tests](testing-javascript.md) — Vitest Esri compatibility tests
- [Shared Seed Data](test-seed-data.md) — YAML seed format for cross-language tests
- [Benchmarks](benchmarks.md) — BenchmarkDotNet performance tests
- [Production Audit Playbook](PRODUCTION_AUDIT_PLAYBOOK.md) — phased production-readiness audit execution
- [OData Test Parity](ODATA_TEST_PARITY.md) — OData v4 specification compliance
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
- [CI Monitoring](CI_MONITORING.md) — CI health and alert monitoring
- [CI Quality Gates](CI_QUALITY_GATES.md) — automated quality enforcement
- [Release Checklist](RELEASE_CHECKLIST.md) — required compatibility/client/caveat updates per release
- [CodeCov Setup](CODECOV_SETUP.md) — code coverage monitoring
- [LLM Architecture Review](development/llm-review-setup.md) — automated PR review

## Project Operations

- [Weekly Backlog Review](BACKLOG_REVIEW_CADENCE.md) — triage, scope gate, and done/close hygiene cadence
