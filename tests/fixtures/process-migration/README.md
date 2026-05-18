# Process Migration Evidence Fixtures

These fixtures define the first server-side process migration evidence slice.
They are scaffold contracts, not proof that arbitrary ArcPy, ModelBuilder,
GeoServer WPS, or OGC API Processes source workloads execute with full parity.

- `vector-process-parity-fixture.json` defines deterministic vector process
  cases the server and SDK runners can submit through OGC API Processes and
  GPServer.
- `expected-evidence-artifact.json` defines the evidence envelope expected from
  a parity runner after it polls status, retrieves results, and compares schema,
  geometry, counts, and result metadata.
