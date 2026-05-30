# ADR-0044: Server.Features.Infrastructure Decomposition (Audit-A1 Follow-Up)

## Status

Proposed

## Context

The modularization plan's Phase 1 calls for extracting each protocol surface
(`OData`, `OgcApi`, `OgcClassic`, `GeoServices`) into its own
`Honua.Protocols.<X>` assembly. ADR-0041 landed the plug-in host contract
(`IHonuaProtocolModule` + four module impls), the AOT-safe explicit-list
registry, and the additive composition entry points so the actual extraction
is a mechanical move rather than an API redesign.

The mechanical move is **blocked** by a cycle:

- A protocol assembly (e.g. `Honua.Protocols.OData`) would
  `ProjectReference` `Honua.Server`, because OData handlers consume types
  from `Honua.Server.Features.Infrastructure.{Authentication,Caching,Events,Helpers,Models,Validation}`.
- `Honua.Server` already needs to `ProjectReference` `Honua.Protocols.OData`
  so the module's `MapEndpoints` can wire the protocol's routes.

The cycle was confirmed by an OData scout that hit `CS0234: 'Infrastructure'
does not exist in 'Honua.Server.Features'` once the OData files moved into
the new protocol assembly (`refactor/protocols-odata` branch, deleted unpushed).

This is exactly the audit-A1 finding in `structural-audit-2026-05`:

> A1 (High): `src/Honua.Server/Features/Infrastructure/` is a
> 351-file/42-subdir grab-bag; Auth(45), ControlPlane(38),
> Styling(26), Rendering(16) misfiled. Both it and Core's
> Infrastructure(84 files) are in `infrastructure_paths` in
> `.github/ci-shards.json` → routine edits force `run_all`. Arch tests
> EXEMPT Infrastructure (`VerticalSliceIsolationTests.cs:44,88`) so it
> grew unchecked. Fix: extract sub-areas into slices w/ own shard
> paths; shrink infra paths to Hosting/Middleware/ServiceRegistration/
> core-Caching.

Decomposing `Honua.Server.Features.Infrastructure` is therefore a
**prerequisite** for Phase 1 physical assembly extraction, not a follow-up.

## Decision

Decompose `src/Honua.Server/Features/Infrastructure/` along the audit-A1
seams. Each sub-area becomes its own slice with its own
`.github/ci-shards.json` path entry (no longer forcing `run_all`); the
ones consumed by protocol assemblies are extracted into a separate
`Honua.Hosting.*` assembly that protocols and Server can both reference
without cycles.

### Sub-area extraction order

Drive the order by what unblocks Phase 1's OData extraction (the
canonical first protocol). OData consumes six Infrastructure sub-areas:

1. **Authentication (45 files)** — consumed by all four protocols. Extract
   to `src/Honua.Hosting.Authentication/`. Includes the existing
   `Authentication.ClientCertificates` subtree.
2. **Models (12 files)** — `ApiErrorResponse`, `ProblemDetailsResponse`,
   `StandardErrorResponse*`, `ProtocolRequestClassifier`. The smallest
   surface but currently has a back-edge into `Protocols.OData.Models` /
   `Protocols.OData.Services` (verified). The back-edge is a misfile —
   the OData type should move into Models, not the other way round —
   resolve while extracting.
3. **Events (20 files)** — protocol-emit transport.
4. **Validation (15 files)** — request-shape validators called from
   handlers; not the `Honua.Core.Features.Validation` abstractions.
5. **Helpers (15 files)** — content negotiation, secret reference
   resolution, base-URL resolution, filter-expression helpers. Currently
   depends on `Authentication`, `Models`, `Validation` (verified) so it
   moves after those three.
6. **Caching (35 files)** — HTTP-level caching surface (not
   `Honua.Core.Features.Caching`). Largest of the six but isolated from
   the others.

The remaining ~36 sub-areas (ControlPlane, Styling, Rendering, Hosting,
Middleware, …) stay in `Honua.Server` for now. Their extraction is
sequenced behind Phase 1 since they aren't consumed by protocol assemblies.

### Sequencing

One sub-area per PR, in the order above. Each PR:

1. Creates `src/Honua.Hosting.<SubArea>/Honua.Hosting.<SubArea>.csproj`
   referencing only `Honua.Core.Abstractions` + `Honua.Core` (and any
   prior `Honua.Hosting.*` slices it transitively needs).
2. `git mv`-s the sub-area's source files (namespace
   `Honua.Server.Features.Infrastructure.<SubArea>` preserved so no
   consumer needs a `using` update).
3. `Honua.Server.csproj` adds a `ProjectReference` to the new slice.
4. `.github/ci-shards.json` adds a shard record for the new slice and
   drops the corresponding entry from `infrastructure_paths`.
5. The arch-test exemption in `VerticalSliceIsolationTests.cs:44,88` is
   tightened: the new slice is treated like any other vertical slice
   (arch enforcement applies). Audit-A1 explicitly calls this out as
   the reason Infrastructure grew unchecked.

Six PRs land Phase 0c. Phase 1's four protocol-extraction PRs follow
(one per protocol).

### Done

For each sub-area:

1. New `Honua.Hosting.<SubArea>` csproj exists; `Honua.Server`
   ProjectReferences it.
2. Old `src/Honua.Server/Features/Infrastructure/<SubArea>/` is empty.
3. Arch tests apply to the new slice (no Infrastructure exemption).
4. `.github/ci-shards.json` routes the slice's tests to a dedicated
   shard; `infrastructure_paths` no longer triggers `run_all` on edits
   to it.
5. Full solution build clean.

After all six sub-areas:

6. The OData extraction scout (re-run from a fresh branch) compiles
   cleanly without back-referencing `Honua.Server`. This is the
   acceptance gate for Phase 0c.

## Consequences

### Positive

- Phase 1's mechanical extraction unblocks.
- Audit-A1's CI-shard `infrastructure_paths` problem (routine edits
  forcing `run_all`) shrinks proportionally as sub-areas move out.
- Arch tests stop exempting Infrastructure — the rot that the audit
  describes no longer grows undetected.

### Negative

- Six additional PRs ahead of Phase 1's four protocol-extraction PRs
  (ten PRs before any `Honua.Protocols.<X>` ships). The modularization
  plan estimated four Phase 1 PRs and did not anticipate the audit-A1
  prerequisite. The corrected total is ~14 PRs for Phases 1–3 combined,
  not the ~9 the plan originally listed.
- Each sub-area extraction touches `Honua.Server.csproj`,
  `Honua.sln`, and `.github/ci-shards.json`. Sequential merge is required
  so the JSON conflicts don't have to be re-resolved per PR; the
  modularization plan's "branch off the previous merged commit" rule
  applies here too.

### Neutral

- The new `Honua.Hosting.*` assembly family is an internal organizational
  convention; it does not change runtime behaviour. Once Phase 1 lands
  and the per-protocol assemblies stop touching the legacy paths, the
  `Honua.Hosting.*` boundary becomes the actual hosting surface and the
  remaining 30+ Infrastructure sub-areas can be decomposed at lower
  priority (they don't gate Phase 1+).

## References

- ADR-0037: Unified CI Test Tier Strategy (the lane model these slices
  feed into via `.github/ci-shards.json`).
- ADR-0041: Honua.Core.Abstractions Extraction (Phase 0 + Phase 1
  plug-in contract).
- ADR-0042: Per-Protocol Test Project Split (Phase 2).
- ADR-0043: Modularization CI Rework (Phase 3).
- `structural-audit-2026-05` (A1 — Infrastructure grab-bag with file
  counts).
- `modularization-plan` (Phase 1 — protocol assembly extraction).
- OData scout verification: `refactor/protocols-odata` branch (deleted
  unpushed) recorded `CS0234: 'Infrastructure' does not exist in
  'Honua.Server.Features'` for ~30 files when the protocol code moved
  into a separate assembly.
