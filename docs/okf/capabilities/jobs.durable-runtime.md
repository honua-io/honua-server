---
type: capability
title: "Durable Job Runtime"
description: "The durable job substrate behind imports, tile operations, geoprocessing and workflow orchestration. Advertised as jobs.runner on the honua.capability_manifest.v1 wire and named by typed dependency-unavailable refusals, so a client receiving one can resolve the id. The runtime itself is Community; durable persistence across restarts and nodes requires Redis, which caching.redis gates."
resource: "honua://capability/jobs.durable-runtime"
tags: [capability, jobs, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Durable Job Runtime

The durable job substrate behind imports, tile operations, geoprocessing and workflow orchestration. Advertised as jobs.runner on the honua.capability_manifest.v1 wire and named by typed dependency-unavailable refusals, so a client receiving one can resolve the id. The runtime itself is Community; durable persistence across restarts and nodes requires Redis, which caching.redis gates.

| | |
| --- | --- |
| Capability key | `jobs.durable-runtime` |
| Category | Jobs |
| Edition | Community |

The facts above come from `docs/gis/data/capability-keys.v1.json` and `capability-matrix.v1.json`, both generated from the server's own registry and test evidence. This page is a pure function of those two files — nothing in it depends on what the prose happens to say, so an unrelated documentation edit cannot stale it.

Which pages discuss this capability is a question about the prose, so it is reported rather than baked in: run `scripts/ci/generate-capability-concepts.py --report`.
