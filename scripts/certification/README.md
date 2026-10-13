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

## Governed receipt context

Certification dispatches supply both `nightly_dispatch_id` and the bounded
`honua.protocol-producer-context/v1` requirements context, including the exact
candidate source, image digest, cut timestamp and requirements revision. The
producer validates the complete implemented roster against its own capability
matrix and assignment contract before executing tests. Unknown, missing,
duplicate or altered rows fail closed. The receipt uses
`honua.certification-evidence-receipt/v2` and binds that context and the actual
workflow run and attempt into its digest.

All 51 declared operations still execute across all five owning test projects.
Only the 42 operations whose capabilities are implemented in the source matrix
emit governed observations; the nine Preview operations remain runtime checks.
Source-host observations and receipt identities retain a null image digest; the
outer candidate tuple records the separately verified candidate image.

Scheduled or manual runs without both context inputs run the same runtime tests
and retain TRX files under `server-protocol-harness-runtime-<run>-<attempt>`. They
emit no certification fragment. A nonzero test exit still fails the workflow.
A dispatch with context also supplies an explicit candidate cut timestamp.

Execution metadata names the canonical source harness and its contract revision,
with profile `source-test-host-http-adapter`. The TRX records actual test methods
and outcomes but does not capture an absolute request URL; `request_url` is
therefore null. The receipt payload binds this execution metadata alongside
the test outcomes. This profile neither claims a network endpoint nor TLS, and
requires the corresponding narrowly scoped evidence collector support.
