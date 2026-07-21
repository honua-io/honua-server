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
(`CapabilityKeyCatalog.All = CapabilityKeyCatalog.CommunityKeys ++ FeatureCatalog.All`),
which adds the Community-tier capabilities (serve/query surfaces per protocol
family, file import, discovery, control-plane, …) that ship ungated and
therefore have no entry in `FeatureCatalog`. Adding a Community capability key
**never** touches `LicenseGate` or any entitlement-enforcement code path — it is
a pure description-layer addition.

Both lists share one naming rule: every key is dot-namespaced lowercase,
`<category>.<name>` (e.g. `editing.feature-edits`, `serve.wms`,
`import.file`), enforced by `CapabilityKeyCatalogTests` in
`Honua.Core.Tests`.

## Document shape

```json
{
  "schemaVersion": "1.0.0",
  "generator": "src/Honua.Core/Features/Licensing/Domain/CapabilityKeyCatalog.cs",
  "trackingIssue": "#2893",
  "description": "...",
  "capabilities": [
    {
      "key": "serve.wms",
      "displayName": "WMS 1.3",
      "category": "Serve",
      "edition": "Community",
      "description": "Serve map images through WMS 1.3 (GetMap, GetFeatureInfo, GetCapabilities)."
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
| `category` | string | Grouping label — one of `FeatureCatalog.Categories` (Alerts, Editing, …) or `CapabilityKeyCatalog.Categories` (Serve, Discovery, ControlPlane, Ops, Process, Collaboration, Demo, Enrichment). |
| `edition` | string | `Community`, `Pro`, or `Enterprise` — the minimum `HonuaEdition` required. |
| `description` | string | One- or two-sentence description of what the capability does. |

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

## Regenerating

`capability-keys.v1.json`, `capability-route-mapping.v1.json`, and
`capability-no-surface-allowlist.v1.json` are hand-maintained JSON files kept
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
