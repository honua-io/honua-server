# ADR-0054: Evidence-based feature catalog (generated, drift-gated capability map)

## Status

Proposed. Slice 1 (API-surface projection + drift guard + MCP resource) lands
with this ADR; later slices are tracked in #1946 (see roadmap below).

## Context

Honua's capabilities are real and enforced, but the *map* of those capabilities
is scattered and partly hand-maintained:

- `EndpointRegistry.All` is the canonical list of every deployed HTTP route, and
  architecture tests already hard-fail if a route lacks an integration test
  (`ApiSurfaceCoverageTests`) or a proof-ledger surface
  (`PublicInterfaceProofLedgerTests`).
- `[Endpoint("METHOD /...")]`, `[Operation]`, `[Protocol]`, and
  `[IntegrationTest]` attributes already link each route to its proving tests.
- `docs/gis/data/public-interface-proof.json` (the proof ledger) already maps
  every surface to its evidence (code locations, execution lanes, conformance).

But the human-facing capability docs are **drift-prone prose**. The canonical
example is `docs/reference/compatibility/geoservices-parity.md`: a hand-edited
table that conflicts on nearly every raster/imageserver PR (it caused multiple
rebases this week that touched *only* that file). Worse, the AI planner /
authoring layer has no machine-readable, evidence-backed capability surface to
ground on, so an agent can hallucinate a capability that does not exist, or
reimplement one that is already shipped, because nothing tells it *what is
proven*.

We already have the substrate. What is missing is a single **generated
projection** of it that an agent can read and that CI keeps honest.

## Decision

Introduce an **evidence-based feature catalog**: a generated, drift-gated JSON
artifact, `docs/gis/data/feature-catalog.json` (next to the proof ledger it
projects), where every entry is backed by an artifact CI already enforces, and
where adding an endpoint without catalog evidence fails the build.

Five principles (from #1946) govern the full vision:

1. **Generated, never authored.** The catalog is projected from registries +
   test attributes + the proof ledger. Hand-editing it fails the guard.
2. **Drift = red build.** A guard test fails if a registered capability has no
   catalog row, a row resolves to no real route/test, or the committed file
   differs from freshly-generated output.
3. **Status computed (later slice).** Green/red/flaky from the last trunk CI per
   shard — not asserted by a human.
4. **Maturity is honest.** `implemented` requires green tests; `partial`/`deferred`
   must link the open issue + remaining AC. "Implemented with no test" is
   structurally impossible.
5. **Agent surfaces.** An in-repo JSON artifact and a read-only MCP resource
   (`honua://catalog/features`) so agents discover capabilities + evidence at
   runtime and can only claim what has evidence.

### Slice 1 (this ADR): the API surface

Slice 1 projects the **HTTP API surface only** and keeps the wiring thin.

**Schema** (`feature-catalog.json`, snake_case, source-generated, stable
ordering by method then route):

```jsonc
{
  "schema_version": "1.0.0",
  "generator": "tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/FeatureCatalogGenerator.cs",
  "tracking_issue": "#1946",
  "slice": "slice-1-api-surface",
  "entries": [
    {
      "id": "get-rest-services-serviceid-featureserver-layerid-query",
      "route": "/rest/services/{serviceId}/FeatureServer/{layerId}/query",
      "method": "GET",
      "family": "GeoServices FeatureServer",      // proof-ledger surface protocol label
      "protocol": "http-route-family",            // proof-ledger surface kind
      "code_location": "src/.../FeatureServerEndpoints.Query.cs",  // mapped source from the proof ledger
      "proving_tests": [                           // [Endpoint]-attributed test ids covering this route
        "Honua...QueryEndpointTests.Query_WithWhereClause_ReturnsFilteredFeatures"
      ],
      "proof_ledger_surface": "feature-server",
      "maturity": "implemented"
    }
  ]
}
```

For slice 1, `maturity` is `"implemented"` for every `EndpointRegistry.All`
route that has at least one proving test — which, given the existing API-surface
coverage gate, is all of them. `status`-from-CI and `partial`/`deferred`
maturity are explicitly deferred to later slices.

**Generation pipeline.** `FeatureCatalogGenerator.Generate()` (in
`Honua.Architecture.Tests`, alongside the proof-ledger tests it mirrors)
deterministically joins three already-enforced sources:

- `EndpointRegistry.All` — one entry per distinct `(method, route)`;
- the `[Endpoint]`-attributed integration tests discovered via the shared
  `ArchitectureTestHelpers.IntegrationTestMethods()` helper (the same discovery
  the API-surface coverage gate uses) — the `proving_tests` column;
- the proof ledger (`public-interface-proof.json`), matched by the same
  exact/prefix/contains selectors the ledger governance test enforces — the
  `family`, `protocol`, `code_location`, and `proof_ledger_surface` columns.

The same `Generate()` backs both the **regeneration entry point** and the
**drift guard**, so the two can never disagree. Regeneration is a `[Fact]`
emitter (`FeatureCatalogEmitter`) gated behind `HONUA_EMIT_FEATURE_CATALOG=1`
(so ordinary `dotnet test` runs stay read-only), invoked by
`scripts/generate-feature-catalog.sh`.

**Freshness-gate contract (drift = red build).** `FeatureCatalogDriftTests` (in
`Honua.Architecture.Tests`, `[ArchitectureTest]`, mirroring the proof-ledger
tests) enforces:

- every `EndpointRegistry.All` route has a catalog entry;
- every catalog entry resolves to a registered route **and** ≥1 proving test;
- the committed `feature-catalog.json` **equals** freshly-generated output.

So a new endpoint added without regenerating, or a hand-edit, fails the build.

**Agent surface (MCP).** `FeatureCatalogResource` serves the read-only resource
`honua://catalog/features`. The committed, drift-gated artifact is embedded into
`Honua.Ai` (logical name `Honua.Ai.Catalog.feature-catalog.json`) so a deployed
server with no repo checkout serves *exactly what CI gated* — reading the
embedded text verbatim keeps it AOT-safe and prevents a runtime re-projection
from disagreeing with the committed file. The resource is registered in the
**default** server composition (`AddMcpDataAccessSurface` → `FeatureRegistrationExtensions`),
unlike the persistence-backed promotion resources which stay opt-in: the catalog
has no runtime dependency and cannot advertise an empty or stale surface.

### Maturity tiers (full vision)

| tier | meaning | slice |
|---|---|---|
| `implemented` | registered route + green proving test(s) | slice 1 |
| `partial` | shipped but incomplete; links open issue + remaining AC | later |
| `deferred` | intentionally not built; links the tracking issue | later |
| `planned` | enumerated, not yet built | later |

## Consequences

- **Positive.** A machine-readable, evidence-backed capability map exists; agents
  ground on it instead of prose. Drift becomes a red build. The forthcoming
  retirement of `geoservices-parity.md` removes a recurring merge-conflict tax.
- **Cost.** One more generated artifact to regenerate when the API surface or its
  proving tests change (one script invocation; the guard tells you when). The
  catalog is large (one row per route) but deterministic and diff-friendly.
- **Scope discipline.** Slice 1 deliberately does **not** compute status from CI,
  populate partial/deferred maturity, cover `OperationRegistry` / the MCP tool
  catalog / `WorkflowNodeRegistry` / operations-toolset descriptors, generate
  `AGENTS-CAPABILITIES.md`, or retire `geoservices-parity.md`. Those remain
  prose/registry-driven until their slices land.

## Slice roadmap (#1946)

- **Slice 1 (this ADR):** API-surface projection + drift guard + MCP resource.
- Extend coverage to `OperationRegistry`, the MCP tool catalog,
  `WorkflowNodeRegistry`, and operations-toolset descriptors.
- Compute `status` from last-green trunk CI per shard (`ci-shards.json` join) +
  CITE conformance refs.
- Populate `maturity=partial` + `remaining{issue, ac}` from open issues.
- Generate `AGENTS-CAPABILITIES.md`; retire `geoservices-parity.md` to a
  generated projection.
- Grounding engine consumes the catalog.

## References

- Tracking issue: #1946.
- Substrate: `src/Honua.Server/EndpointRegistry.cs`,
  `docs/gis/data/public-interface-proof.json`,
  `tests/dotnet/Honua.Architecture.Tests/{ApiSurfaceCoverageTests,PublicInterfaceProofLedgerTests}.cs`.
- ADR-0011 (API surface coverage), ADR-0037 (CI test tiers/shards).
