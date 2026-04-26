# ADR-0037: Unified CI Test Tier Strategy

## Status

Accepted

## Context

The .NET test suite has grown to the point where every pull request runs every
test that is wired into `ci.yml`. The 11-shard `Honua.Server.Tests` matrix is
the largest contributor: each shard runs 20–45 minutes wall-clock, and the
cumulative runner contention has produced three concrete failure modes in the
last quarter:

- **#801** sat in local-checks for ~19.5 hours waiting behind the PR queue
  because every queued PR re-ran the full integration matrix.
- **#757** hit the supervisor requeue-loop guard with same-signature failures —
  intermittent failures had no quarantine path, so the same flake re-tripped
  the gate run after run.
- **#802** ran into architecture-test churn when `ApiSurfaceCoverageTests`
  fired before integration coverage was added for a freshly-merged endpoint.

The shape of the problem is _no execution-tier distinction_: PRs, merges, and
nightly all run the same monolithic matrix. There is no fast-lane for PRs that
touch a single feature, no place for legitimately-slow work (multi-node scale,
external services), and no quarantine for known-flaky tests.

Existing infrastructure that this ADR builds on:

- `Honua.TestKit/Attributes/UnitTestAttribute`,
  `IntegrationTestAttribute`, `ScaleTestAttribute`,
  `ExternalServiceTestAttribute`, `EmulatorTestAttribute`,
  `CloudTestAttribute` already emit a `Category` trait and (for the
  env-gated ones) a `Skip` reason when required environment variables are
  absent.
- The 11-shard matrix in `.github/workflows/ci.yml::server-tests` already
  partitions `Honua.Server.Tests` along namespace boundaries.
- `Honua.Architecture.Tests` enforces 100% API-surface and operation coverage
  via static reflection over the `EndpointRegistry` /
  `OperationRegistry` against the
  `IntegrationTestAttribute` on test methods.

What is missing: a shared **`Tier`** trait, a CI schedule that maps each tier
to an event, a selective-test entry point so PRs that touch one feature do not
have to run all eleven shards, and a flaky-quarantine reporting workflow.

This ADR is the convention source. Sibling repositories (`honua-sdk-js`,
`honua-sdk-python`, `honua-sdk-dotnet`, `honua-server-admin`) adopt the
same `Tier` trait and the same `Fast / Integration / Slow` schedule via
the cross-repo CI conventions in `honua-devops`.

## Decision

### Three Execution Tiers

| Tier | Trait                              | Goal                                           | Examples                                                       |
|------|------------------------------------|------------------------------------------------|----------------------------------------------------------------|
| Fast | `Tier=Fast`                        | Run on every PR. Wall-clock target < 10 min.   | `[UnitTest]` — no DB, no HTTP, no Testcontainers.              |
| Integration | `Tier=Integration`         | Real DB / HTTP / in-process services.          | `[IntegrationTest]` — most of the existing test suite.         |
| Slow | `Tier=Slow`                        | External services, emulators, multi-node, perf | `[ScaleTest]`, `[ExternalServiceTest]`, `[EmulatorTest]`, `[CloudTest]`. |

A test is assigned exactly one tier. Tier assignment is done **on the
attribute**, not on the test method, so the existing TestKit attributes are
the single source of truth and no test method needs to be re-tagged for the
default mapping. The mapping is documented in
`tests/dotnet/Honua.TestKit/Constants/Tiers.cs`.

### Tagging Convention

- New test attributes must emit one and only one `Tier` trait.
  Use `Honua.TestKit.Constants.Tiers` (`Fast`, `Integration`, `Slow`).
- Existing tests do not need to change. The trait is added once on the
  attribute discoverer; every test method that uses the attribute inherits
  the trait at xUnit discovery time.
- The `Category` trait is preserved untouched. `Tier` is purely additive.
- Sibling repositories MUST use the same string values (`Fast`,
  `Integration`, `Slow`). Any divergence breaks the cross-repo
  selective-execution convention.

### CI Schedule

| Event           | Workflow                              | What runs                                                                                             |
|-----------------|---------------------------------------|-------------------------------------------------------------------------------------------------------|
| Pull Request    | `ci.yml`                              | Build + format + Architecture tests + Tier=Fast across all projects + targeted Tier=Integration shards via `scripts/ci/honua-server-targeted-tests.sh`. |
| Merge to trunk  | `ci.yml` (push event)                 | Everything PR runs **plus** the full 11-shard `server-tests` matrix and the Postgres compat matrix.   |
| Nightly         | `nightly-slow-tier.yml`               | `Tier=Slow` across all projects with required environment variables asserted before the test step.    |
| Nightly (flake) | `flaky-detection.yml`                 | Re-runs the Integration tier 3× and emits a candidate-flake report.                                   |

`server-tests` is the largest contributor and stays sharded. The shards are
the canonical Integration-tier partition for `Honua.Server.Tests`. The
shard map is now also encoded in `.github/ci-shards.json` so the targeted-
test script and the workflow matrix do not drift.

### Selective Execution On PRs

`scripts/ci/honua-server-targeted-tests.sh` reads the PR diff, maps each
changed source path to one or more shards via `.github/ci-shards.json`, and
emits a JSON descriptor consumed by the `server-tests` matrix:

- If the diff touches **shared infrastructure** (`tests/dotnet/Honua.TestKit/`,
  `src/Honua.Core/`, `src/Honua.Postgres/`, `src/Honua.Server/Features/Infrastructure/`,
  `Honua.sln`, `.github/`, `scripts/ci/`), the script emits
  `{"run_all": true}` and the matrix runs every shard. This is the safe
  fallback.
- Otherwise, the script emits `{"run_all": false, "shards": ["odata", ...]}`
  with the relevant shard names.
- The matrix uses GitHub Actions' built-in `if:` filter on the matrix entry so
  unmatched shards skip cleanly with a green status.
- The script never emits an empty matrix. When no source paths match, it
  defaults to `{"run_all": false, "shards": ["core"]}` so the smoke shard
  always runs.

The targeted entrypoint runs the architecture tests on every PR regardless of
diff content; that is the gate that catches the #802 class of issue (new
endpoint without integration coverage).

### Flaky-Test Quarantine

- New `Honua.TestKit.Attributes.FlakyTestAttribute(reason)` adds the trait
  `Flaky=true`. It does **not** set `Skip`. Quarantined tests still run on
  the same schedule as their tier — quarantining is a reporting concern.
- `flaky-detection.yml` runs nightly (and on `workflow_dispatch`) and
  re-runs the Integration tier three times against a fresh runner. Any test
  that is not 3-for-3 consistent across the runs is reported as a flake
  candidate via job summary and an `actions/upload-artifact@v7` JSON
  payload.
- The workflow does **not** auto-edit source. A human reviews candidates
  and applies `[FlakyTest("reason — tracked in #N")]`. Once tagged, a
  follow-up triage workflow can filter on `Flaky=true` to track the
  quarantine queue.

### Architecture-Test Interaction

The 100% API-surface coverage check stays on PRs. A PR that adds an HTTP
endpoint without its `[IntegrationTest]` will still fail the gate.
The ADR explicitly requires endpoints and their integration tests to land
together; deferring integration tests to a follow-up PR is not allowed.

`FlakyTestAttribute` is recognized as additive — the
`ApiSurfaceCoverageTests` reflection inspects `IntegrationTestAttribute`
independently of `FlakyTestAttribute`, so quarantining a test does not
remove it from the coverage ledger.

## Consequences

### Positive

- PR turnaround drops sharply for PRs that touch a single feature directory
  (one shard + Fast tier instead of all 11 shards). Target: < 10 min wall-clock
  on the happy path.
- Slow / external / emulator tests have a real home (nightly) instead of
  silently no-op-ing on PRs because their env vars aren't set.
- Flaky tests are visible: they appear in the nightly flake report instead of
  re-tripping the requeue-loop guard.
- The convention is portable: sibling repos adopt the same tier strings and
  inherit the same execution model.

### Negative

- Integration regressions outside the targeted shards reach trunk. Mitigated by
  (a) the merge queue running the full matrix on push, and (b) the script
  defaulting to `run_all` when shared infrastructure changes.
- `.github/ci-shards.json` becomes load-bearing. Drift between source layout
  and shard mapping is silent unless a CI step validates that every source
  directory is covered. The validation step is wired into the
  `Detect Targeted Shards` job below.
- Operating-system-level flakes (Testcontainers slow-start, runner congestion)
  look like flaky tests. Human review is required before `[FlakyTest]` is
  applied.

### Neutral

- Cross-repo coordination requires honua-devops to mirror this ADR's tier
  strings. The mirror is part of #810's compat matrix work.

## Implementation Notes

- TestKit attribute traits are declared via `ITraitAttribute` and
  `ITraitDiscoverer`. The discoverers return a `KeyValuePair<string,string>`
  list; the `Tier` value comes from `Honua.TestKit.Constants.Tiers`.
- `dotnet test --filter "Tier=Fast"` and
  `dotnet test --filter "Tier=Integration"` consume the trait directly.
- `FlakyTestAttribute` is `[AttributeUsage(... AllowMultiple = false)]`. A
  test should never be tagged flaky on more than one attribute level — if
  the whole class is flaky, tag the class.
- The targeted-test script is shell-only. Avoid Python so the PR-gate startup
  cost stays under one second.
- Any new shard added to the `server-tests` matrix MUST also be added to
  `.github/ci-shards.json` in the same PR. CI fails fast if a shard exists in
  one and not the other.
