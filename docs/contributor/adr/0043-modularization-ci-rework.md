# ADR-0043: Modularization CI Rework (Modularization Phase 3)

## Status

Proposed

## Context

Critical-path CI on `.github/workflows/ci.yml` is ~125 min wall-clock today.
The modularization plan (memory: `modularization-plan`) targets ≤30 min once
the protocol/test extractions (Phases 1 and 2) land. Phase 3 is the workflow
rework that converts the build-graph and test-project separation produced by
Phases 0–2 into actual minutes saved.

ADR-0037 (Unified CI Test Tier Strategy) already established the targeted-
test mechanism that turns `.github/ci-shards.json` into a dynamic matrix
on PRs — that is the foundation this ADR extends. ADR-0041 landed
`Honua.Core.Abstractions` and the `IHonuaProtocolModule` plug-in surface.
ADR-0042 landed the per-protocol test-project split that lets the matrix
target a small compile unit. With those in hand, Phase 3 finally has
something to selectively build.

## Decision

Five small, independently revertable workflow PRs (mirror the
modularization-plan's "five smallish PRs, not one mega-PR" guidance), each
landed with `ci(...)` commit prefix:

### 1. Cross-job binary cache

Restore `bin/` + `obj/` keyed on a content hash of `src/**/*.cs` +
`Directory.Packages.props` + `*.csproj`. Use `actions/cache@v4`. Today
every shard rebuilds everything from scratch; a cache hit means
`dotnet build --no-restore` skips compilation for unchanged projects.

Per-job key uses `${{ runner.os }}-dotnet-build-${{ hashFiles(...) }}` with
a coarser fallback key so a one-file change still hits the per-project
slot's cache.

### 2. Affected-projects build

Replace the unconditional `dotnet build Honua.sln` with a graph-walk:

- `git diff --name-only origin/${{ base }}...HEAD` → changed source paths.
- Map each path to the owning `*.csproj` (use the
  `.github/ci-shards.json` `paths` array as the routing table; reuse the
  shell + jq machinery from `scripts/ci/honua-server-targeted-tests.sh`).
- Walk reverse-dependencies via `dotnet list reference` to capture
  consumer projects.
- Build only the affected closure.

Doc-only / infra-shared edits stay on the full-graph fallback (ADR-0037's
`infrastructure_paths` short-circuit).

### 3. Per-project test jobs

Each extracted protocol test project (per ADR-0042) becomes its own matrix
entry in the `server-tests` job. The matrix-build code in
`.github/workflows/ci.yml` already builds `matrix_include` dynamically
from `.github/ci-shards.json` (ADR-0037 §"Selective Execution On PRs"),
so this PR just routes ADR-0042's new shard records through the existing
mechanism — no workflow logic change beyond confirming the per-project
test invocation works in isolation.

### 4. Reuse Postgres container across shards

A `services: postgres:` block already exists on the `server-tests` job
(verified pre-PR). Phase 3 widens that into a shared service definition so
every shard on the same runner attaches to a single Postgres rather than
spinning up its own. Testcontainers stays — the audit explicitly marked
its per-class schema isolation as the right design (`structural-audit-2026-05`
section "NOT problems"). What changes is whether the *container* is shared;
Testcontainers' schema-per-class semantics keep tests isolated regardless.

### 5. Gate-model update

`docs/ci/gate-model.md` and the required-checks list move from the
monolithic `Server Tests (everything)` to the per-protocol-test + shared
build + arch tests set. The monolith stays in `ci.yml` for one release
cycle as a non-required job so a regression in the new matrix is caught
before the canonical gate switches over; then the monolith is removed.

This PR also adds the next ADR in the lineage (`0044-…`) recording the
final gate model.

## Sequencing

Land in the listed order. PR 1 (binary cache) and PR 4 (shared PG) are
purely additive — they ship a measurable wall-clock win even before
Phases 1/2 are complete and can be queued the moment the bundle PR
(currently #1236) merges. PR 2 (affected-projects) and PR 3 (per-project
matrix) need at least one protocol's worth of Phase 1 + Phase 2 merged
so the affected-projects graph and the per-project shard have something
to point at. PR 5 (gate-model) waits until per-project shards have
stabilized.

Each PR includes a before/after wall-clock comparison in the body
(`gh run list --branch <branch> --limit 5` over a throw-away PR), per
the modularization-plan's verify-step.

## Consequences

### Positive

- Critical-path CI target ≤ 30 min on a single-protocol-touching PR.
- The cache + affected-projects PRs front-load most of the wall-clock
  reduction so the test-project splits don't need to land before users
  notice an improvement.
- Required-checks gate model encoded as a single ADR + one
  `docs/ci/gate-model.md` source of truth.

### Negative

- Five workflow PRs is operational overhead — each requires its own
  before/after measurement on a throwaway branch. The plan accepts this
  because workflow files are high-blast-radius and revertability matters
  more than landing speed.
- The `services: postgres:` widening (PR 4) crosses cleanly only if
  no shard tries to `DROP DATABASE` the shared instance. The audit
  confirmed Testcontainers uses schema-per-class isolation, not
  database-per-class, so this should hold; a regression manifests as
  cross-shard schema collisions and is caught immediately.
- Affected-projects build (PR 2) is a behavioural change to what runs
  on a PR. A bug in the dependency walker could let a regression slip
  past — mitigated by retaining `infrastructure_paths` short-circuit
  to full-graph on shared edits, and by keeping the nightly full
  matrix as the safety net.

### Neutral

- ADR-0037's Tier=Fast/Integration/Slow split is unchanged. Phase 3
  is wall-clock plumbing; tiering remains the orthogonal axis.

## References

- ADR-0037: Unified CI Test Tier Strategy (the lane model this extends).
- ADR-0041: Honua.Core.Abstractions Extraction (Phase 0 + Phase 1 plug-in
  contract).
- ADR-0042: Per-Protocol Test Project Split (Phase 2).
- `.github/ci-shards.json`, `.github/workflows/ci.yml`,
  `scripts/ci/honua-server-targeted-tests.sh`,
  `scripts/ci/run-server-test-shard.sh`.
- `modularization-plan` (Phase 3 — CI rework).
