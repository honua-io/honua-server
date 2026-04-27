# ADR-0034: Mobile SDK Language Strategy

## Status

Accepted

## Context

`AGENTS.md` lists `honua-mobile` as the "MAUI-first mobile SDK and GeoPackage /
offline field-collection foundation," but the repo and SDK package do not yet
exist. A working MAUI reference app at
`honua-sdk-dotnet/examples/FieldDataCollection` demonstrates the full read /
write / edit / sync / offline-cache cycle on iOS and Android, including
GeoPackage / SpatiaLite, `OfflineSyncManager`, OpenRosa / XForms hybrid form
ingestion, and gRPC submission against the canonical server pipelines. The
reference app is shipped, but it is an _example_, not a versioned SDK package.

The roadmap doc at [`docs/developer/mobile-sdk-roadmap.md`](../../developer/mobile-sdk-roadmap.md)
sequences the SDK into six first-tier child tickets. Every one of those
tickets — repo creation, iOS bring-up, Android bring-up, auth, offline storage,
sync — is constrained by the language choice. We need to lock the language(s)
before scoping the rest.

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
licensing infrastructure (ADR-0033). The reference app validates that this
stack reaches iOS and Android in production form. The ADR codifies the
existing MAUI-first intent with explicit reasoning so the child tickets
operate on a settled foundation.

## Decision

Honua ships the mobile SDK as **C# / .NET MAUI**, a single codebase targeting
`net10.0-ios` and `net10.0-android`, published as the NuGet package
`Honua.Sdk.Mobile` parallel to `Honua.Sdk.Grpc` and `Honua.Sdk.OgcFeatures`.

Concrete consequences:

- The mobile SDK shares `Honua.Sdk.Grpc` proto stubs and `Honua.Sdk.OgcFeatures`
  contracts directly. No FFI boundary, no duplicated client code.
- Build matrix pins to .NET 10 LTS, matching the rest of the Honua server and
  SDK ecosystem (`Honua.Sdk.Grpc`, `Honua.Sdk.OgcFeatures`, and the
  `FieldDataCollection` reference app all target `net10.0`). Upgrades are
  evaluated through a CI matrix entry, not a runtime decision.
- Storage uses SQLite-PCL-raw with a bundled SpatiaLite native binary for
  GeoPackage, the same combination already exercised by the
  `FieldDataCollection` reference app.
- Platform-native secure storage (Keychain on iOS, Keystore on Android) is
  reached through the MAUI essentials surface, not a third-party crypto
  library.
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

| Package | Namespace |
|---------|-----------|
| `Honua.Sdk.Mobile` | `Honua.Sdk.Mobile.*` |
| `Honua.Sdk.Mobile.Forms` (optional) | `Honua.Sdk.Mobile.Forms.*` |

`Honua.Sdk.Mobile.Forms` is reserved for the OpenRosa / XForms hybrid surface
already prototyped in the reference app. Splitting it lets non-form consumers
avoid the form parser dependency.

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
  auth lifecycle, sync semantics, and storage logic live in one place.
- **Direct reuse of `honua-sdk-dotnet` contracts.** No FFI, no second proto
  build, no duplicated DTOs.
- **AOT and trim story already proven.** ADR-0018 and the existing server
  publish pipeline cover the constraints; the SDK adopts them rather than
  re-deriving them.
- **Reference app validates the platform.** `FieldDataCollection` already
  exercises iOS, Android, gRPC submission, GeoPackage offline storage,
  OpenRosa form ingestion, and the `OfflineSyncManager` end to end.
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
- `honua-sdk-dotnet/examples/FieldDataCollection/` — MAUI reference app
- `AGENTS.md` § Honua Repository Map (`honua-mobile` row)
- .NET 10 Mobile NativeAOT documentation:
  https://learn.microsoft.com/en-us/dotnet/maui/
- OGC GeoPackage Encoding Standard 1.3
- Kotlin Multiplatform status (decision-date snapshot, 2026-04)
