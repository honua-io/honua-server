# ADR-0036: Mobile SDK Language Strategy

## Status

Accepted

## Context

`AGENTS.md` lists `honua-mobile` as the "MAUI-first mobile SDK and GeoPackage /
offline field-collection foundation." The repo now exists at
[honua-io/honua-mobile](https://github.com/honua-io/honua-mobile) with a
Phase-0 baseline already on .NET MAUI — `Honua.Mobile.Sdk`,
`Honua.Mobile.Field`, `Honua.Mobile.Offline`, and `Honua.Mobile.Maui` packages
build on `net10.0`, with 74 tests across 4 test projects and a working MAUI
reference app at `apps/Honua.Mobile.App/`. A separate legacy reference app at
`honua-sdk-dotnet/examples/FieldDataCollection` first demonstrated the full
read / write / edit / sync / offline-cache cycle on iOS and Android (GeoPackage
/ SpatiaLite, `OfflineSyncManager`, OpenRosa / XForms hybrid form ingestion,
gRPC submission against the canonical server pipelines); those patterns have
since been lifted into the `Honua.Mobile.Field` and `Honua.Mobile.Offline`
packages.

The roadmap doc at [`docs/developer/mobile-sdk-roadmap.md`](../../developer/mobile-sdk-roadmap.md)
sequences the next-phase SDK work into six first-tier child tickets. Every one
of those tickets — Phase-0 scaffolding gap closure, iOS bring-up, Android
bring-up, auth, offline storage, sync — is constrained by the language
choice. This ADR codifies the C# / .NET MAUI direction already in production
in `honua-mobile` so the child tickets operate on a settled, written-down
foundation rather than an inferred one.

The candidates considered:

1. **C# / .NET MAUI** — single codebase, `net10.0-ios` + `net10.0-android` target
   frameworks. Shares the existing `Honua.Sdk.Grpc` and `Honua.Sdk.OgcFeatures`
   stubs without an FFI boundary.
2. **Swift / Kotlin native** — separate iOS and Android SDKs, hand-rolled
   protocol clients per platform (or generated from the gRPC and OpenAPI
   contracts).
3. **Kotlin Multiplatform (KMP)** — shared business logic in Kotlin, iOS UI
   layer in Swift / Compose Multiplatform.
4. **React Native / Flutter** — JavaScript or Dart bridge with native
   platform UI shells.

Honua's server-side infrastructure is already deeply invested in .NET 10: AOT
compatibility (ADR-0018), source-generated JSON and logging, the .NET-first
SDK ecosystem (`honua-sdk-dotnet` is the canonical client SDK), and the
licensing infrastructure (ADR-0033). Both the legacy `FieldDataCollection`
example and the in-repo `apps/Honua.Mobile.App` validate that this stack
reaches iOS and Android in production form.

## Decision

Honua ships the mobile SDK as **C# / .NET MAUI**, a single codebase targeting
`net10.0` library projects with MAUI workload-specific TFMs on the platform
heads, published as the `Honua.Mobile.*` NuGet family —
`Honua.Mobile.Sdk` (transport, gRPC client with REST fallback, auth),
`Honua.Mobile.Field` (forms, validation, calculated fields, record
workflow), `Honua.Mobile.Offline` (GeoPackage storage, sync queue, map-area
download, conflict resolution), and `Honua.Mobile.Maui` (DI extensions for
the MAUI host) — independent from the `Honua.Sdk.*` packages in
`honua-sdk-dotnet`.

Concrete consequences:

- The mobile SDK consumes server contracts through generated gRPC stubs from
  the mobile-owned `proto/honua/v1/` definitions in `honua-mobile`. The
  message schemas mirror the canonical server proto field-for-field, but
  the package paths currently diverge: the server hosts
  `geospatial.v1.FeatureService` (defined in
  `src/Honua.Core/Transport/Proto/geospatial/v1/feature_service.proto` and
  registered by `OperationRegistry.cs`), while both `Honua.Mobile.Sdk` and
  `Honua.Sdk.Grpc` generate clients against the parallel `honua.v1` package.
  Closing the package gap (either re-packaging the mobile/SDK proto under
  `geospatial.v1` or registering a `honua.v1` alias on the server) is a
  Phase-1 prerequisite tracked in
  [`docs/developer/mobile-sdk-roadmap.md`](../../developer/mobile-sdk-roadmap.md#phase-1--read).
  There is no FFI boundary and no hand-written DTOs duplicated between
  repos; the mobile proto packaging is otherwise independent from
  `Honua.Sdk.Grpc` (which is tuned for desktop/server consumers).
- Build matrix pins to .NET 10 LTS, matching the rest of the Honua server and
  SDK ecosystem (`Honua.Sdk.Grpc`, `Honua.Sdk.OgcFeatures`, the
  `FieldDataCollection` reference app, and the `Honua.Mobile.*` library
  projects all target `net10.0`). Upgrades are evaluated through a CI matrix
  entry, not a runtime decision.
- Storage uses SQLite-PCL-raw via `Honua.Mobile.Offline`'s GeoPackage layer,
  the same combination already exercised in production by the existing
  `Honua.Mobile.Offline.Tests` suite. SpatiaLite is bundled only when the
  spatial-index work in Phase 3 of the roadmap requires it.
- Platform-native secure storage (Keychain on iOS, Keystore on Android) is
  reached through the MAUI essentials surface in `Honua.Mobile.Maui`, not a
  third-party crypto library.
- Hiring and community contribution targets the .NET mobile segment. Sample
  apps and tutorials assume C#.

### AOT and trimming

iOS publish requires NativeAOT / ILC; .NET 10 supports this for MAUI iOS apps,
continuing the NativeAOT publish path that .NET 8 first enabled. The SDK keeps
to the same constraints as the rest of the codebase:

- Source-generated JSON contexts (per ADR-0018). No
  `JsonSerializer.Serialize(object)` reflection paths.
- `[LoggerMessage]` source-generated logging.
- No `System.Reflection.Emit`, no `dynamic`, no `Activator.CreateInstance`.
- `PublishTrimmed=true; TrimMode=full` and `PublishAot=true` for iOS verified
  in CI on every PR.

Android does not require AOT, but the same trim mode applies. Android uses
.NET 10's r2r / interpreter mix; the SDK is trim-clean either way.

### Package and namespace

| Package | Namespace | Purpose |
|---------|-----------|---------|
| `Honua.Mobile.Sdk` | `Honua.Mobile.Sdk.*` | Transport, gRPC-first client with REST fallback, auth |
| `Honua.Mobile.Field` | `Honua.Mobile.Field.*` | Dynamic forms, validation, calculated fields, record workflow |
| `Honua.Mobile.Offline` | `Honua.Mobile.Offline.*` | GeoPackage storage, sync queue, map-area download, conflict resolution |
| `Honua.Mobile.Maui` | `Honua.Mobile.Maui.*` | MAUI service registration and DI extensions |
| `Honua.Mobile.IoT` (future) | `Honua.Mobile.IoT.*` | Sensor abstractions (interface stubs only at decision date) |

The split keeps form parsing, GeoPackage storage, and MAUI DI off the
critical path for clients that only need transport. Each package targets
`net10.0`; `Honua.Mobile.Maui` adds the MAUI workload TFMs on the platform
heads. The `Honua.Mobile.*` family is independent from the `Honua.Sdk.*`
packages in `honua-sdk-dotnet` — the mobile SDK has its own gRPC client
packaging tuned for mobile constraints (no `Honua.Sdk.Mobile` umbrella).

### Re-evaluation triggers

This decision can be revisited if any of the following land:

- Kotlin Multiplatform reaches Compose Multiplatform stable on iOS **and** a
  mature GeoPackage / SpatiaLite story (Squirreled SQLite-PCL equivalent or
  better) ships.
- .NET MAUI iOS NativeAOT publish regresses for two consecutive .NET LTS
  releases.
- A strategic enterprise customer requires a non-.NET mobile binding for
  procurement or compliance reasons.

The ADR is otherwise stable. The mobile SDK is C# / .NET MAUI.

## Alternatives Considered

### Swift / Kotlin native

Rejected. Doubles the maintenance surface — every gRPC contract, every
authentication change, every conflict-resolution tweak needs two
implementations. Adds a third language to the SDK ecosystem (alongside
JavaScript and .NET) without a commensurate gain in coverage; the existing
.NET SDK already covers desktop and server scenarios, so the only payoff
would be platform-native UI affinity, which app teams handle in their own
shells anyway. The reference app proves MAUI is sufficient for the field-
collection scenario on both platforms.

### Kotlin Multiplatform (KMP)

Rejected at decision date (2026-04). KMP's Compose Multiplatform UI on iOS
remains pre-stable; the SQLite ecosystem on KMP lacks the GeoPackage and
SpatiaLite tooling that `SQLite-PCL-raw` already provides on .NET. Choosing
KMP would force us to either ship a hybrid (KMP business logic + Swift UI)
or to wait for the ecosystem to mature, and either path defers the SDK
beyond the timeline that the field-collection scenario can absorb. The
re-evaluation triggers above keep this option open.

### React Native / Flutter

Rejected. JavaScript / Dart bridges add latency on the gRPC hot path,
neither has a mature gRPC-Web-on-mobile native story, and both create a
fourth language in the SDK ecosystem (after JS, .NET, and Python). Flutter
in particular requires its own GeoPackage tooling that does not exist in
production-ready form. Neither choice aligns with the existing
`honua-sdk-dotnet` client surface.

## Consequences

### Positive

- **Single codebase across iOS and Android.** Conflict-resolution policy,
  auth lifecycle, sync semantics, and storage logic live in one place across
  the `Honua.Mobile.*` package family.
- **Shared contract surface across `honua-sdk-dotnet` and `honua-mobile`.**
  The mobile SDK has its own gRPC client packaging in `Honua.Mobile.Sdk`
  tuned for mobile, but its message schemas are field-for-field aligned
  with the canonical server proto (`geospatial.v1` under
  `src/Honua.Core/Transport/Proto/geospatial/v1/`); no hand-written DTOs
  or FFI shims. The current proto-package divergence (`honua.v1` mobile
  copy vs `geospatial.v1` server canonical) is acknowledged as a Phase-1
  bring-up prerequisite in the roadmap, not as a structural fork.
- **AOT and trim story already proven.** ADR-0018 and the existing server
  publish pipeline cover the constraints; the SDK adopts them rather than
  re-deriving them.
- **Production baseline already validates the platform.** `Honua.Mobile.Sdk`,
  `Honua.Mobile.Field`, and `Honua.Mobile.Offline` ship with 74 tests across
  4 test projects and a working reference app at `apps/Honua.Mobile.App/`,
  exercising gRPC transport, GeoPackage offline storage, sync queue, and
  conflict resolution end to end.
- **Bounded learning curve for contributors.** Anyone fluent in
  `honua-sdk-dotnet` can contribute to the mobile SDK without learning a
  new toolchain.

### Negative

- **MAUI iOS NativeAOT remains a sharp edge.** New .NET versions occasionally
  regress iOS publish; CI must keep an iOS publish smoke on the matrix.
- **Hiring pool is narrower than Swift / Kotlin native.** Mitigated by the
  project's existing .NET community and the SDK's small surface area.
- **No platform-native UI primitives.** App teams that need the most
  Apple- or Material-pure look-and-feel must build their own UI shell on top
  of the SDK. This is consistent with the SDK / app split documented in the
  roadmap.
- **One bad runtime upgrade can block iOS releases.** Mitigated by the
  .NET 10 LTS pin and the explicit upgrade-evaluation step in the CI matrix.

### Supersedes

None. ADR-0034 codifies the existing AGENTS.md "MAUI-first" direction; no
prior ADR proposed an alternative.

## References

- ADR-0018: Source-Generated JSON Serialization for AOT Compatibility
- ADR-0024: Open-Core Edition Model (mobile editions follow server tier
  gating)
- ADR-0033: Unified License Format and Entitlement Architecture
- [Honua Mobile SDK Roadmap](../../developer/mobile-sdk-roadmap.md)
- Canonical server proto: `src/Honua.Core/Transport/Proto/geospatial/v1/feature_service.proto`
  (package `geospatial.v1`); registered handlers in
  `src/Honua.Server/OperationRegistry.cs`. The mobile copy at
  `honua-mobile/proto/honua/v1/feature_service.proto` mirrors the message
  schemas under the parallel `honua.v1` package; alignment is a Phase-1
  prerequisite per the roadmap.
- [`honua-io/honua-mobile`](https://github.com/honua-io/honua-mobile) — the mobile
  SDK repo with the `Honua.Mobile.*` package family and Phase-0 baseline
- `honua-sdk-dotnet/examples/FieldDataCollection/` — legacy MAUI reference
  app illustrating the patterns lifted into `Honua.Mobile.Field` and
  `Honua.Mobile.Offline`
- `AGENTS.md` § Honua Repository Map (`honua-mobile` row)
- .NET 10 Mobile NativeAOT documentation:
  https://learn.microsoft.com/en-us/dotnet/maui/
- OGC GeoPackage Encoding Standard 1.3
- Kotlin Multiplatform status (decision-date snapshot, 2026-04)
