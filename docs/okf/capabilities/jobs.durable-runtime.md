---
type: capability
title: "Durable Job Runtime"
description: "The durable job substrate behind imports, tile operations, geoprocessing and workflow orchestration. Advertised as jobs.runner on the honua.capability_manifest.v1 wire and named by typed dependency-unavailable refusals, so a client receiving one can resolve the id. The runtime is Community and persists jobs in the PostgreSQL system database, so they survive restarts and move between nodes."
resource: "honua://capability/jobs.durable-runtime"
tags: [capability, jobs, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Durable Job Runtime

The durable job substrate behind imports, tile operations, geoprocessing and workflow orchestration. Advertised as jobs.runner on the honua.capability_manifest.v1 wire and named by typed dependency-unavailable refusals, so a client receiving one can resolve the id. The runtime is Community and persists jobs in the PostgreSQL system database, so they survive restarts and move between nodes.

| | |
| --- | --- |
| Capability key | `jobs.durable-runtime` |
| Category | Jobs |
| Edition | Community |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Automate workflows](../../guides/query-analyze/automate-workflows.md)
