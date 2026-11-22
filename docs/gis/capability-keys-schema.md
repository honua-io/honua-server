# Capability Keys Schema (`capability-keys.v1.json`)

Tracking issue: [#2893](https://github.com/honua-io/honua-server/issues/2893) (child of the canonical capability matrix epic [#2892](https://github.com/honua-io/honua-server/issues/2892)).

`docs/gis/data/capability-keys.v1.json` is the canonical, customer-facing
capability vocabulary for the Honua platform. It is the **one key list every
other repo consumes and never forks** — honua-evidence, honua-site, the SDK
coverage snapshots (honua-sdk-js/-dotnet/-python), honua-migrate's codemod
parity roster, and honua-samples all resolve against this file's `key` values.

## Why two key lists exist

This repo already has an edition-gated entitlement vocabulary,
[`FeatureCatalog`](../../src/Honua.Core/Core/Features/Licensing/Domain/FeatureCatalog.cs)
(`docs generator`: `src/Honua.Core/Core/Features/Licensing/Domain/FeatureCatalog.cs`),
which enumerates the entitlement keys `LicenseGate` enforces (mostly Pro and
Enterprise, plus a few Community entries).
`capability-keys.v1.json` is generated from a strictly broader C# type,
[`CapabilityKeyCatalog`](../../src/Honua.Core/Core/Features/Licensing/Domain/CapabilityKeyCatalog.cs)
(`CapabilityKeyCatalog.All = CommunityKeys ++ RoutedExperimentalKeys ++
RoutedPreviewKeys ++ RoutedDependentKeys ++ DescriptiveKeys ++ FeatureCatalog.All`,
with any `FeatureCatalog` key that `RoutedPreviewKeys` redefines taken from
`RoutedPreviewKeys`). Community keys describe capabilities that ship ungated.
`RoutedExperimentalKeys` and `RoutedPreviewKeys` hold routed keys above Community
edition that are not in `FeatureCatalog`. `RoutedDependentKeys` have no
entitlement of their own: every route they name is gated by the keys in their
`Requires` list. Descriptive keys capture minimum edition and release posture
for runtime-gated surfaces, such as the off-by-default warehouse providers,
without a route or an entitlement-enforcement path.

A key's `status` can come from any of these lists: it is set inline on the key
(for example the Community `serve.wmts`, `serve.ogc-api-edr` and
`format.geoarrow`, the descriptive warehouse providers, and `admin.multi-tenancy`
in `RoutedPreviewKeys`, which is `internal`), or, for a `FeatureCatalog` key, it
is derived from its category (Streaming is `experimental`; Alerts and Channels
are `preview`). Adding a key or a status is a description-layer change; a routed
key also needs its routes mapped (see [Regenerating](#regenerating)).

Every list shares one naming rule: every key is dot-namespaced lowercase,
`<category>.<name>` (e.g. `editing.featureserver-edits`, `serve.wms`,
`import.file`). `CapabilityKeyCatalogTests` in `Honua.Core.Tests` enforces it
for `CommunityKeys`; every key in `capability-keys.v1.json` follows it today.

## Document shape

```json
{
  "schemaVersion": "1.1.0",
  "generator": "src/Honua.Core/Core/Features/Licensing/Domain/CapabilityKeyCatalog.cs",
  "trackingIssue": "#2893",
  "description": "...",
  "capabilities": [
    {
      "key": "provider.redshift",
      "displayName": "Amazon Redshift Provider",
      "category": "DataProviders",
      "edition": "Enterprise",
      "status": "experimental",
      "description": "Experimental, off-by-default Amazon Redshift feature provider."
    }
  ],
  "crosswalks": {
    "geoservicesAssess": [ { "assessKey": "feature-service", "capability": "serve.geoservices-featureserver" } ],
    "interop": [ { "clientLane": "js", "protocol": "wms", "capability": "serve.wms" } ],
    "geoservicesCompatMatrix": [ { "serviceId": "feature-server", "capability": "serve.geoservices-featureserver" } ],
    "geobench": [ { "scenario": "wms-getmap", "capability": "serve.wms" } ]
  }
}
```

### `capabilities[]`

| Field | Type | Description |
|---|---|---|
| `key` | string | Canonical, stable identifier. Never renamed once published; deprecate additively. |
| `displayName` | string | Human-readable name for UI/sales-checklist rendering. |
| `category` | string | Grouping label — one of `FeatureCatalog.Categories` (Alerts, Editing, …) or `CapabilityKeyCatalog.Categories` (Serve, Discovery, ControlPlane, Internal, Ops, DataProviders, Format, Process, Collaboration, Demo, Enrichment, Jobs). |
| `edition` | string | `Community`, `Pro`, or `Enterprise` — the minimum `HonuaEdition` required. |
| `description` | string | One- or two-sentence description of what the capability does. |
| `status` | string or null | Optional explicit release posture. `preview`: real but not covered by the release compatibility promise (the customer-alert trigger and delivery-channel keys, `fieldops.offline-sync`, `process.geoprocessing`, `process.ogc-api-processes`, `serve.ogc-api-coverages`, `serve.ogc-api-edr`, `serve.wmts`, `serve.wps`). `experimental`: the warehouse providers, `serve.i3s-scene`, `collaboration.map-sessions`, `streaming.feature-subscriptions`. `internal`: Honua's own hosted operation, never offered to a licensee (`admin.multi-tenancy`). `live`: the provider-neutral GeoArrow format key. Absence preserves the existing maturity/evidence projection. |

### `crosswalks`

Four sections join external evidence vocabularies onto this repo's capability
keys, because none of them carry a capability key of their own today:

| Section | Source vocabulary | Row shape |
|---|---|---|
| `geoservicesAssess` | `honua-esri-assess` verdict registry (`src/honua_esri_assess/verdict/registry.py` `CAPABILITY_REGISTRY`, read-only reference — this repo does not modify it) | `{ assessKey, capability, note? }` |
| `interop` | Client-interop certification envelopes (`client_lane` × `protocol`, schema in [`CROSS_CLIENT_CERTIFICATION_EVIDENCE.md`](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md)) | `{ clientLane, protocol, capability }` |
| `geoservicesCompatMatrix` | [`geoservices-rest-parity.json`](data/geoservices-rest-parity.json) `services[].id` | `{ serviceId, capability }` |
| `geobench` | [geobench](https://github.com/honua-io/geobench) benchmark scenario names | `{ scenario, capability }` |

**Hard Esri lock-ins are never silently absent.** The `geoservicesAssess` crosswalk
intentionally omits the registry's `no-go` (`hard_lock_in`) tier keys
(`utility-network`, `parcel-fabric`, `lrs`) — they instead appear in the
separate [`capability-no-go-allowlist.v1.json`](data/capability-no-go-allowlist.v1.json)
with an explicit `not-supported` marker and boundary text, so "we don't support
this" is a reviewed, loud statement rather than a silent gap a drift check
could miss.

## Companion artifacts

- [`capability-route-mapping.v1.json`](data/capability-route-mapping.v1.json) —
  the ordered `family`/`routePrefix`/`routeContains`/`method` → `capability`
  rules `FeatureCatalogGenerator` uses to stamp the `capability` field on every
  [`feature-catalog.json`](data/feature-catalog.json) entry (via
  `CapabilityRouteMapper`).
- [`capability-no-surface-allowlist.v1.json`](data/capability-no-surface-allowlist.v1.json) —
  every canonical capability key with zero `feature-catalog.json` entries
  (a config flag, a background job, or an SDK/MCP-only surface) and the
  reviewed reason why, so "no route" is an explicit statement rather than a
  silent gap.
- [`capability-no-go-allowlist.v1.json`](data/capability-no-go-allowlist.v1.json) —
  the explicit not-supported markers described above.
- [`capability-maturity-overrides.v1.json`](data/capability-maturity-overrides.v1.json) —
  the reviewed, demotion-only maturity table described under
  [Demoting a key that is shipped but not GA](#demoting-a-key-that-is-shipped-but-not-ga).
- [`capability-matrix.v1.json`](data/capability-matrix.v1.json) — the Phase-A
  aggregation of all of the above plus proving-test counts, CITE pass rates,
  and parity status; see [`capability-matrix-schema.md`](capability-matrix-schema.md).

## Relationship to the `cap/*` issue-label seed

`.github/workflows/label-sync.yml` (#2896) currently drives the `cap/<category>`
issue-label namespace from a hand-committed stopgap,
[`capability-categories.seed.json`](data/capability-categories.seed.json), and
says explicitly it will retire in favor of pointing `seed_path` at
`capability-keys.v1.json` once this issue lands — with the constraint that
every `slug` value must stay stable across that swap so existing `cap/*`
labels, open issues, and the issue-form capability field are never relabeled.

The seed has 28 slugs, and every one is a projection of a `category` value:
20 (source `feature-catalog`) come from `FeatureCatalog.Categories` and 8 (source
`capability-keys`: `collaboration`, `control-plane`, `demo`, `discovery`,
`enrichment`, `ops`, `process`, `serve`) from `CapabilityKeyCatalog.Categories`.
27 are a straight kebab-case projection (for example `StaticMap` → `static-map`,
`FieldOps` → `field-ops`, `DisasterRecovery` → `disaster-recovery`); `ai` maps to
`AI`. All 26 `serve.*` keys share the single `serve` slug. The `Internal`, `Jobs`,
`DataProviders` and `Format` categories have no slug. Swapping `seed_path` to
`capability-keys.v1.json` therefore needs no grouping decision, only a projection
that keeps these slugs; it is still tracked by the `TODO(#2893)` comments in
`label-sync.yml` and the seed file.

## GA criteria (#2946)

A capability key may be advertised as GA (implemented, on by default, no experimental
flag) only when all four hold:

1. **Interface-level proving tests exercising the core operation.** At least one
   `[IntegrationTest]` proving test drives the capability's actual operation (not only
   its error/validation paths) through a real protocol surface, and that test runs on
   the PR Gate or trunk CI path (a weekly-only conformance cron does not count as the
   proving evidence for GA).
2. **Matrix-joined evidence where applicable.** Where an official conformance suite or
   parity crosswalk exists for the capability (OGC CITE, `geoservices-rest-parity.json`
   via `geoservicesCompatMatrix`), `capability-matrix.v1.json`'s `cite`/`parity` join for that
   key is non-empty and passing.
3. **A factually correct `noSurface` allowlist reason.** If the capability has zero
   `feature-catalog.json` entries, its
   [`capability-no-surface-allowlist.v1.json`](data/capability-no-surface-allowlist.v1.json)
   row must describe a real, verifiable mechanism (an actual config flag, background
   job, or SDK/MCP-only call path) — not an aspirational or historically-stale claim.
4. **Live-provable where a route exists.** When the capability has a routed HTTP/gRPC
   surface, that route must be reachable and correctly gated (auth, entitlement,
   capability-experimental-flag) on a deployed candidate, not only inside a test host.

A key that fails (1) is a **demotion candidate** — evaluate whether the gap is closed
by test-depth work already in flight before demoting (see the July 2026 re-grade
below for the specific policy applied). A key that fails (3) gets its allowlist reason
corrected, not a maturity change. A key that fails (2) or (4) gets a tracked follow-up;
CITE/parity/live-provability gaps are evidence work, not necessarily a reason to hide
an otherwise-real, tested capability.

### Demoting a key that is shipped but not GA

There are exactly two levers, and they answer different questions:

1. **`CapabilityRegistry` + `CapabilityGateResolver`** — the *runtime* lever. A
   `CapabilityDescriptor` marked `Experimental` is 404'd off the default surface (opt in
   via `Capabilities:Experimental:<id>:Enabled`) and the feature catalog projects it as
   `experimental`. Use this when the capability should actually be held off the served
   surface. It only reaches the ~30 curated `/mcp` + `honua.capability_manifest.v1` ids,
   and adding a row to that roster changes the manifest wire.
2. **[`capability-maturity-overrides.v1.json`](data/capability-maturity-overrides.v1.json)**
   — the *claim* lever, for a key outside that roster whose routes should keep serving
   exactly as they do today while the capability stops being advertised as GA for this
   release. `CapabilityMaturityOverrides.ResolveEffective`, applied to the result of
   `FeatureCatalogGenerator.ResolveMaturity`, takes the **lower** of the
   registry-resolved tier and the override tier, so a row can only demote, never promote.
   Every row carries a reason code, a verifiable reason, and the decision it implements;
   `CapabilityKeyDriftTests` fails the build on a row that promotes, matches no catalog
   entry, or is not reflected in the committed catalog.

This second lever exists because
[`capability-ga-regrade-2026-07.md`](capability-ga-regrade-2026-07.md) had to record its
`scene.catalog` demotion as documentation-only: the decision was made, and nothing in the
generated evidence could express it. A documented demotion that the capability matrix
still reports as `implemented` is invisible to
[honua-release's `tools/check_ga_surface.py`](https://github.com/honua-io/honua-release/blob/trunk/tools/check_ga_surface.py),
which reads that matrix and treats every non-`noSurface` key with `maturity.implemented > 0`
as advertised GA.

**Do not** reach for `capability-no-surface-allowlist.v1.json` to demote a key: `noSurface`
means "has no distinct catalogued route of its own", and
`NoSurfaceAllowlist_DoesNotListCapabilitiesThatActuallyHaveEntries` fails the build for a
key that does. Using it to dodge an evidence floor is precisely the dishonesty the GA-surface
gate exists to catch.

See
[`capability-ga-regrade-2026-07.md`](capability-ga-regrade-2026-07.md) for the full
per-key re-grade decision record produced by applying this bar (#2946).

## Drift gates

`Honua.Architecture.Tests` (`CapabilityKeyDriftTests`,
`CapabilityCrosswalkDriftTests`) enforce, as hard build failures:

1. Every `feature-catalog.json` entry's `capability` resolves to exactly one
   key in `CapabilityKeyCatalog.All`.
2. Every canonical capability key has ≥1 `feature-catalog.json` entry **or**
   an entry in `capability-no-surface-allowlist.v1.json`.
3. `capability-keys.v1.json`'s `capabilities` list matches
   `CapabilityKeyCatalog.All` key-for-key (displayName, category, edition,
   description, status).
4. Every crosswalk row's `capability` resolves to a real key.
5. Every `honua-esri-assess` `hard_lock_in` key appears in
   `capability-no-go-allowlist.v1.json`, not in the `geoservicesAssess` crosswalk.
6. Every `capability-maturity-overrides.v1.json` row names a real key, demotes
   (never promotes), carries a reason code / reason / decision, matches at least
   one `feature-catalog.json` entry, and is actually reflected in every entry of
   that capability in the committed catalog.

## Regenerating

`capability-keys.v1.json`, `capability-route-mapping.v1.json`,
`capability-no-surface-allowlist.v1.json`, and
`capability-maturity-overrides.v1.json` are hand-maintained JSON files kept
in lockstep with `CapabilityKeyCatalog.cs` by the drift tests above (unlike
`feature-catalog.json`, they are not emitted by a `[Fact]`). When you add a
capability key:

1. Add it to `CapabilityKeyCatalog.CommunityKeys` (with `Status:` if it is not
   GA); for an entitlement-gated key, to `FeatureCatalog.All` (Alerts, Channels
   and Streaming entries take their status from the category); for a Pro or
   Enterprise routed key outside `FeatureCatalog`, to `RoutedExperimentalKeys` or
   `RoutedPreviewKeys` with its `status`; for a key gated only by other
   entitlements, to `RoutedDependentKeys`; for a description-only key with no
   route, to `DescriptiveKeys`.
2. Add a rule to `capability-route-mapping.v1.json` for every route family it
   should claim, or add it to `capability-no-surface-allowlist.v1.json` with a
   reason if it has no distinct route.
3. Add its row to `capability-keys.v1.json`'s `capabilities` array (and any
   relevant crosswalk row).
4. Regenerate `feature-catalog.json` (`scripts/ci/generate-feature-catalog.sh`)
   and `capability-matrix.v1.json`
   (`python3 scripts/ci/generate-capability-matrix.py`).
5. Run `dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj`
   to confirm the drift gates pass.
