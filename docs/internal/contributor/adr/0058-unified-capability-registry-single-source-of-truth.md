# ADR-0058: Unified Capability Registry as Single Source of Truth for MCP, Spec Conformance, and Studio AI

## Status

Accepted (2026-07)

First-release gate. Tracked by the cross-repo epic honua-io/honua-release#32
("Capability registry unification + Studio AI binding"), which rolls up under
the release-engineering Phase 0/1 epic honua-io/honua-release#8. Track A of that
epic is the concrete Phase 1 **gate-c** interop fix
(`docs/RELEASE-ENGINEERING-PLAN.md` §4) for the MCP `FULL` conformance
false-pass (geospatial-mcp#25).

## Context

Honua describes the same catalog of geospatial capabilities to several
audiences: the `/mcp` surface (AI clients), the `geospatial-mcp` spec + its
conformance harness, the Console/Studio AI authoring flow, and the SDKs. The
**execution** of those capabilities is already unified — `IMapGenerationService`
and `StudioPackageLifecycleService` are shared engines, and ADR-0029 /
ADR-0057 keep geoprocessing on one canonical server engine. The fork is **not**
in execution. It is in the **capability description / discovery / contract**
layer, where five independent rosters have grown and now drift:

1. the `geospatial-mcp` spec + conformance manifest (`index.json`);
2. the `/mcp` tool roster in `McpServiceCollectionExtensions` /
   `McpToolSchemas`;
3. honua-server#1186 `CapabilityManifestService` (a per-environment manifest);
4. `StudioPackageFamilyRegistry` (Studio's package-family list);
5. the Console shim DTOs in `Honua.Console.Contracts`.

Each is hand-maintained against the others. The predictable failure is exactly
what the pre-release audit found: the MCP conformance `FULL` verdict passing
while the served surfaces disagree with the spec (geospatial-mcp#25) — a fake
gate that manufactures confidence. Two specific disagreements matter for first
release:

- **Resource-URI grammar.** The spec and the server had diverged on the
  resource-URI shapes clients dereference.
- **Advertised vs. served scope.** The manifests advertise capabilities that are
  deferred out of first release (temporal, disconnected-sync, realtime/geofence,
  cross-env metadata-promotion, SIEM, mTLS validation), so "what the platform
  claims" is not "what the platform serves."

ADR-0056 already sets the direction (a unified, governed MCP surface); this ADR
decides the **single source of truth** those surfaces derive from, and binds
Studio AI to the same source, at first-release scope.

## Decision

### Decision A — the server resource-URI grammar is the contract

The **server's** resource-URI grammar is canonical; the spec aligns to the
server, never the reverse. The first-release families are:

- `honua://map-packages/{id}`
- `honua://app-packages/{id}`
- `honua://published-services/{id}`
- `honua://jobs/{id}/results`

`McpResourceUris` is the **single seam** where this grammar is defined in code.
Every surface (served `/mcp` resources, the emitted conformance manifest, the
spec's `index.json`) references that seam; a conformance assertion pins the
served templates to it (honua-server A3, honua-io/honua-server#2332) and the
spec regenerates against it (geospatial-mcp A4/A5,
honua-io/geospatial-mcp#40 / #41).

### Decision B — a unifying capability registry in honua-server is the single source of truth; both `/mcp` and Studio AI bind to it

Introduce `CapabilityDescriptor` + `ICapabilityRegistry` in `Honua.Core`
(honua-server B1, honua-io/honua-server#2333) as the one roster. It mirrors the
live `/mcp` catalog and is guarded by a **runtime registry↔catalog conformance
check** so the registry and the served catalog cannot silently diverge.
Everything else becomes a **projection derived from** the registry rather than an
independent list.

### Derive, don't fork (the mechanism)

- The **`CapabilityManifestEmitter`** (honua-server A1/A2,
  honua-io/honua-server#2330 / #2331) emits the `geospatial-mcp` conformance
  manifest from the live catalog, committed and CI-regenerated so hand edits
  fail the build. It advertises the 20 first-release tools and declares the
  unserved result/artifact/provenance sub-families as **known-gap** — honest,
  not silently green.
- The emitter is then **promoted to read `ICapabilityRegistry`** and `/mcp` is
  **composed from** the registry, behind flag `Capabilities:RegistryBinding`
  (honua-server B2, honua-io/honua-server#2334).
- The `geospatial-mcp` spec/schemas/`index.json` **regenerate from the emitter
  output** and enforce it with `check_manifest.py --strict` in CI (geospatial-mcp
  A4/A5).

### Layered #1186 architecture (do not replace, layer)

honua-server#1186's `CapabilityManifestService` is **not** removed. It is
**layered onto** the registry as a **per-environment resolver** over the shared
descriptors, and `StudioPackageFamilyRegistry` becomes a registry projection —
behind flag `Capabilities:ManifestFromRegistry` (honua-server B3,
honua-io/honua-server#2335). The existing **`honua.capability_manifest.v1` wire
is preserved**: consumers see the same manifest shape; only its source of truth
changes from a hand-kept list to a registry projection. Per-env behavior (which
capabilities an environment exposes) stays a property of the resolver layer.

### Two mechanisms hold the experimental + disabled set OFF (the registry flag is one, not both)

The registry-derived manifest is **one of two** levers that keep the
experimental + disabled set (ADR-0059 §2) out of a default deployment — not a
single uniform lever over the whole set.

**(a) Registry-flag gating** covers exactly the experimental-tier capabilities
whose API routes are enumerated in the feature catalog's `experimental` tier —
the route-bearing descriptors: **temporal** analytics/versioning
(`/api/v1/temporal/*`, incl. as-of/diff/timeline and rollback/rollback-plan),
**disconnected-sync / replicas** (`/api/v1/admin/services/{id}/replicas` +
conflict-resolution), **realtime feature-streams**
(`/api/v1/streaming/features` + `/api/v1/admin/(operations/)streaming/*`),
**geofence alerting** (`/api/v1/admin/alerts/*`), and **mTLS client-certificate
validation** (`/api/v1/admin/security/client-certificates/*`). For these,
flipping the capability off in the registry removes it from the manifest,
`/mcp`, Studio availability, and Console at once — the single-lever behavior this
ADR gives the registry, and the manifest lever the Console release gate
(honua-io/honua-console#264) consumes.

> **Update (#2427).** **Geofence alerting** (`alerts.geofence`,
> `/api/v1/admin/alerts/*`) was promoted from `experimental` to `Implemented`
> (GA) — the first `Experimental → Implemented` promotion. It is therefore no
> longer part of the registry-flag experimental roster (a): its routes ship on
> the default first-release surface like any other GA capability. The alerts
> pipeline still self-gates on `Alerts:Enabled` (default `false`), so GA does not
> mean on-by-default; it means no longer hidden/unadvertised.

> **Update (#2429).** **Temporal analytics** (`temporal.filtering`,
> `temporal.extent-discovery`, `temporal.histogram`, `temporal.time-series-tiles`;
> `/api/v1/temporal/*` plus the FeatureServer `temporalExtent`/`queryDateBins`/
> time-series-tile surfaces) was promoted from `experimental` to `Implemented`
> (GA). It is therefore no longer part of the registry-flag experimental roster
> (a): its routes ship on the default first-release surface like any other GA
> capability. **Edition split is unchanged**: time filtering + extent discovery
> are Community; histogram (date-bins), time-series tiles, and the animation-API
> contract are Pro — those entitlement gates still apply, GA does not bypass
> licensing. Provider coverage is Postgres (all four surfaces) and DuckDB
> (filtering/extent/histogram); providers without temporal SQL translation
> (MySQL, SQL Server, Oracle, Snowflake, Redshift, Databricks) now uniformly
> reject a temporal filter with `NotSupportedException` — the fail-loud contract
> hardened in #2429 so no provider silently returns rows outside the requested
> window.

> **Update (#2431).** **mTLS client-certificate validation** (`security.mtls`,
> `/api/v1/admin/security/client-certificates/*`) was promoted from `experimental`
> to `Implemented` (GA), after
> hardening chain validation and CRL/OCSP revocation (a fail-closed
> revocation-status-unknown policy and a distinct revoked-vs-untrusted-chain
> outcome). It is therefore no longer part of the registry-flag experimental roster
> (a): its routes ship on the default first-release surface. mTLS carries an
> Enterprise entitlement (`identity.mtls-client-certificate`), so the manifest and
> registry advertise it as Enterprise-gated; GA does not mean on-by-default —
> client-certificate enforcement still self-gates on
> `Authentication:ClientCertificates:Mode` (default `Disabled`).

> **Update (#2428).** **Realtime feature streams** (`realtime.feature-streams`,
> `/api/v1/streaming/features` + `/api/v1/admin/(operations/)streaming/*`) — the
> WebSocket/SSE feature-change streams with subscription filters and durable
> replay cursors — were promoted from `experimental` to `Implemented` (GA).
> Those three route groups drop
> their `WithCapabilityGate("realtime.feature-streams")` and ship on the default
> first-release surface; the capability is no longer part of the registry-flag
> experimental roster (a). Streaming remains **Pro-edition** gated
> (`streaming.feature-subscriptions` entitlement, enforced per request), so GA
> means no longer hidden/unadvertised, not free-tier. GA-hardening added OTel
> telemetry (session/backpressure/heartbeat/replay/cluster-broadcast instruments
> on the shared `Honua` meter) and a `FeatureStreamHealthCheck` surfacing
> slow-consumer drops, session saturation, and cross-node broadcast backlog loss.

**(b) Edition/entitlement + Console-UI gating** covers the remainder of the
experimental + disabled set — the capabilities that are **not** held back by a
route-level registry flag: SSO/OIDC/SAML/SCIM, **forms** authoring + **field
data collection** (honua-collect), **mobile / offline**, **branch / versioned
editing**, SIEM / investigations, **cross-environment** metadata-promotion
(`/api/v1/admin/metadata` + deploy `MetadataRelease` ops) and dev→staging→prod
promotion, the **rollback** operate loop, and collaborative map sessions. These
are held OFF by edition/entitlement checks and Console-UI availability, not by
the registry manifest flag. (Format surfaces — GeoParquet / GeoArrow / GeoBuf /
FileGDB — are **not** in either gating set; they are ungated and ship as
supported for the first release, see ADR-0059 §1.) Note only
**cross-environment** metadata-promotion is gated; **single-instance GitOps
change-safety stays as the release operate floor.**

Folding more of the entitlement/UI-gated set (b) behind the registry flag (a) —
so the manifest becomes the uniform advertised-scope lever — is
post-first-release registry work, not a first-release claim.

### Studio stays REST; it binds via SDK projections

Studio is **not** re-platformed onto MCP. It stays a REST client and binds to the
registry through **SDK projections** — `Honua.Sdk.Studio` types
(honua-io/honua-sdk-dotnet#169, honua-io/honua-sdk-js#230). Console then
replaces its `Honua.Console.Contracts` shim DTOs with those SDK types across
`HonuaServerStudio*DataSource` (honua-console D1, honua-io/honua-console#265),
and wires `StudioIntentResolver` + `ICapabilityRegistryClient` so
generate→validate→preview→publish resolve to registry descriptors with
manifest-driven availability, behind flag `Studio:RegistryIntentResolution`
(honua-console D2, honua-io/honua-console#266). Execution stays on the already
shared engines; only description/discovery is unified.

## Scope Out

- No change to execution engines (`IMapGenerationService`,
  `StudioPackageLifecycleService`) — they are already shared.
- No new transport for Studio; it stays REST (no Studio-over-MCP).
- The deferred capabilities are gated, not deleted — they remain on trunk behind
  flags for a post-first-release wave.
- No change to the `honua.capability_manifest.v1` wire contract; only its
  producer changes.

## Consequences

**Easier**
- One conformance truth: the spec/`index.json` and the served `/mcp` catalog
  cannot disagree without failing CI — closing the gate-c false-pass
  (geospatial-mcp#25).
- Registry-flag scope lever (for the route-bearing experimental descriptors):
  advertised-vs-served is governed by the registry flag, so scoping those (and
  the Console gate) is a data decision, not five code edits. The rest of the
  experimental + disabled set stays held back by edition/entitlement + Console-UI
  gating (see "Two mechanisms hold the experimental + disabled set OFF") — a
  second lever the registry does not yet subsume.
- No re-fork: Console and the SDKs consume server-owned projections instead of
  local shim DTOs, ending the five-roster drift.
- Studio and `/mcp` inherit new capabilities automatically once a descriptor is
  registered.

**Harder / cost**
- A new required freeze discipline (below) and additional feature flags to
  stage the cutover (`Capabilities:RegistryBinding`,
  `Capabilities:ManifestFromRegistry`, `Studio:RegistryIntentResolution`).
- honua-server becomes the hard upstream for a cross-repo fan-out
  (geospatial-mcp, both SDKs, Console); its descriptor contract must be stable
  before downstream work starts.

## Freeze discipline

**Freeze `CapabilityDescriptor` and the `honua.capability_manifest.v1` wire
(honua-server B1/B3) before the Console/SDK fan-out (Tracks C and D) begins.**
Changing the descriptor or the manifest wire after the SDKs and Console have
projected from it re-introduces exactly the per-surface drift this ADR exists to
eliminate. Post-freeze changes follow the normal additive/contract-version
discipline (ADR-0054's drift gating; RELEASE-ENGINEERING-PLAN §1–2).

## References

- honua-io/honua-release#32 — cross-repo epic (Tracks A–D, dependency graph)
- honua-io/honua-release#8 — release-engineering Phase 0/1 epic;
  `docs/RELEASE-ENGINEERING-PLAN.md` §3–4 (dependency-ordered pipeline, gate
  stack, gate-c)
- honua-io/geospatial-mcp#25 — MCP `FULL` conformance false-pass (the gate this
  closes)
- ADR-0056 — MCP Redesign — Unified, Client-Agnostic, Governed Surface
- ADR-0029 / ADR-0057 — one canonical geoprocessing engine; thin clients
- ADR-0054 — Evidence-Based Feature Catalog (generated, drift-gated capability
  map)
- honua-io/honua-server#1186 — Console capability manifest endpoint (layered,
  not replaced)
- honua-io/honua-console#264 — Console release gate consuming the manifest lever
- honua-io/honua-sdk-dotnet#169, honua-io/honua-sdk-js#230 —
  `Honua.Sdk.Studio` projections (Track C)
