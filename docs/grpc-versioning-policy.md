# gRPC Versioning Policy

Status: stable
Last reviewed: 2026-05-20
Owner: Honua Server platform

This document defines how Honua versions its gRPC public surface. It applies to
the `Geospatial.Grpc` protocol package (`Geospatial.V1` namespace, consumed via
`PackageReference Include="Geospatial.Grpc"` in
[`src/Honua.Server/Honua.Server.csproj`](../src/Honua.Server/Honua.Server.csproj))
and to every service hosted under
[`src/Honua.Server/Features/Protocols/Grpc/`](../src/Honua.Server/Features/Protocols/Grpc/).

The gRPC surface is one of the proof-classed public surfaces tracked in the
[public-interface proof ledger](gis/data/public-interface-proof.json) — see
[`public-interface-quality-model.md`](contributor/public-interface-quality-model.md)
for how each surface's contract is enforced. This policy is the rule set that
keeps the `Geospatial.V1` row in that ledger from silently breaking clients.

## Namespace versioning convention

- Every protobuf package is suffixed with a major version, lowercase: `geospatial.v1`,
  future `geospatial.v2`, etc. The generated .NET namespace follows: `Geospatial.V1`,
  `Geospatial.V2`.
- A service lives under exactly one major-version namespace. `FeatureService` in
  `geospatial.v1` is a distinct service from `FeatureService` in `geospatial.v2`
  — they share a name but are routed independently and registered as separate
  service descriptors.
- File layout mirrors the namespace: `proto/geospatial/v1/feature_service.proto`,
  `proto/geospatial/v2/feature_service.proto`. Do not commingle versions in a
  single file.
- Server-side, the `using Proto = Geospatial.V1;` alias pattern (see
  [`HonuaFeatureService.cs`](../src/Honua.Server/Features/Protocols/Grpc/HonuaFeatureService.cs))
  is the supported way to consume protos. Each new major version gets its own
  alias and its own service implementation type.

## Backwards-compatibility rules (within a major version)

Within `v1` we promise wire and source compatibility. Inside a single major
version the following rules are mandatory and enforced by code review plus the
contract-governance gates listed in the proof ledger:

1. **Never remove a field.** A field number, once shipped in a stable release,
   is permanent. Removing a field would let a new sender reuse the tag for a
   different type and silently corrupt existing receivers.
2. **Never renumber a field.** The wire format keys on tag numbers, not names.
   Renaming a field in the `.proto` is acceptable (and gets a CHANGELOG note);
   renumbering is a breaking change.
3. **Never change a field's wire type.** `int32` to `int64`, `string` to
   `bytes`, `enum` to `int32`, `repeated` to non-repeated — all forbidden in a
   stable version. Wire-compatible changes that look identical on the wire
   (e.g. `int32` ↔ `uint32` ↔ `sint32` for non-negative values) are still
   discouraged because they change client codegen.
4. **Never change `oneof` membership.** Removing a member, moving a field in or
   out of a `oneof`, or merging two `oneof`s is a breaking change.
5. **Only add new fields with new tag numbers.** New optional fields, new
   enum values, new messages, and new RPC methods are additive and safe.
   Reserve a contiguous block of tag numbers per message to make this clean.
6. **Mark retiring fields with `[deprecated = true]`** for at least one minor
   release before introducing a `v2` replacement. Generated .NET code surfaces
   this as `[Obsolete]` so downstream consumers see the warning at compile
   time. The field stays on the wire; only the source-level annotation
   changes.
7. **Default values stay the same.** Changing a field's implicit default
   (e.g. switching a proto2 default, or changing the "zero value" semantics of
   a proto3 field) is a breaking change.
8. **Reserved tags and names.** When a field is fully retired (after the
   deprecation window and after the field is removed from new code in `v2`),
   add `reserved <tag>;` and `reserved "<name>";` so the tag can never be
   reused inside `v1`.
9. **Streaming direction is fixed.** A unary RPC cannot become server-streaming
   in the same major version, and vice versa. Add a new RPC instead.
10. **Status codes and error details are part of the contract.** Do not change
    which `grpc.Status` code a method returns for an established failure mode
    within a major version; new failure modes can use new codes.

CI enforcement: any PR that touches a `.proto` in the `Geospatial.Grpc` package
must pass the package's protobuf-breaking-change linter (`buf breaking` or
equivalent) against the most recent `Geospatial.Grpc` release tag. The Honua
Server repo additionally pins a specific `Geospatial.Grpc` version, so consumer
incompatibilities surface as build failures in
[`ci.yml`](../.github/workflows/ci.yml).

## Breaking-change process: introducing V2 alongside V1

When a change cannot be expressed under the rules above (e.g. a method needs to
return a fundamentally different message shape), introduce a new major version
rather than mutating `v1`:

1. **Open an interface ADR.** Capture the motivation, the migration plan, and
   the deprecation timeline in `docs/contributor/decisions/` (or the active
   ADR location) and link it from the public-interface proof ledger row.
2. **Add `v2` in parallel.** Create `proto/geospatial/v2/` with the new
   messages, services, and RPCs. `v1` and `v2` coexist in the published
   `Geospatial.Grpc` package; nothing in `v1` is modified or removed.
3. **Host both on the server.** Register the `v2` service implementation
   alongside the `v1` one in
   [`GrpcServiceCollectionExtensions`](../src/Honua.Server/Features/Protocols/Grpc/GrpcServiceCollectionExtensions.cs).
   Both versions share the same backend domain code; the per-version surface
   is a thin adapter.
4. **Announce deprecation of `v1`.** Mark `v1` as deprecated in the release
   notes the moment `v2` ships. Annotate the `v1` service and messages with
   `option deprecated = true;` so generated clients warn.
5. **Hold the deprecation window.** `v1` must remain wire-compatible and
   functional for at least two minor releases after `v2` ships, and longer if
   any contracted client (see the
   [tool lanes](contributor/public-interface-quality-model.md#tool-lanes))
   still depends on it. The window length is documented in the ADR.
6. **Remove `v1` only after the window closes.** Removal is itself a release
   note item and triggers a major version bump of the `Geospatial.Grpc`
   package.

## References

- [`docs/contributor/public-interface-quality-model.md`](contributor/public-interface-quality-model.md)
  — proof-class definitions; the gRPC surface uses `contract-governance` and
  `route-coverage` proofs.
- [`docs/gis/data/public-interface-proof.json`](gis/data/public-interface-proof.json)
  — machine-readable surface ledger; the `Geospatial.V1` rows live here.
- [`src/Honua.Server/Features/Protocols/Grpc/`](../src/Honua.Server/Features/Protocols/Grpc/)
  — server-side service implementations and the `Geospatial.V1` alias pattern.
- [gRPC API Evolution / Versioning best practices](https://grpc.io/docs/what-is-grpc/core-concepts/#protobuf-versions-and-language-versions)
  — upstream gRPC guidance.
- [Protobuf "Updating a Message Type"](https://protobuf.dev/programming-guides/proto3/#updating)
  — canonical list of wire-safe and wire-breaking changes; this policy is a
  strict superset.
- [Buf "Breaking Changes" rules](https://buf.build/docs/breaking/rules) — the
  reference rule set used by the protobuf-breaking-change linter.
