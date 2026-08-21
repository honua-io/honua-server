# Capability Keys Schema (`capability-keys.v1.json`)

Tracking issue: [#2893](https://github.com/honua-io/honua-server/issues/2893) (child of the canonical capability matrix epic [#2892](https://github.com/honua-io/honua-server/issues/2892)).

`docs/gis/data/capability-keys.v1.json` is the canonical, customer-facing
capability vocabulary for the Honua platform. It is the **one key list every
other repo consumes and never forks** — honua-evidence, honua-site, the SDK
coverage snapshots (honua-sdk-js/-dotnet/-python), honua-migrate's codemod
parity roster, and honua-samples all resolve against this file's `key` values.

## Why two key lists exist

This repo already has an edition-gated entitlement vocabulary,
[`FeatureCatalog`](../../src/Honua.Core/Features/Licensing/Domain/FeatureCatalog.cs)
(`docs generator`: `src/Honua.Core/Features/Licensing/Domain/FeatureCatalog.cs`),
which enumerates only the Pro/Enterprise keys `LicenseGate` enforces.
`capability-keys.v1.json` is generated from a strictly broader C# type,
[`CapabilityKeyCatalog`](../../src/Honua.Core/Features/Licensing/Domain/CapabilityKeyCatalog.cs)
(`CapabilityKeyCatalog.All = CapabilityKeyCatalog.CommunityKeys ++
CapabilityKeyCatalog.DescriptiveKeys ++ FeatureCatalog.All`). Community keys
describe capabilities that ship ungated. Descriptive keys capture minimum
edition and release posture for runtime-gated surfaces, such as the
off-by-default warehouse providers, without adding an entitlement-enforcement
path. Adding either kind of key is a description-layer change only.

Both lists share one naming rule: every key is dot-namespaced lowercase,
`<category>.<name>` (e.g. `editing.feature-edits`, `serve.wms`,
`import.file`), enforced by `CapabilityKeyCatalogTests` in
`Honua.Core.Tests`.

## Document shape

```json
{
  "schemaVersion": "1.1.0",
  "generator": "src/Honua.Core/Features/Licensing/Domain/CapabilityKeyCatalog.cs",
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
    "esriAssess": [ { "assessKey": "feature-service", "capability": "serve.geoservices-featureserver" } ],
    "interop": [ { "clientLane": "js", "protocol": "wms", "capability": "serve.wms" } ],
    "esriCompatMatrix": [ { "serviceId": "feature-server", "capability": "serve.geoservices-featureserver" } ],
    "geobench": [ { "scenario": "wms-getmap", "capability": "serve.wms" } ]
  }
}
```

### `capabilities[]`

| Field | Type | Description |
|---|---|---|
| `key` | string | Canonical, stable identifier. Never renamed once published; deprecate additively. |
| `displayName` | string | Human-readable name for UI/sales-checklist rendering. |
| `category` | string | Grouping label — one of `FeatureCatalog.Categories` (Alerts, Editing, …) or `CapabilityKeyCatalog.Categories` (Serve, Discovery, ControlPlane, Ops, Deploy, DataProviders, Format, Process, Collaboration, Demo, Enrichment). |
| `edition` | string | `Community`, `Pro`, or `Enterprise` — the minimum `HonuaEdition` required. |
| `description` | string | One- or two-sentence description of what the capability does. |
| `status` | string or null | Optional explicit release posture. The warehouse-provider keys use `experimental`; the provider-neutral GeoParquet/GeoArrow format keys use `live`. Absence preserves the existing maturity/evidence projection. |

### `crosswalks`

Four sections join external evidence vocabularies onto this repo's capability
keys, because none of them carry a capability key of their own today:

| Section | Source vocabulary | Row shape |
|---|---|---|
| `esriAssess` | `honua-esri-assess` verdict registry (`src/honua_esri_assess/verdict/registry.py` `CAPABILITY_REGISTRY`, read-only reference — this repo does not modify it) | `{ assessKey, capability, note? }` |
| `interop` | Client-interop certification envelopes (`client_lane` × `protocol`, schema in [`CROSS_CLIENT_CERTIFICATION_EVIDENCE.md`](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md)) | `{ clientLane, protocol, capability }` |
| `esriCompatMatrix` | [`geoservices-rest-parity.json`](data/geoservices-rest-parity.json) `services[].id` | `{ serviceId, capability }` |
| `geobench` | [geobench](https://github.com/honua-io/geobench) benchmark scenario names | `{ scenario, capability }` |

**Hard Esri lock-ins are never silently absent.** The `esriAssess` crosswalk
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

20 of the seed's 25 slugs are a straight kebab-case projection of a
`CapabilityKeyCatalog`/`FeatureCatalog` `category` value already (for example
`StaticMap` → `static-map`, `FieldOps` → `field-ops`,
`DisasterRecovery` → `disaster-recovery`) — no rename was needed here to keep
those stable. The remaining 5 seed slugs (`serve-geoservices`, `serve-ogc`,
`serve-tiles`, `serve-odata`, `serve-stac`) are a coarse, deliberately partial
grouping of what this catalog models as ~24 individual `serve.*` keys under a
single `Serve` category (plus `serve.sensorthings`, `serve.3d-tiles-scene`,
`serve.i3s-scene`, and `serve.elevation`, which the seed does not cover at
all). Deriving a label-sync-compatible slug list from `Serve` therefore needs
a many-to-one grouping decision (which `serve.*` keys fold into
`serve-geoservices` vs. `serve-ogc`, and what slug the uncovered protocols get)
that is out of scope for this issue; it is left for whoever picks up the
seed-retirement swap, tracked by the `TODO(#2893)` comments already in
`label-sync.yml` and the seed file itself.

## GA criteria (#2946)

A capability key may be advertised as GA (implemented, on by default, no experimental
flag) only when all four hold:

1. **Interface-level proving tests exercising the core operation.** At least one
   `[IntegrationTest]` proving test drives the capability's actual operation (not only
   its error/validation paths) through a real protocol surface, and that test runs on
   the PR-or-train CI path (a weekly-only conformance cron does not count as the
   proving evidence for GA).
2. **Matrix-joined evidence where applicable.** Where an official conformance suite or
   parity crosswalk exists for the capability (OGC CITE, `geoservices-rest-parity.json`
   via `esriCompatMatrix`), `capability-matrix.v1.json`'s `cite`/`parity` join for that
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
   release. `FeatureCatalogGenerator.ResolveMaturity` takes the **lower** of the
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
   `CapabilityKeyCatalog.All` key-for-key (name, category, edition,
   description).
4. Every crosswalk row's `capability` resolves to a real key.
5. Every `honua-esri-assess` `hard_lock_in` key appears in
   `capability-no-go-allowlist.v1.json`, not in the `esriAssess` crosswalk.
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

1. Add it to `CapabilityKeyCatalog.CommunityKeys` (or, for an entitlement-gated
   key, to `FeatureCatalog.All`).
2. Add a rule to `capability-route-mapping.v1.json` for every route family it
   should claim, or add it to `capability-no-surface-allowlist.v1.json` with a
   reason if it has no distinct route.
3. Add its row to `capability-keys.v1.json`'s `capabilities` array (and any
   relevant crosswalk row).
4. Regenerate `feature-catalog.json` (`scripts/generate-feature-catalog.sh`)
   and `capability-matrix.v1.json`
   (`python3 scripts/ci/generate-capability-matrix.py`).
5. Run `dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj`
   to confirm the drift gates pass.
