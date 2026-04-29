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
| Pull Request    | `ci.yml`                              | Build + format + Architecture tests + Tier=Fast across all projects + targeted shards (`server-tests`) composed as `(matrix.filter)&Tier!=Slow` so Slow-tagged tests stay out of the PR lane. |
| Merge to trunk  | `trunk-sanity.yml`                    | Restore and build only. Heavy integration coverage comes from PR gates plus scheduled/manual full integration runs. |
| Full integration | `ci.yml` (schedule / workflow_dispatch) | Full 11-shard `server-tests` matrix and the Postgres compat matrix. The `&Tier!=Slow` exclusion still applies — Slow remains nightly-only. |
| Nightly (slow)  | `nightly-slow-tier.yml`               | `Tier=Slow&Category=Emulator` (LocalStack S3 + Azurite + Postgres). Scale/Cloud/External slow subfamilies need additional fixtures and are tracked as separate workflows. |
| Nightly (flake) | `flaky-detection.yml`                 | Re-runs the Integration tier 3× and emits a candidate-flake report.                                   |

`server-tests` is the largest contributor and stays sharded. The shards are
the canonical Integration-tier partition for `Honua.Server.Tests`. The
shard map is now also encoded in `.github/ci-shards.json` so the targeted-
test script and the workflow matrix do not drift.

### Selective Execution On PRs

`scripts/ci/honua-server-targeted-tests.sh` reads the PR diff, maps each
changed source path to one or more shards via `.github/ci-shards.json`, and
emits a JSON descriptor consumed by the `server-tests` matrix:

- If the diff touches **shared infrastructure**
  (`infrastructure_paths` in `ci-shards.json` —
  `tests/dotnet/Honua.TestKit/`, `src/Honua.Core/`, `src/Honua.Postgres/`,
  `src/Honua.ServiceDefaults/`, `src/Honua.Server/Features/Infrastructure/`,
  `Honua.sln`, `.github/`, `scripts/ci/`), the script short-circuits to
  `{"run_all": true, "reason": "infrastructure_change"}` and the matrix
  runs every shard.
- If the diff touches a **watched source prefix** (`unmapped_source_run_all_prefixes`
  — `src/Honua.Server/`, `src/Honua.DuckDB/`, `tests/dotnet/Honua.Server.Tests/`)
  but the changed path is **not** matched by any shard's `paths`, the script
  emits `{"run_all": true, "reason": "unmapped_source_change"}`. This catches
  new feature directories whose tests are not yet routed to a specific shard
  — the alternative would be to silently fall back to the Core shard whose
  filter excludes `Honua.Server.Tests.Features.*`. New features land green
  on a full matrix until a follow-up shards them.
- Otherwise, the script emits `{"run_all": false, "shards": [...]}` with the
  matched shard names.
- The script never emits an empty matrix. When no source under a watched
  prefix was touched (e.g. doc-only diffs), it defaults to
  `{"run_all": false, "shards": ["Core"], "reason": "no_path_match"}` so a
  smoke shard still runs.

The `targeted-shards` job then projects the selected shard names into a
`matrix_include` JSON array by joining against the rich shard records in
`.github/ci-shards.json` (each record carries `shard_name`,
`artifact_suffix`, `log_name`, `timeout_minutes`, `max_cpu_count`,
`upload_operator_eval_report`, `upload_odata_evidence`, and `filter`).
`server-tests` then declares its matrix as
`strategy.matrix.include: ${{ fromJson(needs.targeted-shards.outputs.matrix_include) }}`.
This means **unselected shards never instantiate a runner job at all** —
GitHub Actions only schedules matrix entries that exist in the include list,
so there is no per-shard checkout, build, service container, or runner cost
for shards a PR did not select. (Earlier iterations kept the full 11-entry
matrix and gated each entry with a step-level skip, which still incurred
runner startup and Postgres service container cost for every shard. The
current dynamic-matrix model eliminates that cost.) On `push` and
`workflow_dispatch` events the descriptor is forced to `run_all: true`, so
all eleven entries appear in `matrix_include` and run.

`.github/ci-shards.json` is the single source of truth for both routing
(`paths`) and matrix-runtime metadata. Adding a shard means editing one
file. There is no separate parity check because there is no second source
of truth to drift from.

In addition to selecting shards, the **PR Fast tier always runs** regardless
of which shards are selected: `dotnet-foundation-tests` invokes
`dotnet test --filter "Tier=Fast"` against `Honua.Server.Tests` (alongside the
unfiltered Core / LoadTests / Architecture projects whose contents are
already Fast-tier). This honours the "Tier=Fast across all projects on every
PR" contract and prevents a PR with no matching shard from skipping the fast
server unit tests.

**Slow tests stay out of PR shards.** The `Run server test shard` step in
`ci.yml` composes the matrix-supplied filter as `(matrix.filter)&Tier!=Slow`
before invoking `dotnet test`. The `ci-shards.json` `filter` field expresses
pure FQN→shard routing; the Tier exclusion is layered in at a single,
reviewable point at the test-invocation step. This prevents `[EmulatorTest]`
/ `[ScaleTest]` / `[ExternalServiceTest]` / `[CloudTest]` methods sitting in
a shard's namespace (e.g. `Honua.Server.Tests.Import.*`) from running on
PRs. As a consequence, PR shards never need LocalStack/Azurite emulators —
the Slow-only `[EmulatorTest]` tests that drive `EmulatorFixture` cannot
fire — so `server-tests` does not provision them. Emulator provisioning
lives exclusively in `EmulatorFixture` (Testcontainers) and only fires
under `nightly-slow-tier.yml`.

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
  (a) the merge queue running the full matrix on push, (b) the script
  defaulting to `run_all` when shared infrastructure changes, and (c) the
  `unmapped_source_run_all_prefixes` guard which forces `run_all` when a PR
  touches a source path under a watched prefix that no shard claims.
- `.github/ci-shards.json` becomes load-bearing. It is the single source of
  truth for both the routing data (`paths`, `infrastructure_paths`,
  `unmapped_source_run_all_prefixes`, `default_shards_when_no_match`) and
  the matrix-runtime metadata each shard provides (`shard_name`, `filter`,
  `artifact_suffix`, `log_name`, `timeout_minutes`, `max_cpu_count`, upload
  flags). The `Detect Targeted Server-Tests Shards` job builds the
  `matrix_include` array directly from this file, so there is no second
  source to drift from. Drift between source layout and shard mapping (a
  new source directory landing without an entry in `ci-shards.json`) is
  still possible but is contained by the
  `unmapped_source_run_all_prefixes` guard which emits `run_all` for that
  PR.
- Operating-system-level flakes (Testcontainers slow-start, runner congestion)
  look like flaky tests. Human review is required before `[FlakyTest]` is
  applied.

### Neutral

- Cross-repo coordination requires honua-devops to mirror this ADR's tier
  strings. The mirror is part of #810's compat matrix work.

## Implementation Notes

- TestKit attribute traits are declared via `ITraitAttribute` and
  `ITraitDiscoverer`. The discoverers return a `KeyValuePair<string,string>`
  list; the `Tier` value comes from `Honua.TestKit.Constants.Tiers`
  (`Tiers.Fast` / `Tiers.Integration` / `Tiers.Slow`) — discoverers must
  reference the constants rather than hard-coding the strings so the
  cross-repo contract stays in one place.
- `dotnet test --filter "Tier=Fast"` and
  `dotnet test --filter "Tier=Integration"` consume the trait directly.
  The PR `server-tests` step composes its filter as
  `(matrix.filter)&Tier!=Slow` so Slow-tagged tests in a shard's namespace
  (e.g. `[EmulatorTest]` methods inside `Honua.Server.Tests.Import.*`) do not
  run on PRs. `Tier!=Slow` is preferred over `Tier=Integration` because a
  significant fraction of Server.Tests methods are still plain `[Fact]`
  without a Tier trait — those are kept in the PR lane.
- `FlakyTestAttribute` is `[AttributeUsage(... AllowMultiple = false)]`. A
  test should never be tagged flaky on more than one attribute level — if
  the whole class is flaky, tag the class.
- The targeted-test script is shell + jq only. Keep the PR-gate startup cost
  near zero — the script must complete in well under a second on the
  workflow runner.
- The `targeted-shards` job uses `jq` only (no Python required) to project
  the selected shard names into a `matrix_include` array drawn from
  `.github/ci-shards.json`. The `server-tests` matrix is then declared as
  `strategy.matrix.include: ${{ fromJson(needs.targeted-shards.outputs.matrix_include) }}`,
  so unselected shards never instantiate a runner.
- Any new shard is added to `.github/ci-shards.json` only — there is no
  separate matrix entry in `ci.yml`. Each shard record carries both routing
  data (`paths`) and matrix-runtime metadata (`shard_name`,
  `artifact_suffix`, `log_name`, `timeout_minutes`, `max_cpu_count`, upload
  flags, `filter`). The runtime `&Tier!=Slow` composition is applied
  uniformly to every shard at the test-invocation step in `ci.yml` and is
  not encoded in `ci-shards.json`.
- `nightly-slow-tier.yml` currently runs `Tier=Slow&Category=Emulator`
  because LocalStack S3 + Azurite + Postgres are the only fixtures
  provisioned. The Scale, Cloud, and ExternalService subfamilies need
  dedicated fixtures (multi-node compose, real cloud credentials, Esri
  Geoportal) and are tracked as separate workflows; their attributes still
  emit `Tier=Slow` so a future workflow can opt them in by tightening
  `Category=`.
