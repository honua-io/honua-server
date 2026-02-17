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
- [OData Test Parity](ODATA_TEST_PARITY.md) — OData v4 specification compliance
- [CITE OGC Features](cite-conformance-testing.md) — OGC API Features conformance
- [CITE OGC Tiles](cite-tiles-conformance-testing.md) — OGC API Tiles conformance

## CI/CD

- [CI Monitoring](CI_MONITORING.md) — CI health and alert monitoring
- [CI Workflows](ci-workflows.md) — GitHub Actions pipeline overview
- [CI Quality Gates](CI_QUALITY_GATES.md) — automated quality enforcement
- [CodeCov Setup](CODECOV_SETUP.md) — code coverage monitoring
- [LLM Architecture Review](development/llm-review-setup.md) — automated PR review
