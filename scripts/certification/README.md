---
type: reference
---

# Source-host protocol harness

`protocol-harness-certification.yml` executes the exact test IDs in
`docs/gis/data/protocol-harness-assignments.v1.json` in their owning projects.
The fragment records `source-test-host` and a null image digest. It is not an
execution against the submitted candidate container. Missing or failed governed
tests cannot become passing observations.

The WPS assignments exercise the HTTP adapter over a controlled canonical job
service. They exclude the separate conformance echo path and do not prove a
production executor or durable job store.

The four `analytics.h3` assignments exercise real HTTP handlers with controlled
provider inputs and Community entitlement denials. JSON assertions bind the
selected storage layer and resolution to an exact controlled cell/count response.
The tile assertion proves opaque byte transport from the bound provider. These
assignments claim only positive and negative adapter facets. They do not prove
valid MVT/schema, H3 cell computation, h3-pg execution, aggregation fidelity,
engine performance, or a published candidate image. The test provider deliberately
returns a marker cell and distinct opaque bytes so fallback routing is observable.

Full H3 computation and tile-format qualification require their own actual engine
and fixture evidence; these source-host observations cannot substitute for it.
