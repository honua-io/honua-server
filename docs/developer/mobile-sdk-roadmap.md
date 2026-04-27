# Honua Mobile SDK Roadmap

This document scopes the `honua-mobile-sdk` deliverable: a MAUI-first cross-platform
mobile SDK that gives field-collection apps a shippable read / write / edit / sync /
offline-cache cycle on top of the canonical Honua server pipelines.

The roadmap is the source of truth for first-tier child tickets while the
`honua-mobile` GitHub repo does not yet exist. Sub-tickets live in
`honua-server` under the `area/sdk` label and migrate into the new repo once it
is created (see [Phase 0 — Scaffolding](#phase-0--scaffolding-and-bring-up)).

Companion ADR: [ADR-0034 Mobile SDK Language Strategy](../contributor/adr/0034-mobile-sdk-language-strategy.md).

## Why this roadmap exists now

`AGENTS.md` lists `honua-mobile` as a sibling repo to `honua-sdk-dotnet`, but the
repo and roadmap have not been created. A working MAUI reference application
already exists at `honua-sdk-dotnet/examples/FieldDataCollection`, demonstrating
the offline cycle end to end (auth, GeoPackage local storage, OpenRosa / XForms
form ingestion, `OfflineSyncManager`, and gRPC submission). The SDK pulls those
patterns out of the example and into a shippable, versioned NuGet package.

This pass scopes the SDK only. Implementation lands in the child tickets below.

### Naming convention

Three names are used distinctly throughout this roadmap and ADR-0034:

- **GitHub repo**: `honua-mobile` (per `AGENTS.md` repository map). All
  `honua-io/honua-mobile` URLs below resolve to this repo once it is created.
- **SDK deliverable**: `honua-mobile-sdk` (the ticket-level product name used
  in `#811` and child tickets).
- **NuGet package**: `Honua.Sdk.Mobile` (parallel to `Honua.Sdk.Grpc` and
  `Honua.Sdk.OgcFeatures`; see ADR-0034).

## Scope

In scope:

- gRPC + OData read paths against the canonical `Honua.Sdk.Grpc` and
  `Honua.Sdk.OgcFeatures` contracts.
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
- React Native or Flutter bindings (see ADR-0034 alternatives).

## Architecture invariants

- The SDK is a thin client over canonical pipelines. It must not reimplement
  query, edit, metadata, raster, or security logic that already exists on the
  server (`AGENTS.md § Protocol Adapter Architecture`).
- Error mapping must run through the shared problem/error helper pattern. Raw
  gRPC status codes, SQLite exceptions, or filesystem paths must not surface to
  SDK consumers (`AGENTS.md § Cross-Cutting Concerns`).
- AOT and trimming compatibility are required: source-generated JSON and
  logging, no reflection in hot paths, NativeAOT-publishable on iOS via .NET 8
  ILC.
- CRS metadata must be preserved per OGC GeoPackage. Default storage CRS is
  EPSG:4326; CRS104 (WGS84-2D) is supported per spec.
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
| 0 | Repo, CI, NuGet publish pipeline, platform targets (iOS + Android) | (foundation) | A — repo creation and CI scaffolding |
| 1 | Read: feature query via gRPC, layer inspection, CRS-aware geometry | Read | B (iOS bring-up), C (Android bring-up) |
| 2 | Auth and write: token lifecycle, ApplyEdits, OData CRUD | Write / edit | D — auth module |
| 3 | Offline-first storage: GeoPackage + SpatiaLite, TTL eviction, prefetch | Offline cache | E — offline-first storage |
| 4 | Sync: conflict detection, resolution strategies, durable retry | Sync / edit | F — sync conflict resolution |

### Phase 0 — Scaffolding and bring-up

Repository creation is the gate. Until `honua-io/honua-mobile` exists,
sub-tickets are filed under `honua-server` and labelled with `area/sdk`. Each
sub-ticket body links back to this roadmap and includes the migration note
("move to honua-mobile once created"). Scaffolding deliverables:

- GitHub repo with branch protection, dependabot, and Trivy scanning aligned
  with the rest of the org.
- NuGet publish workflow gated on signed releases.
- CI matrix covering iOS 17+ and Android API 33+ on simulator and emulator.
- Trim-compatibility check (`PublishTrimmed=true; TrimMode=full`) and AOT
  smoke (`PublishAot=true` for iOS) wired into the matrix.
- Apache 2.0 license header (per project licensing strategy: client SDKs are
  Apache 2.0).

### Phase 1 — Read

The read path proves the platform targets work end to end with a live server.
Both child tickets B and C land identical smoke tests and CI runners; the
divergence is platform tooling.

- `IFeatureClient` wraps `Honua.Sdk.Grpc` query stubs. No new proto.
- Layer inspection uses the shared metadata catalog already exposed via gRPC.
- CRS-aware geometry conversion uses the existing geodesy helpers in
  `Honua.Core` rather than a mobile-local copy.
- Smoke test asserts the SDK can query a public layer from a live Honua
  server in under one second on simulator and emulator.

### Phase 2 — Auth and write

- `IAuthTokenProvider` follows the bearer-token convention established in
  `FieldDataCollection/Services/HonuaMobileClient.cs` (`CreateAuthHeaders`).
  No shared gRPC auth handler exists in `honua-sdk-dotnet` today; the mobile
  SDK defines the interface fresh and aligns with the admin-side
  `HonuaAdminAuthHandler` token semantics. No new authorization model.
- Platform-native secure storage: Keychain on iOS, Keystore on Android. The
  SDK does not roll its own crypto; it adapts to the platform APIs.
- API-key and bearer-token modes are supported. OAuth device flow is deferred
  to a follow-on once the server-side OAuth surface stabilises.
- Write paths exercise the canonical edit/transaction pipeline through the
  existing gRPC and OData CRUD endpoints.

### Phase 3 — Offline-first storage

- `ILocalStorageService` is extracted from
  `honua-sdk-dotnet/examples/FieldDataCollection/Services/GeoPackageLocalStorageService.cs`
  into a first-class SDK service, behind an interface.
- Backend: GeoPackage + SpatiaLite via SQLite-PCL-raw, already proven on iOS
  and Android in the reference app. SpatiaLite ships as a bundled native
  binary; documented in child ticket E.
- TTL-based eviction and a background prefetch scheduler (cooperatively
  cancellable with platform lifecycle) keep the cache bounded.
- Spatial indexes target sub-second feature queries for layers up to
  100,000 features.
- CRS metadata is preserved per OGC GeoPackage; default EPSG:4326, CRS104 for
  WGS84-2D.

### Phase 4 — Sync

- `OfflineSyncManager` is extracted from the reference app into a first-class
  SDK service. The reference app continues to consume it through DI.
- Three conflict resolution strategies, all in v1: user-choice (default),
  last-write-wins, merge / server-wins.
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
- Feature contracts (gRPC `Honua.Sdk.Grpc` stubs and OData `Honua.Sdk.OgcFeatures`)
  are not covered by that matrix. Their compatibility follows proto
  backward-compatibility conventions (additive changes only within a major;
  breaking changes ride a new proto package version). Concrete tracking is
  scoped into child ticket F (sync) when the SDK surface stabilises.
- Mobile SDK semver follows the .NET SDK family. Backwards-incompatible
  mobile changes follow the migration-guide policy in
  [SDK Migration Guide Baseline](SDK_MIGRATION_GUIDE_BASELINE.md).

## Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|-----------|
| .NET MAUI iOS AOT publish regressions on a future runtime upgrade | Medium | High (CI breaks on iOS target) | Pin to .NET 8 LTS in Phase 0; upgrade path tracked in Phase 1. |
| GeoPackage SpatiaLite extension availability on Android emulator images | Medium | Medium (offline tests fail in CI) | Use SQLite-PCL-raw with bundled SpatiaLite; documented in Phase 3. |
| KMP ecosystem catching up before implementation starts | Low | Low (decision can be revisited) | ADR-0034 records explicit re-evaluation triggers. |
| Reference app patterns not extractable as clean SDK surfaces | Medium | Medium (scope creep on Phases 3 and 4) | Each phase scopes the interface-first extraction explicitly. |
| Sub-tickets filed in wrong repo become orphaned before move | Low | Low | `area/sdk` label and roadmap cross-links make migration traceable. |

## Child tickets

Six first-tier sub-tickets are filed in `honua-server` and link back to this
roadmap. Each migrates into `honua-io/honua-mobile` once the repo exists.

| ID | Issue | Title | Phase |
|----|-------|-------|-------|
| A | [#826](https://github.com/honua-io/honua-server/issues/826) | `honua-mobile-sdk: repo creation and CI scaffolding` | 0 |
| B | [#827](https://github.com/honua-io/honua-server/issues/827) | `honua-mobile-sdk: iOS MAUI target bring-up` | 1 |
| C | [#828](https://github.com/honua-io/honua-server/issues/828) | `honua-mobile-sdk: Android MAUI target bring-up` | 1 |
| D | [#829](https://github.com/honua-io/honua-server/issues/829) | `honua-mobile-sdk: auth module` | 2 |
| E | [#830](https://github.com/honua-io/honua-server/issues/830) | `honua-mobile-sdk: offline-first storage layer` | 3 |
| F | [#831](https://github.com/honua-io/honua-server/issues/831) | `honua-mobile-sdk: sync conflict resolution policy` | 4 |

Repo creation (ticket #826) is the migration gate: once
`honua-io/honua-mobile` exists, child tickets B–F migrate into the new
repo and the parent (#811) is updated with the new issue numbers.

## Out-of-scope follow-ons

These are explicitly deferred and tracked separately:

- AR / VR utility visualization (`#359` mobile epic).
- React Native and Flutter bindings (rejected in ADR-0034; revisit triggers
  documented there).
- OAuth device flow (gated on server-side OAuth surface stabilising).
- Mobile-side spatial editing UI primitives (app-team responsibility).
- Per-tenant mobile entitlement gating (server-side licensing already covers
  this; mobile-side gates are not in v1).

## References

- ADR-0034: Mobile SDK Language Strategy
- ADR-0024: Open-Core Edition Model (license tier coverage)
- ADR-0018: Source-Generated JSON Serialization for AOT Compatibility
- `AGENTS.md`: Honua repository map, protocol adapter architecture,
  cross-cutting concerns
- `honua-sdk-dotnet/examples/FieldDataCollection/`: reference MAUI app
  implementing the full read / write / edit / sync / offline-cache cycle
- [SDK Compatibility Matrix](SDK_COMPATIBILITY_MATRIX.md)
- [SDK Migration Guide Baseline](SDK_MIGRATION_GUIDE_BASELINE.md)
- OGC GeoPackage Encoding Standard 1.3 (CRS104 / WGS84-2D)
