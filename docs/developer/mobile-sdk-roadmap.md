# Honua Mobile SDK Roadmap

This document scopes the `honua-mobile-sdk` deliverable: a MAUI-first cross-platform
mobile SDK that gives field-collection apps a shippable read / write / edit / sync /
offline-cache cycle on top of the canonical Honua server pipelines.

The roadmap is the source of truth for first-tier child tickets that
coordinate ongoing SDK work. The `honua-mobile` GitHub repo already exists at
[honua-io/honua-mobile](https://github.com/honua-io/honua-mobile) with a
Phase-0 baseline in place (see [Why this roadmap exists now](#why-this-roadmap-exists-now)).
Child tickets live in `honua-server` under the `area/sdk` label for cross-repo
tracking — server-side requirement coordination keeps them adjacent to this
roadmap and to the parent `#811`. Implementation lands in the
`honua-io/honua-mobile` repo.

Companion ADR: [ADR-0036 Mobile SDK Language Strategy](../contributor/adr/0036-mobile-sdk-language-strategy.md).

## Why this roadmap exists now

`AGENTS.md` lists `honua-mobile` as a sibling repo to `honua-sdk-dotnet`, and
the repo now exists with a working baseline: `Honua.Mobile.Sdk` (transport,
auth, gRPC-first client with REST fallback), `Honua.Mobile.Field` (dynamic
forms, validation, calculated fields, record workflow), `Honua.Mobile.Offline`
(GeoPackage storage, sync queue, map-area download, conflict resolution), and
`Honua.Mobile.Maui` (DI extensions). Phase 0 in the repo's own terminology —
parity baseline (`docs/phase-0/PARITY_SPEC.md`), gRPC contract freeze
(`proto/honua/v1/`), reference MAUI app shell (`apps/Honua.Mobile.App/`),
offline sync engine, and quality gates with 74 tests across 4 test projects —
is complete. A reference field-collection MAUI application also exists at
`honua-sdk-dotnet/examples/FieldDataCollection`, demonstrating the offline
cycle end to end (auth, GeoPackage local storage, OpenRosa / XForms form
ingestion, `OfflineSyncManager`, and gRPC submission); the new
`Honua.Mobile.Field` and `Honua.Mobile.Offline` packages generalise those
patterns into shippable, versioned NuGet packages.

This pass scopes the server-side view of the next-phase SDK work — the
read / write / edit / sync / offline-cache cycle the server must continue to
support — and enumerates first-tier child tickets that coordinate
implementation in `honua-io/honua-mobile`.

### Naming convention

Three name spaces are used distinctly throughout this roadmap and ADR-0036:

- **GitHub repo**: `honua-mobile`, hosted at
  [honua-io/honua-mobile](https://github.com/honua-io/honua-mobile) (per
  `AGENTS.md` repository map).
- **SDK deliverable**: `honua-mobile-sdk` (the ticket-level product name used
  in `#811` and child tickets).
- **NuGet packages**: the `Honua.Mobile.*` family —
  `Honua.Mobile.Sdk` (transport / gRPC / auth), `Honua.Mobile.Field` (dynamic
  forms), `Honua.Mobile.Offline` (GeoPackage sync), `Honua.Mobile.Maui`
  (MAUI DI extensions), and the future `Honua.Mobile.IoT` (interface stubs).
  These names are independent from the `Honua.Sdk.*` packages in
  `honua-sdk-dotnet` (`Honua.Sdk.Grpc`, `Honua.Sdk.OgcFeatures`); the mobile
  SDK consumes those contracts through their generated client code rather
  than re-publishing them under a `Honua.Sdk.Mobile` umbrella.

## Scope

In scope:

- gRPC read paths against the canonical server `FeatureService` (currently
  `geospatial.v1.FeatureService`; mobile bring-up aligns the proto package
  per [Phase 1 — Read](#phase-1--read)) and OGC API Features read paths
  against the `Honua.Sdk.OgcFeatures` contract (matching the
  `/ogc/features/collections/.../items` REST surface that
  `Honua.Mobile.Sdk` already calls as a gRPC fallback).
- Authenticated writes through the canonical edit/transaction pipeline.
- Local feature edits with conflict-aware re-sync.
- Bidirectional sync with three resolution strategies (user-choice,
  last-write-wins, merge / server-wins).
- Offline cache backed by GeoPackage + SpatiaLite, with TTL eviction and a
  background prefetch scheduler.
- iOS and Android target bring-up, validated by CI smoke tests against a live
  server.
- Telemetry events (sync start, sync complete, conflict count) observable via
  the platform diagnostic listener.

Out of scope:

- Pure-mobile UI components (live in app teams).
- AR / VR overlays (`#359` mobile epic, separate work).
- Native platform UI shells; this SDK ships services and view-model-friendly
  primitives, not pages or views.
- Server-side changes; the SDK only consumes the canonical server pipelines.
- React Native or Flutter bindings (see ADR-0036 alternatives).

## Architecture invariants

- The SDK is a thin client over canonical pipelines. It must not reimplement
  query, edit, metadata, raster, or security logic that already exists on the
  server (`AGENTS.md § Protocol Adapter Architecture`).
- Error mapping must run through the shared problem/error helper pattern. Raw
  gRPC status codes, SQLite exceptions, or filesystem paths must not surface to
  SDK consumers (`AGENTS.md § Cross-Cutting Concerns`).
- AOT and trimming compatibility are required: source-generated JSON and
  logging, no reflection in hot paths, NativeAOT-publishable on iOS via .NET 10
  ILC.
- CRS metadata must be preserved per OGC GeoPackage. Default storage CRS is
  EPSG:4326 (WGS-84) per the spec's built-in SRS records.
- Telemetry counters and `ActivitySource`s mirror the server convention
  (`Honua.Mobile.<area>`).
- Dependency budget: max 5 per service, max 4 per handler, matching the server
  rules in `AGENTS.md`.

## Phases

The roadmap is staged so each phase can ship independently. Each row maps to a
first-tier child ticket. Phases 1 through 4 sequence the read / write / edit /
sync / offline-cache cycle named in the ticket acceptance criteria.

| Phase | Capability | Cycle slice | Child ticket |
|-------|-----------|-------------|--------------|
| 0 | NuGet publish pipeline, MAUI-workload CI matrix, AOT/trim smoke (iOS + Android) | (foundation) | A — repo scaffolding gap closure |
| 1 | Read: feature query via gRPC, layer inspection, CRS-aware geometry | Read | B (iOS bring-up), C (Android bring-up) |
| 2 | Auth and write: token lifecycle, ApplyEdits, OGC API Features CRUD | Write / edit | D — auth module |
| 3 | Offline-first storage: GeoPackage + SpatiaLite, TTL eviction, prefetch | Offline cache | E — offline-first storage |
| 4 | Sync: conflict detection, resolution strategies, durable retry | Sync / edit | F — sync conflict resolution |

### Phase 0 — Scaffolding and bring-up

Most Phase-0 deliverables are already in place in `honua-io/honua-mobile`.
Child ticket A tracks the remaining gaps:

- ✅ GitHub repo with branch protection, Apache 2.0 license, and the
  `Honua.Mobile.Sdk` / `Honua.Mobile.Field` / `Honua.Mobile.Offline` /
  `Honua.Mobile.Maui` projects building on `net10.0` (see the repo
  solution `Honua.Mobile.sln`).
- ✅ Existing test suites: `Honua.Mobile.Sdk.Tests`, `Honua.Mobile.Field.Tests`,
  `Honua.Mobile.Offline.Tests`, plus a separate `Honua.Mobile.Smoke.Tests`
  project (74 tests across 4 projects per the repo README).
- ❓ NuGet publish workflow gated on signed releases — verify and close
  any gaps in child ticket A.
- ❓ CI matrix covering iOS 17+ and Android API 33+ on simulator and
  emulator with the MAUI workload — `apps/Honua.Mobile.App` exercises the
  MAUI surface; remaining work is the MAUI-workload CI runner.
- ❓ Trim-compatibility check (`PublishTrimmed=true; TrimMode=full`) and AOT
  smoke (`PublishAot=true` for iOS) wired into the matrix — confirm in
  Phase 0 and add if absent.

### Phase 1 — Read

The read path proves the platform targets work end to end with a live server.
The `Honua.Mobile.Sdk` package already provides gRPC-first feature query
with REST fallback (`QueryFeaturesAsync`, `QueryFeaturesStreamAsync`); child
tickets B and C extend the matrix to MAUI iOS / Android targets with the
MAUI workload installed and add platform-specific smoke runners.

- **gRPC proto-package alignment is a Phase 1 prerequisite.** The mobile-owned
  `proto/honua/v1/feature_service.proto` (package `honua.v1`,
  `csharp_namespace = "Honua.Server.Features.Grpc.Proto"`) is a parallel copy
  of the canonical public proto at
  `geospatial-grpc/geospatial/v1/feature_service.proto`
  (package `geospatial.v1`, `csharp_namespace = "Geospatial.V1"`). The message
  schemas match field-for-field today, but the gRPC method paths differ —
  the server registers handlers for `geospatial.v1.FeatureService/*`
  (`OperationRegistry.cs`), while `Honua.Mobile.Sdk` (and `Honua.Sdk.Grpc`)
  call `honua.v1.FeatureService/*`. Smoke tests in child tickets B and C
  must close this gap before claiming gRPC interop, by either re-packaging
  the mobile proto to `geospatial.v1` or registering a `honua.v1` alias on
  the server. Until that lands, the mobile gRPC path falls back to the OGC
  API Features REST surface (which is already wired end-to-end).
- The transport client (`Honua.Mobile.Sdk`) consumes generated stubs from
  `proto/honua/v1/feature_service.proto`. No new proto file is added in this
  phase; the alignment work is a package/registration fix on top of the
  existing schema.
- Layer inspection uses the shared metadata catalog already exposed via gRPC.
- CRS-aware geometry conversion uses the existing geodesy helpers in
  `Honua.Core` (server) and equivalent client-side helpers; do not duplicate
  the projection math in mobile code.
- Smoke test asserts the SDK can query a public layer from a live Honua
  server in under one second on simulator and emulator. The smoke runner
  also asserts that the gRPC method path resolves end-to-end, gating on the
  package alignment above.

### Phase 2 — Auth and write

- The `Honua.Mobile.Sdk` package already exposes `HonuaMobileClientOptions`
  with `ApiKey` and bearer-token modes (per the repo README quick-start).
  Phase 2 lifts the token-lifecycle abstraction (refresh, secure persistence)
  out of the client options and into a first-class `IAuthTokenProvider` that
  matches the bearer-token convention already used in
  `FieldDataCollection/Services/HonuaMobileClient.cs` (`CreateAuthHeaders`)
  and aligns with the admin-side `HonuaAdminAuthHandler` token semantics.
  No new authorization model.
- Platform-native secure storage: Keychain on iOS, Keystore on Android, via
  the MAUI essentials surface in `Honua.Mobile.Maui`. The SDK does not roll
  its own crypto; it adapts to the platform APIs.
- API-key and bearer-token modes are supported. OAuth device flow is deferred
  to a follow-on once the server-side OAuth surface stabilises.
- Write paths exercise the canonical edit/transaction pipeline through the
  existing gRPC and OGC API Features CRUD endpoints (the same
  `/ogc/features/collections/.../items` surface the SDK already uses for
  REST fallback).

### Phase 3 — Offline-first storage

- `Honua.Mobile.Offline` already ships GeoPackage-backed offline storage
  (`GeoPackageSyncStoreOptions`, `AddHonuaGeoPackageOfflineSync`) plus
  map-area download with path-traversal protection. Phase 3 hardens the
  TTL-based eviction policy and a background prefetch scheduler (cooperatively
  cancellable with platform lifecycle) so the cache stays bounded.
- Backend: GeoPackage + SQLite, already exercised on iOS and Android.
  SpatiaLite is not currently bundled with `Honua.Mobile.Offline`; if the
  spatial-index work in this phase requires it, child ticket E documents the
  bundling plan (SQLite-PCL-raw with SpatiaLite native binary).
- Spatial indexes target sub-second feature queries for layers up to
  100,000 features.
- CRS metadata is preserved per OGC GeoPackage; default EPSG:4326 (WGS-84) per
  the spec's built-in SRS records.

### Phase 4 — Sync

- `Honua.Mobile.Offline` already ships an `OfflineSyncEngineOptions` with
  three `SyncConflictStrategy` values (`ClientWins`, `ServerWins`,
  `ManualReview`) and queue-based sync with claim/lease semantics
  (per the repo README). Phase 4 graduates this engine to long-term
  durability guarantees and rounds out telemetry.
- Three conflict resolution strategies, all in v1: `ManualReview`
  (corresponds to user-choice default), `ClientWins` (last-write-wins from
  the mobile side), `ServerWins` (canonical merge).
- Durable retry survives process restart by persisting pending operations to
  a local state table; the queue is opened on app start and drained when the
  connectivity service reports online.
- Telemetry: `ActivitySource` `Honua.Mobile.Sync`, counters
  `mobile_sync_runs_total{result}`, `mobile_sync_conflicts_total{strategy}`,
  `mobile_pending_operations`. Counter shapes mirror the server conventions
  in `AGENTS.md § Cross-Cutting Concerns`.
- Errors map through the shared problem helper. Raw gRPC `StatusCode` or
  `SqliteException` text never reaches SDK consumers.

## Compatibility and versioning

- For admin/control-plane usage, the SDK follows the
  [SDK Compatibility Matrix](SDK_COMPATIBILITY_MATRIX.md), which governs the
  JS/TS, Python, and .NET admin clients only. A mobile SDK release that
  consumes the admin surface stays on the same admin API major as the
  matched server release.
- Feature contracts are not covered by that matrix. The gRPC stubs follow
  proto backward-compatibility conventions: additive changes only within a
  major, breaking changes ride a new proto package version. Today the
  canonical feature proto is `geospatial.v1` (under
  `geospatial-grpc/geospatial/v1/`) while both `Honua.Sdk.Grpc`
  and the mobile-owned `proto/honua/v1/` consume a parallel `honua.v1`
  package; the message schemas match but the package paths diverge, and
  closing that gap is scoped into Phase 1 (see the proto-alignment
  prerequisite above). The OGC API Features REST surface
  (`Honua.Sdk.OgcFeatures`, used as the gRPC fallback) follows the OGC API
  Features specification's stable resource model and OpenAPI compatibility —
  additive query parameters and response fields only within a major.
  Concrete version-by-version tracking is scoped into child ticket F (sync)
  when the SDK surface stabilises.
- Mobile SDK semver follows the .NET SDK family. Backwards-incompatible
  mobile changes follow the migration-guide policy in
  [SDK Migration Guide Template](sdk-migration-template.md).

## Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|-----------|
| .NET MAUI iOS AOT publish regressions on a future runtime upgrade | Medium | High (CI breaks on iOS target) | Pin to .NET 10 LTS in Phase 0; upgrade path tracked in Phase 1. |
| GeoPackage SpatiaLite extension availability on Android emulator images | Medium | Medium (offline tests fail in CI) | Use SQLite-PCL-raw with bundled SpatiaLite when spatial-index work in Phase 3 requires it; documented in child ticket E. |
| KMP ecosystem catching up before implementation starts | Low | Low (decision can be revisited) | ADR-0036 records explicit re-evaluation triggers. |
| Reference app patterns not extractable as clean SDK surfaces | Medium | Medium (scope creep on Phases 3 and 4) | Each phase scopes the interface-first extraction explicitly. |
| Cross-repo coordination friction (server-tracked tickets vs `honua-mobile` implementation) | Low | Low | `area/sdk` label and roadmap cross-links keep tickets traceable; migrate to `honua-mobile` if friction surfaces. |

## Child tickets

Six first-tier sub-tickets are filed in `honua-server` (issues #826–#831) for
cross-repo coordination so they remain adjacent to the parent `#811` and to
this roadmap. Implementation lands in `honua-io/honua-mobile`.

| ID | Issue | Title | Phase |
|----|-------|-------|-------|
| A | [#826](https://github.com/honua-io/honua-server/issues/826) | `honua-mobile-sdk: repo scaffolding gap closure (NuGet publish, MAUI-workload CI, AOT/trim smoke)` | 0 |
| B | [#827](https://github.com/honua-io/honua-server/issues/827) | `honua-mobile-sdk: iOS MAUI target bring-up` | 1 |
| C | [#828](https://github.com/honua-io/honua-server/issues/828) | `honua-mobile-sdk: Android MAUI target bring-up` | 1 |
| D | [#829](https://github.com/honua-io/honua-server/issues/829) | `honua-mobile-sdk: auth module` | 2 |
| E | [#830](https://github.com/honua-io/honua-server/issues/830) | `honua-mobile-sdk: offline-first storage layer` | 3 |
| F | [#831](https://github.com/honua-io/honua-server/issues/831) | `honua-mobile-sdk: sync conflict resolution policy` | 4 |

Tickets stay in `honua-server` so they remain attached to the parent `#811`
and to this server-side roadmap. Each child ticket links into the
corresponding implementation work in `honua-io/honua-mobile` (PRs, milestones,
or sub-issues there). When a child ticket completes, close it in
`honua-server` with a back-link to the merged PR in `honua-mobile`.

## Out-of-scope follow-ons

These are explicitly deferred and tracked separately:

- AR / VR utility visualization (`#359` mobile epic).
- React Native and Flutter bindings (rejected in ADR-0036; revisit triggers
  documented there).
- OAuth device flow (gated on server-side OAuth surface stabilising).
- Mobile-side spatial editing UI primitives (app-team responsibility).
- Per-tenant mobile entitlement gating (server-side licensing already covers
  this; mobile-side gates are not in v1).

## References

- ADR-0036: Mobile SDK Language Strategy
- ADR-0024: Open-Core Edition Model (license tier coverage)
- ADR-0018: Source-Generated JSON Serialization for AOT Compatibility
- `AGENTS.md`: Honua repository map, protocol adapter architecture,
  cross-cutting concerns
- Canonical feature proto:
  [`geospatial/v1/feature_service.proto`](https://github.com/honua-io/geospatial-grpc/blob/main/geospatial/v1/feature_service.proto)
  (package `geospatial.v1`); generated .NET bindings are consumed through
  `Geospatial.Grpc`, and registered server handlers are enumerated in
  `src/Honua.Server/OperationRegistry.cs`.
- [`honua-io/honua-mobile`](https://github.com/honua-io/honua-mobile): the
  mobile SDK repo itself — `Honua.Mobile.Sdk` / `Honua.Mobile.Field` /
  `Honua.Mobile.Offline` / `Honua.Mobile.Maui` packages, mobile-owned proto
  copy at `proto/honua/v1/feature_service.proto` (package `honua.v1`,
  pending Phase-1 alignment with the canonical `geospatial.v1`), reference
  app at `apps/Honua.Mobile.App/`, and the Phase-0 baseline docs at
  `docs/phase-0/` (PARITY_SPEC, INNOVATION_SPEC, PHASE_0_SUMMARY,
  TEST_STRATEGY).
- `honua-sdk-dotnet/examples/FieldDataCollection/`: legacy reference MAUI app
  illustrating the full read / write / edit / sync / offline-cache cycle
  before the patterns were lifted into `Honua.Mobile.Field` and
  `Honua.Mobile.Offline`.
- [SDK Compatibility Matrix](SDK_COMPATIBILITY_MATRIX.md)
- [SDK Migration Guide Template](sdk-migration-template.md)
- OGC GeoPackage Encoding Standard 1.3 (built-in EPSG:4326 / WGS-84 SRS records)
