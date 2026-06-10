# ADR-0042: Per-Protocol Test Project Split (Modularization Phase 2)

## Status

Proposed

## Context

`tests/dotnet/Honua.Server.Tests/` is the largest .NET test assembly in the
repository at ~653 source files. Every CI shard in `.github/ci-shards.json`
compiles the whole assembly even when its FQN filter only executes a slice
of the tests (Architecture stays on a separate, smaller project; the rest
lives in `Server.Tests`). The modularization plan
(memory: `modularization-plan`) and the structural audit
(memory: `structural-audit-2026-05`, A2) both identify this as the real CI
wall-clock contributor — compile time is amortized across every shard, so
shrinking each shard's compile unit is the meaningful win.

ADR-0041 landed the Phase 0 contract surface (`Honua.Core.Abstractions`)
and the Phase 1 plug-in host (`IHonuaProtocolModule`) on the same branch.
This ADR records the Phase 2 plan that follows once a protocol's assembly
is actually extracted.

## Decision

For each protocol that ships as its own assembly
(`Honua.Protocols.OData` / `Honua.Protocols.OgcApi` /
`Honua.Protocols.OgcClassic` / `Honua.Protocols.GeoServices`), create a
sibling test project:

```
tests/dotnet/Honua.Protocols.<X>.Tests/Honua.Protocols.<X>.Tests.csproj
```

referencing `Honua.Protocols.<X>` + `Honua.TestKit` (and `Honua.Core` /
`Honua.Postgres` if the moved tests need real-DB fixtures).

Move:

1. `tests/dotnet/Honua.Server.Tests/Features/Protocols/<X>/**` → the new test
   project. Namespaces preserved via `git mv`.
2. Shared helpers that **only** one protocol uses → into that protocol's
   test project.
3. Truly shared helpers (e.g. `WebAppFixture` base) → stay in
   `Honua.TestKit`, but split per audit-A2: thin `WebAppFixtureBase` + per-
   protocol mixins. The 262/647 dependency fan-in must shrink. A2's
   per-protocol mixin pattern lets each test project carry only the
   fixture surface it needs, instead of every shard recompiling the
   `WebAppFixture` god-class.

`Honua.Server.Tests` keeps only:

- Architecture / arch-test files that need the composed `Honua.Server`
  surface.
- True cross-cutting integration (auth, observability, infrastructure-wide).
- Smoke tests touching the composed app.

`.github/ci-shards.json` is the single source of truth for shard routing.
Splitting `Server.Tests` means:

1. Adding a new shard record per extracted protocol whose `paths` cover the
   new project's source tree.
2. Removing those FQN patterns from the old `Server.Tests` monolith
   shard's `filter`.
3. The protocol's test project gets its own CI matrix entry under
   `server-tests` (the matrix is dynamically built from `ci-shards.json` per
   ADR-0037; no separate workflow edit needed).

### Sequencing

One test-project extraction per PR — mirrors the Phase 1 cadence (one
protocol per PR). The Phase 2 PR for protocol `<X>` branches off the
merged Phase 1 PR for `<X>`. Bundling the two would over-stuff the review
unit; bundling Phase 2 across multiple protocols would conflict on the
same `.github/ci-shards.json` file and require re-rebases.

### Done

For each protocol:

1. New test csproj exists, references `Honua.Protocols.<X>` + `Honua.TestKit`.
2. Old `tests/dotnet/Honua.Server.Tests/Features/Protocols/<X>/` is empty.
3. `.github/ci-shards.json` routes the protocol's tests to the new
   project's shard; old shard's filter no longer matches the moved FQNs.
4. CI shows the new shard exists + passes; old shard's compile time
   drops noticeably (record before/after in the PR body).
5. `WebAppFixture` dependency count (262 today) is measurably lower.

## Consequences

### Positive

- Real CI compile-time reduction. Per-shard `dotnet build` cost shrinks to
  the size of the per-protocol test project instead of the 653-file
  monolith.
- Test failures map cleanly to a single assembly, which simplifies the
  per-shard timing/log artifacts the runner emits.
- The `WebAppFixture` audit-A2 split happens incrementally as protocols
  extract instead of as one cross-cutting fixture rewrite.

### Negative

- `.github/ci-shards.json` churns once per Phase 2 PR. Mitigation: the
  shard record schema is stable, so each PR's `ci-shards.json` diff is
  bounded.
- The shared base of `WebAppFixture` cannot land in one PR if the per-
  protocol mixins need to be co-extracted — A2 mixin design may need its
  own follow-up PR before any Phase 2 split. Recorded as a sequencing risk.

### Neutral

- Existing arch tests (`ProtocolModuleCoverageTests`, `CrossProtocolIsolationTests`)
  keep working as written — they reflect over loaded assemblies, so they
  pick up the protocol-test-project assembly automatically once it lands.

## References

- ADR-0037: Unified CI Test Tier Strategy (the lane model this builds on).
- ADR-0041: Honua.Core.Abstractions Extraction (Phase 0 + Phase 1 plug-in
  contract).
- `structural-audit-2026-05` (A2 — WebAppFixture / TestKit fan-in).
- `modularization-plan` (Phase 2 — "the real CI win").
