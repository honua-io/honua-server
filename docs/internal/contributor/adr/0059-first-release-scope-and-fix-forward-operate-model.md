# ADR-0059: First-release scope and fix-forward operate model

## Status

Accepted (2026-07)

## Context

The first platform release (the RC gated by honua-io/honua-release#32) needs a
single, fixed statement of **what must work and be certified** versus **what is
present in code but intentionally held back**, plus the **operate model** the
release actually ships with. Two prior artifacts frame this space but do not, on
their own, pin the release line:

- [ADR-0054](0054-evidence-based-feature-catalog.md) defines an evidence-based
  feature catalog with `maturity` tiers (`implemented` / `partial` / `deferred`
  / `planned`) and a drift-gated capability map — but has no tier for "shipped
  in the binary yet deliberately disabled and unadvertised for this release."
- ADR-0058 (capability registry — the `ICapabilityRegistry` /
  `CapabilityDescriptor` single-source-of-truth recorded in
  honua-io/honua-release#32) makes the registry-derived **capability manifest**
  the single lever that decides what is advertised and reachable, but does not
  itself enumerate the first-release in/out split.
- honua-console `docs/roadmap/FIRST_RELEASE_STRATEGY_AND_CUT_LINE.md` draws the
  depth cut-line and, critically, asserts a **"non-negotiable operate floor"**
  whose exit criterion is *"propose → preflight → approve → apply → **roll
  back**, without ever touching Git"* and *"failure auto-rolls-back."*

That operate floor cannot be honestly certified for the first release.
**Rollback / auto-rollback was never certified**: the health-gated
auto-rollback path is not exercised in CI and does not run as part of any
release gate (per the release-readiness audits under `_release-audit/` and the
safe-rollout audit notes). Asserting a rollback floor we cannot prove
contradicts the evidence-based posture of ADR-0054 ("implemented with no test is
structurally impossible"). Cross-environment promotion (the dev→staging→prod
fleet path, honua-io/honua-devops#57/#58) is likewise not part of the
single-environment release and was already flagged gated exotic.

We need to (a) fix the in-scope/out-of-scope line, (b) give the catalog a tier
that means "in the binary but off," and (c) replace the rollback operate floor
with the operate model we can actually stand behind: **health-gated AI
fix-forward** via the AI operator surface.

## Decision

### 1. First-release in-scope set (must work, must be certified)

The following are **in first-release scope** — they must function and must be
certified (CITE where applicable, integration/conformance-tested, and
advertised in the capability manifest):

- **AI Studio authoring** — the generate → validate → preview → publish package
  pipeline.
- **Geoprocessing / GP** — the server-canonical engine and its protocol surfaces
  (per [ADR-0057](0057-geoprocessing-capability-boundaries.md)).
- **Core server** — OGC (WMS/WFS/WCS/WMTS + OGC API), Esri GeoServices REST
  (incl. GPServer), STAC, OData, and tiles/MVT, CITE-certified.
- **Console Studio** — the `map` / `query` / `analysis` / `dashboard` / `report`
  / `app` package families.
- **SDKs** — `honua-sdk-js`, `honua-sdk-dotnet`, `honua-sdk-python`.
- **Single-environment deploy** — one deployable artifact, one origin, one
  environment.
- **Migration / interop** — live Esri REST migration, FileGDB import, Esri REST
  northbound interop (sufficient, not exhaustive parity).
- **AI operator surface with fix-forward** — the operate model below.

### 2. Experimental + disabled set (present in code, held back)

The following are **experimental for the first release**: they carry
`maturity: experimental` in the feature catalog, are **NOT advertised in the
capability manifest**, and their surfaces/endpoints are **disabled / gated off**.
They may be present in the binary but must not appear in the manifest, `/mcp`,
Studio availability, or Console. Each links its tracking issue:

- **Mobile** (honua-mobile) — mobile / offline capabilities.
- **Forms + field collection** — `form.package` / forms authoring and field
  data collection (honua-collect).
- **Temporal / data-versioning** — honua-io/honua-server#1166.
- **Versioned editing** — honua-io/honua-server#371.
- **Disconnected-sync conflict review** — honua-io/honua-server#1167.
- **Realtime / geofence alerting** — honua-io/honua-server#1169.
- **SIEM / investigations** — honua-io/honua-devops#59.
- **Native MAUI host + mTLS** — honua-io/honua-server#1171.
- **Exhaustive GP/ETL node breadth + custom script/model tools** —
  honua-io/honua-server#1185.
- **Cross-environment deploy / promotion** (dev→staging→prod fleet) —
  honua-io/honua-devops#57 / #58.
- **Rollback / auto-rollback** — the propose→preflight→approve→apply→**rollback**
  operate loop, including health-gated auto-rollback (superseded by fix-forward,
  see §4).
- **Governance auth** — SAML / CAC / PIV / FIPS (honua-io/honua-server#1275) and
  SSO / OIDC (honua-io/honua-server#3240, #1372).
- **Collaborative map sessions** — honua-io/honua-server#971.

"Experimental + disabled" is a deliberate ship-it-off posture, not a "not built"
claim: the code may exist and even be exercised in isolation, but it is not
certified for this release and is not reachable by a default deployment.

### 3. Feature-catalog `experimental` tier

Extend the ADR-0054 maturity model with a fifth tier:

| tier | meaning |
|---|---|
| `implemented` | registered surface + green proving test(s) — certified, advertised |
| `partial` | shipped but incomplete; links open issue + remaining AC |
| `deferred` | intentionally not built; links the tracking issue |
| `planned` | enumerated, not yet built |
| **`experimental`** | **present in code but intentionally disabled and unadvertised for this release; gated OFF in the capability manifest** |

`experimental` differs from `deferred`: `deferred` means *not built*, whereas
`experimental` means *built (or partly built) but held back*. An
`experimental` capability MUST be gated off in the registry-derived capability
manifest, so it does not surface in the manifest, `/mcp`, Studio availability,
or Console — exactly the single-lever behavior ADR-0058 gives the registry.

This ADR **specifies** the tier and its manifest semantics; it does **not**
implement the catalog/registry change. Realization is owned by the capability
registry work — **honua-io/honua-server#2335 (Track B3)** — which gates the
deferred/experimental endpoints off the manifest. The `FeatureCatalogGenerator`
/ drift-guard changes (ADR-0054 slices) land there, not here.

### 4. Operate model: health-gated fix-forward, not rollback

The first release operates as **single-environment deploy with NO rollback**.
Safety is delivered by **health-gated AI fix-forward** (roll-forward
convergence) via the AI operator surface: when a change degrades health, the AI
operator proposes and applies a *forward* corrective change that converges the
system back to a healthy state, rather than reverting to a prior state.

This **replaces** the cut-line's
"propose → preflight → approve → apply → **rollback**" operate floor and its
"failure auto-rolls-back" exit criterion.

**Rationale — rollback was never certified.** The health-gated auto-rollback
path is not tested or run in CI and is not exercised by any release gate (per
the release-readiness / safe-rollout audits). Shipping a rollback floor we
cannot prove would violate the evidence-based posture of ADR-0054. Fix-forward
is the safety model we can actually certify for a single environment, and it is
already the convergence model the merge-train / operator surface uses
elsewhere ("roll-forward-first convergence"). Rollback / auto-rollback and
cross-environment promotion therefore move to the experimental + disabled set
(§2) and can light up post-release when they are tested and gated in.

## Consequences

- **Positive.** The release has one fixed, evidence-honest in/out line. The
  capability manifest advertises only what is certified; experimental surfaces
  cannot leak into `/mcp`, Studio, or Console. The operate story stops
  asserting an uncertified rollback floor and states the safety model we can
  prove (fix-forward). ADR-0054's catalog gains the missing tier for
  "shipped-but-off."
- **Cost / risk.** Fix-forward assumes the AI operator surface can converge a
  degraded single environment; there is no automatic revert safety net, so the
  operator surface's health-gating and forward-correction quality become
  load-bearing for the release. Governance auth (SSO/OIDC, SAML/CAC/PIV/FIPS)
  being experimental means the first release does not advertise enterprise SSO —
  scope this in GTM.
- **Follow-through.** honua-io/honua-server#2335 (Track B3) must add the §2 set
  to its disabled list at `maturity: experimental`; the cut-line doc must stop
  asserting rollback as the non-negotiable floor (tracked against
  `FIRST_RELEASE_STRATEGY_AND_CUT_LINE.md`).

## References

- [ADR-0054](0054-evidence-based-feature-catalog.md) — evidence-based feature
  catalog and maturity tiers (this ADR adds the `experimental` tier).
- ADR-0058 — capability registry (`ICapabilityRegistry` /
  `CapabilityDescriptor`), the manifest lever (honua-io/honua-release#32).
- [ADR-0057](0057-geoprocessing-capability-boundaries.md) — geoprocessing
  capability boundaries (in-scope GP).
- honua-console `docs/roadmap/FIRST_RELEASE_STRATEGY_AND_CUT_LINE.md` — the
  cut-line and superseded operate floor.
- honua-io/honua-release#32 — first-release capability-registry epic (single
  source of truth for the in/out split).
- honua-io/honua-server#2335 — Track B3, registry-derived manifest + deferred /
  experimental capability disable.
