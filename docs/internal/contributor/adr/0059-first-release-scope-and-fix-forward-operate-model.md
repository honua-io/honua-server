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
  the lever that decides what is advertised and reachable **for its route-bearing
  capabilities**, but does not itself enumerate the first-release in/out split
  (and is not the only gate — see §2 for the entitlement/UI mechanism that holds
  the rest of the disabled set off).
- honua-console `docs/roadmap/FIRST_RELEASE_STRATEGY_AND_CUT_LINE.md` draws the
  depth cut-line and, critically, asserts a **"non-negotiable operate floor"**
  whose exit criterion is *"propose → preflight → approve → apply → **roll
  back**, without ever touching Git"* and *"failure auto-rolls-back."*

That operate floor cannot be honestly certified for the first release.
**Automatic rollback was never certified as an operate floor.** A rollback
path *is* built (approve → apply → rollback), but it is approval-gated and
disabled by default; there is no certified, health-gated *automatic* revert
that runs unattended as part of a release gate (per the release-readiness audits
under `_release-audit/` and the safe-rollout audit notes). Asserting an
*auto*-rollback floor we cannot prove contradicts the evidence-based posture of
ADR-0054 ("implemented with no test is structurally impossible").
Cross-environment promotion (the dev→staging→prod fleet path,
honua-io/honua-devops#58) is likewise built but not part of the
single-environment release and stays gated off.

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
- **Data formats** — GeoParquet, GeoArrow, GeoBuf, and FileGDB read/serve
  support are **supported (in-scope)** for the first release. They were never
  gated (ungated = served), so they are neither registry-flag nor
  entitlement/UI gated — they simply ship. Not experimental.
- **AI operator surface with fix-forward** — the operate model below.

### 2. Experimental + disabled set (built server-side, gated off)

The following are **built server-side and gated OFF for the first release**.
They carry `maturity: experimental` in the feature catalog, are **NOT advertised
in the capability manifest**, and their surfaces/endpoints are **disabled by
default** — reachable only as a **customer opt-in** (via the registry capability
flag for the route-bearing capabilities, or via edition/entitlement + Console-UI
for the rest — see the two-mechanism note below).
Present, wired, and tested in the binary, they still must not appear in the
manifest, `/mcp`, Studio availability, or Console in a default deployment. This
is a "built, gated off, customer-opt-in" posture — **not** "deferred / not
built." Each links its tracking issue:

- **Mobile** (honua-mobile) — mobile / offline SDK + field-collection foundation.
- **Forms + field collection** — `form.package` authoring and field data
  collection (server form-package endpoints + `IFieldCollectionSyncStore` /
  the `sync.offline` capability).
- **Temporal / data-versioning** — honua-io/honua-server#1166.
- **Versioned editing** — honua-io/honua-server#371.
- **Disconnected-sync conflict review** — honua-io/honua-server#1167.
- **Realtime / geofence alerting** — honua-io/honua-server#1169.
- **SIEM / investigations** — honua-io/honua-server#1168 /
  honua-io/honua-devops#59.
- **Native MAUI host + mTLS** — honua-io/honua-server#1171 (caveat: client
  certificate trust is currently in-memory).
- **Exhaustive GP/ETL node breadth + custom script/model tools** —
  honua-io/honua-server#1185 (96 built-in nodes present).
- **Cross-environment deploy / promotion** (dev→staging→prod fleet) —
  honua-io/honua-devops#58.
- **Rollback** — the approve → apply → **rollback** operate loop
  (honua-io/honua-server#133). Built, but approval-gated and flag-off; the
  shipped safety model is fix-forward (see §4). It is **not** an unattended
  auto-rollback.
- **SSO / OIDC / SAML / SCIM** — honua-io/honua-server#348 (built; issue closed).
- **Collaborative map sessions** — honua-io/honua-server#971 (session transport
  built).

These are certified-off, not absent: the code is present, wired, and tested, but
it is not certified for this release and is not reachable by a default
deployment. It lights up when the customer opts in.

> **Update (#2427).** **Geofence alerting** (`alerts.geofence`) has been promoted
> out of this experimental + disabled set to **GA (`Implemented`)** — the first
> `Experimental → Implemented` promotion. The alerts engine now ships as shared,
> un-gated infrastructure: geofence/dwell/attribute-threshold triggers,
> multi-channel delivery, and a second consumer (ops deploy/job-event
> notifications, per ADR-0060). Its admin routes (`/api/v1/admin/alerts/*`)
> therefore carry `maturity: implemented` in the feature catalog and are
> advertised in the capability manifest. It remains OFF by default operationally
> — the pipeline self-gates on `Alerts:Enabled` (default `false`) — but that is a
> runtime enablement switch, not experimental gating. The
> "Realtime / geofence alerting" bullet and the geofence entries in the
> two-mechanism roster below are superseded for the alerting half by this note;
> realtime feature-streaming stays experimental.

> **Update (#2429).** **Temporal analytics** (`temporal.filtering`,
> `temporal.extent-discovery`, `temporal.histogram`, `temporal.time-series-tiles`)
> has been promoted out of this experimental + disabled set to **GA
> (`Implemented`)**. Time filtering, extent discovery, date-bin histograms,
> time-series tiles, and the animation-API contract now ship on the default
> surface: the `/api/v1/temporal/*` routes carry `maturity: implemented` and the
> capabilities are advertised in the manifest. The **Community/Pro edition split
> is unchanged** (filtering + extent discovery are Community; histogram,
> time-series tiles, and animation are Pro), enforced by the existing entitlement
> gates — GA does not bypass licensing. Providers that cannot translate a temporal
> predicate now fail loud (`NotSupportedException`) rather than silently returning
> unfiltered rows (hardened in #2429). The "Temporal / data-versioning" bullet
> above and the temporal entry in the two-mechanism roster below are superseded by
> this note.

> **Update (#2431).** **mTLS client-certificate validation** (`security.mtls`) has
> been promoted out of this experimental + disabled set to **GA (`Implemented`)** —
> after hardening chain
> validation and CRL/OCSP revocation (fail-closed on indeterminate revocation
> status; a distinct revoked-vs-untrusted-chain outcome). Its admin routes
> (`/api/v1/admin/security/client-certificates/*`) therefore carry
> `maturity: implemented` in the feature catalog and are advertised in the
> capability manifest, gated by the Enterprise entitlement
> `identity.mtls-client-certificate`. It remains OFF by default operationally —
> enforcement self-gates on `Authentication:ClientCertificates:Mode` (default
> `Disabled`) — but that is a runtime enablement switch, not experimental gating.
> The mTLS entry in the two-mechanism roster below is superseded by this note.

> **Update (#2958).** The #2431 GA promotion above is superseded: **mTLS
> client-certificate validation** (`security.mtls`) is DEMOTED back to **experimental**
> (release-safety follow-up) — the always-on client-certificate scheme/RBAC layer could
> 403 a fully valid bearer-token admin request (#2945). It is `maturity: experimental`
> again, gated behind `Capabilities:Experimental:security.mtls:Enabled`.

**Two mechanisms hold this set OFF — not one uniform registry flag.** The
route-bearing experimental capabilities — **temporal** analytics/versioning
(`/api/v1/temporal/*`), **disconnected-sync / replicas**, **realtime
feature-streams**, **geofence alerting** (`/api/v1/admin/alerts/*`), and **mTLS
client-certificate validation** — are gated by the **registry capability flag**:
flipping them off in the registry drops them from the manifest, `/mcp`, Studio,
and Console at once (the single-lever behavior ADR-0058 gives the registry, over
exactly the descriptors whose routes are in the catalog's `experimental` tier).
The remainder — **SSO/OIDC/SAML/SCIM**, **forms + field data collection**,
**mobile / offline**, **branch / versioned editing**, **SIEM / investigations**,
**cross-environment promotion**, the **rollback** operate loop, and
**collaborative map sessions** — are held OFF by **edition/entitlement checks and
Console-UI availability**, not by the registry manifest flag. Folding more of the
entitlement/UI-gated set behind the registry flag is post-first-release registry
work (see ADR-0058, "Two mechanisms hold the experimental + disabled set OFF").

**Genuinely not built (`planned`).** The only items in this space that are *not*
built are the federal smart-card auth paths — **CAC / PIV**
(honua-io/honua-server#1275 / #1273) — which are `planned`. **FIPS**
(honua-io/honua-server#1275) is **attestation-only, with no enforcement**, so it
is `planned` / caveated, not `experimental`.

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

### 4. Operate model: health-gated fix-forward, not auto-rollback

The first release's **shipped safety model is health-gated AI fix-forward**
(roll-forward convergence) via the AI operator surface: when a change degrades
health, the AI operator proposes and applies a *forward* corrective change that
converges the system back to a healthy state, rather than reverting to a prior
state. A rollback path **is built**, but it ships **approval-gated and disabled
by default**; it is not the shipped operate floor and is **not** an unattended
*auto*-rollback.

This **replaces** the cut-line's
"propose → preflight → approve → apply → **rollback**" operate floor and its
"failure auto-rolls-back" exit criterion.

**Rationale — automatic rollback was never certified.** No health-gated,
unattended *auto*-rollback path is tested or run in CI or exercised by any
release gate (per the release-readiness / safe-rollout audits); the built
rollback loop is approval-gated and off by default. Shipping an auto-rollback
floor we cannot prove would violate the evidence-based posture of ADR-0054.
Fix-forward is the safety model we can actually certify for a single
environment, and it is already the convergence model the merge-train / operator
surface uses elsewhere ("roll-forward-first convergence"). The built rollback
loop and cross-environment promotion therefore sit in the experimental +
disabled set (§2 — built + gated off) and can light up post-release when they
are tested and gated in.

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
