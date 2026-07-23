# ADR-0037: Unified CI Test Tier Strategy

## Status

Accepted

## Context

The .NET test suite has grown to the point where every pull request runs every
test that is wired into `ci.yml`. The `Honua.Server.Tests` matrix is the
largest contributor: oversized protocol shards have run 30-60 minutes
wall-clock, and the
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
- The matrix in `.github/workflows/ci.yml::server-tests` already partitions
  `Honua.Server.Tests` along namespace boundaries.
- `Honua.Architecture.Tests` enforces 100% API-surface and operation coverage
  via static reflection over the `EndpointRegistry` /
  `OperationRegistry` against the
  `IntegrationTestAttribute` on test methods.

What is missing: a shared **`Tier`** trait, a CI schedule that maps each tier
to an event, a selective-test entry point so PRs that touch one feature do not
have to run every configured shard, and a flaky-quarantine reporting workflow.

This ADR is the convention source. Sibling repositories (`honua-sdk-js`,
`honua-sdk-python`, `honua-sdk-dotnet`, `honua-console`) adopt the
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
| Pull Request    | `ci.yml`                              | Build + format + Architecture tests + Tier=Fast across all projects + targeted shards (`server-tests`) composed as `(matrix.filter)&Tier!=Slow&Tier!=Fast` so Slow-tagged tests stay out of the PR lane and Fast tests run only once. |
| Merge to trunk  | `trunk-sanity.yml`                    | Restore and build only. Heavy integration coverage comes from PR gates plus scheduled/manual full integration runs. |
| Full integration | `ci.yml` (schedule / workflow_dispatch / PR label `ci/full`) | Full configured `server-tests` matrix, full-CI interop/certification lanes, and the expanded Postgres compat matrix. The `&Tier!=Slow&Tier!=Fast` exclusion still applies — Slow remains nightly-only and Fast remains in the foundation lane. |
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

- **Registration-plumbing & shared-feature-area overrides
  (`targeted_override_prefixes`).** Evaluated first (after the empty-diff guard,
  before `infrastructure_paths`). A changed file under one of these prefixes is
  *claimed* by an explicit shard subset instead of escalating to `run_all`: it is
  removed from the `infrastructure_paths` short-circuit and the
  `unmapped_source_run_all_prefixes` net, and the entry's shards are unioned into
  the targeted result. This narrows two over-broad `run_all` triggers that almost
  every feature PR tripped:
  - **Endpoint-registration plumbing** —
    `src/Honua.Server/EndpointRegistry.cs`, `src/Honua.Server/Program.cs`, and
    `src/Honua.Server/Startup/JsonContextRegistration.cs` — routes to a
    representative **smoke subset** (FeatureServer Endpoints, OGC API Features,
    OGC Classic Maps, OData Core, MCP, STAC and API Governance, Admin &
    Infrastructure), not all 45 shards. An endpoint-*adding* PR that also touches
    a feature dir under a shard's `paths` runs the smoke subset **plus** that
    feature's owning shard(s).
  - **Shared auth/security feature areas** —
    `src/Honua.Hosting/Features/Authentication/` and
    `src/Honua.Hosting/Features/Security/` — route to the auth/security shards
    (Infra and Security, Admin & Infrastructure, OData Core), not all 45.

  Safety rests on the **always-on architecture/governance guards**: the `build`
  job runs `Honua.Architecture.Tests` on every PR, which includes the
  `EndpointRegistry`/`OperationRegistry` drift + coverage tests and the
  proof-ledger tests. A registration mistake (an endpoint added to `Program.cs`
  but missing from `EndpointRegistry`, an operation with no test, an
  uncovered public surface) fails there **regardless of which server-test shards
  run** — `run_all` was never what caught those. Keep these prefixes SPECIFIC:
  they must point at individual plumbing files or narrowly-bounded feature dirs,
  never at `Honua.Core` abstractions, DI/host bootstrap
  (`Startup/InfrastructureCompositionRoot.cs`, `Honua.Hosting.csproj`), the rest
  of `src/Honua.Hosting/Features/` (Rendering/Caching/… stay `run_all` via the
  unmapped net), `Directory.Build.props`/`Directory.Packages.props`,
  `.github/workflows/**`, `ci-shards.json`, the router scripts, or `*.sln` —
  those stay `run_all`. `scripts/ci/validate-ci-router.sh` asserts every override
  shard name is a real shard and locks in the narrowed routing against
  regression.
- If the diff touches **shared infrastructure**
  (`infrastructure_paths` in `ci-shards.json` —
  `tests/dotnet/Honua.TestKit/` (the `PostgresFixture`/`SeedRunner` harness),
  the shared canonical query/filter/paging/CRS pipeline and shared
  models/config/exceptions in `src/Honua.Core/` (`Queries/`, `Models/`,
  `Configuration/`, `Exceptions/`, `Features/Infrastructure/`,
  `Features/Shared/`, `Honua.Core.csproj`), the Postgres connection/migration
  layer (`src/Honua.Postgres/Migrations/`, `Queries/`, `Features/Infrastructure/`,
  `ServiceCollectionExtensions.cs`, `Honua.Postgres.csproj`),
  `src/Honua.ServiceDefaults/`, the cross-cutting server hosting/middleware/
  monitoring infrastructure (`src/Honua.Server/Features/Infrastructure/{Hosting,
  Middleware,Services,Monitoring}/`, `src/Honua.Server/Startup/`), and
  `src/Honua.Hosting/Honua.Hosting.csproj`), the script short-circuits to
  `{"run_all": true, "reason": "infrastructure_change"}` and the matrix
  runs every shard.
- **`Honua.sln` is intentionally NOT an `infrastructure_path` (#1897).** A
  project add/remove modifies the solution on nearly every feature PR; treating
  it as shared infrastructure forced `run_all` on every new module and was a
  primary driver of full-suite-per-PR. A sln-only diff (no other signal) now
  routes to the smoke shard via `default_shards_when_no_match` (reason
  `no_path_match`); the actual added project's source still routes to its
  owning shard(s) on its own. Likewise `src/Honua.Aws/` / `src/Honua.Azure/`
  moved out of `infrastructure_paths` (they are not exercised by every shard)
  into the `unmapped_source_run_all_prefixes` safety net.
- If the diff touches a **watched source prefix** (`unmapped_source_run_all_prefixes`)
  but the changed path is **not** matched by any shard's `paths`, the script
  emits `{"run_all": true, "reason": "unmapped_source_change"}`. Post-#1897 this
  set is the safety net for two cases: (a) genuinely cross-cutting source with
  no single owning shard — the shared `Honua.Core`/`Honua.Postgres`/`Honua.Server`
  runtime, `Honua.Geometry`, `Honua.Jobs`, `Honua.Routing`, `Honua.Hosting`,
  and the cloud providers `Honua.Aws`/`Honua.Azure`; and (b) a brand-new,
  not-yet-mapped TOP-LEVEL source area (e.g. a future
  `src/Honua.Protocols.SensorThings/` landing before its shard is added). The
  alternative would be to silently fall back to the Core shard whose filter
  excludes `Honua.Server.Tests.Features.*`. New modules land green on a full
  matrix until a follow-up adds their shard + `paths`.
- **The protocol source dirs route targeted, not run_all (#1897).** Each
  protocol-specific subdir is claimed by its owning shard(s)' `paths`:
  `src/Honua.Protocols.GeoServices/FeatureServer/` → the six FeatureServer
  shards; `ImageServer/` → GeoServices ImageServer; `MapServer/`, `GPServer/`,
  `NAServer/`, `GeometryService/`, `Catalog/` → their owning shards;
  `src/Honua.Protocols.OData/` → the four OData shards;
  `src/Honua.Protocols.OgcApi/{Features,Maps,Records,Tiles,Coverages,Processes}/`,
  `src/Honua.Protocols.OgcClassic/{Wfs20,Wcs20,Wms,Wmts}/`,
  `src/Honua.Protocols.Stac/`, `src/Honua.Protocols.Scene/` + `src/Honua.Scene/`,
  and `src/Honua.Ai/` → their respective shards. So a normal single-protocol
  feature PR matches a shard (targeted) instead of tripping the unmapped net.
  Code directly under a protocol project ROOT (shared across all that
  project's shards, e.g. `GeoServicesGeometryParser.cs`, the GeoServices
  `VectorTileServer/`/`VersionManagementServer/` whose tests are currently
  unfiltered, the `OgcClassicEndpoints.cs` shared dispatcher, or
  `Honua.Protocols.Ogc.Shared/`) is left in the safety net and over-includes to
  run_all rather than risk under-testing a sibling shard.
- Otherwise, the script emits `{"run_all": false, "shards": [...]}` with the
  matched shard names.
- The script never emits an empty matrix. When no source under a watched
  prefix was touched (e.g. doc-only, CI-only, or sln-only diffs), it defaults to
  `{"run_all": false, "shards": ["Core"], "reason": "no_path_match"}` so a
  smoke shard still runs.

**Correctness invariant (#1897).** Every `src/` prefix is either (a) claimed by
the shard(s) whose `filter` selects the tests that exercise it, or (b)
intentionally in `infrastructure_paths` / `unmapped_source_run_all_prefixes` as
cross-cutting. The mapping must never cause a PR to skip a shard that genuinely
tests the changed code; when a source area's tests span several shards it is
over-included to all of them. `scripts/ci/validate-ci-router.sh` is the guard:
it dry-runs the router against representative synthetic diffs and asserts a
single-protocol change is `targeted` and excludes unrelated shards, while a
shared-harness / shared-query-pipeline / `Honua.sln` / new-unmapped-module diff
routes correctly (run_all for the cross-cutting ones, smoke shard for sln-only).
This guard runs as the `ci-router-validation` job on every PR.

**Coverage invariant (#1899).** Routing (`paths`) decides *which shards run*;
each shard then runs `dotnet test <csproj> --filter <filter>`, so a test class
only executes if some shard targeting its assembly has a `filter` that matches
its fully-qualified name. A class matched by *no* shard filter therefore never
runs — not even on `run_all`, which just selects every shard while each shard
still applies its own filter. #1899 found ~218 such orphaned classes (whole
namespaces: `Features.Admin`/`Console`/`Alerts`, GeoServices
`VectorTileServer`/`VersionManagementServer`, the `GeometryService` namespace
whose owning shard targeted the wrong assembly, several OData classes outside
the class-name-keyed OData filters, `Features.Providers.Bedrock`, the merged
SensorThings module, etc.). The fix: convert class-name-keyed filters to
namespace-prefix form where they were the gap (OData Core, Migration), add
catch-all shards (`Server Features Misc` for any `Features.*` namespace not
owned by a specific shard; `GeoServices Geometry VectorTile and Versioning` as
the GeoServices-assembly catch-all), add `Server Features Admin and Console`
and a `SensorThings` shard, and route `Features.Providers` to the AI shard. The
anti-regression guard is `scripts/ci/check-server-test-shard-coverage.py`: it
enumerates every test class across the server-test assemblies, evaluates each
shard's `filter` (the `dotnet test --filter` mini-grammar) **per target
assembly**, and fails CI if any class is claimed by zero shards. It runs inside
`scripts/ci/validate-ci-router.sh` (the `ci-router-validation` job), so a new
test class in an unmapped namespace fails the PR instead of silently never
running. A tiny, justified exemption list covers namespaces deliberately routed
to a non-PR lane (currently `Honua.Server.Tests.Scale`, the `Category=Scale`
nightly stack).

The `targeted-shards` job then projects the selected shard names into a
`matrix_include` JSON array by joining against the rich shard records in
`.github/ci-shards.json` (each record carries `shard_name`,
`artifact_suffix`, `log_name`, `timeout_minutes`, `test_timeout_minutes`,
`max_cpu_count`,
`upload_operator_eval_report`, `upload_odata_evidence`, and `filter`).
`server-tests` then declares its matrix as
`strategy.matrix.include: ${{ fromJson(needs.targeted-shards.outputs.matrix_include) }}`.
This means **unselected shards never instantiate a runner job at all** —
GitHub Actions only schedules matrix entries that exist in the include list,
so there is no per-shard checkout, build, service container, or runner cost
for shards a PR did not select. (Earlier iterations kept the full configured
matrix and gated each entry with a step-level skip, which still incurred
runner startup and Postgres service container cost for every shard. The
current dynamic-matrix model eliminates that cost.) On `push` and
`workflow_dispatch` events the descriptor is forced to `run_all: true`, so
every configured shard entry appears in `matrix_include` and runs.

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

**Slow and Fast tests stay out of PR shards.** `scripts/ci/run-server-test-shard.sh`
composes the matrix-supplied filter as `(matrix.filter)&Tier!=Slow&Tier!=Fast` before
invoking `dotnet test`. The `ci-shards.json` `filter` field expresses pure
FQN→shard routing; the Tier exclusions are layered in at a single, reviewable
test-invocation entry point shared by CI and `scripts/ci/pre-pr-check.sh`.
This prevents `[EmulatorTest]`
/ `[ScaleTest]` / `[ExternalServiceTest]` / `[CloudTest]` methods sitting in
a shard's namespace (e.g. `Honua.Server.Tests.Import.*`) from running on
PRs, and prevents Fast tests from rerunning after the foundation lane. As a
consequence, PR shards never need LocalStack/Azurite emulators —
the Slow-only `[EmulatorTest]` tests that drive `EmulatorFixture` cannot
fire — so `server-tests` does not provision them. Emulator provisioning
lives exclusively in `EmulatorFixture` (Testcontainers) and only fires
under `nightly-slow-tier.yml`.

The targeted entrypoint runs the architecture tests on every PR regardless of
diff content; that is the gate that catches the #802 class of issue (new
endpoint without integration coverage).

**Local pre-PR runs use the same smart filters.** `scripts/ci/pre-pr-check.sh`
consumes the identical router (`honua-server-targeted-tests.sh`) and affected-
projects closure (`compute-affected-projects.sh`) the CI workflow uses, so a
local run scopes the build (affected-project solution filter), format check
(changed `*.cs` only), unit-test projects (affected closure), and server-test
shards (targeted subset) to the diff against `origin/trunk` instead of grinding
the whole solution and every shard. The architecture tests still run on every
invocation (cheap topology guard). Set `HONUA_PRE_PR_FULL=1` (or `--full`) to
force the full suite (recommended before a release or a large cross-cutting
refactor); `HONUA_PRE_PR_BASE=<ref>` (or `--base <ref>`) overrides the diff
base, and `HONUA_PRE_PR_SKIP_AOT=1` skips the AOT publish. If the base ref is
missing locally the script falls back to a full run so it never silently
under-tests.

**Capability-scoped narrowing layers on top, never replaces, this router**
(honua-server#2951). `honua-server-targeted-tests.sh` still decides WHICH
shards run — that has not changed. On top of that selection,
`scripts/ci/capability-impact.py select-local` (the same route/proving-test/
shard crosswalk `capability-impact-comparison.yml` validates in shadow mode)
narrows a selected shard's `dotnet test --filter` down to just the proving
tests `testsByShard` assigns to that shard, but ONLY when the crosswalk
accounted for every changed file with zero ambiguity (not `runAll`, no
`unmatchedSourceFiles`) AND its own shard list corroborates the shard ADR-0037
selected. Any diff the crosswalk cannot confidently map — which today includes
essentially all handler/service source edits, since `feature-catalog.json`'s
`code_location` mostly resolves to the endpoint-registry file rather than the
real handler — falls back to the shard's ADR-0037 filter unchanged, so this
can only narrow further, never select fewer shards. A single widely-shared
`code_location` (e.g. `EndpointRegistry.cs` itself) can still map a shard to
thousands of proving tests; a narrowed filter over ~6,000 characters falls
back to that shard's full ADR-0037 filter instead of risking an oversized
`dotnet test --filter` argument on the Windows/Git Bash path. `--dry-run` (or
`HONUA_PRE_PR_DRY_RUN=1`) prints the full
resolved selection — build set, targeted shards, capability narrowing, format
scope — without restoring, building, formatting, or testing anything. A set of
cross-cutting paths (`Directory.Build.props`, `Directory.Packages.props`,
`Honua.TestKit/**`, `.github/ci-shards.json`, the selector/catalog files
themselves, `.github/workflows/**`) forces full mode outright, as does a
failing `capability-impact.py validate`.

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
  (one shard + Fast tier instead of every configured shard). Target: < 10 min wall-clock
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
  `artifact_suffix`, `log_name`, `timeout_minutes`, `test_timeout_minutes`,
  `max_cpu_count`, upload
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
  `(matrix.filter)&Tier!=Slow&Tier!=Fast` so Slow-tagged tests in a shard's
  namespace (e.g. `[EmulatorTest]` methods inside
  `Honua.Server.Tests.Import.*`) do not run on PRs and Fast tests do not
  rerun after the foundation lane. `Tier!=Slow` is preferred over
  `Tier=Integration` because a
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
  `artifact_suffix`, `log_name`, `timeout_minutes`, `test_timeout_minutes`,
  `max_cpu_count`, upload
  flags, `filter`). The runtime `&Tier!=Slow&Tier!=Fast` composition is applied
  uniformly to every shard by `scripts/ci/run-server-test-shard.sh` and is
  not encoded in `ci-shards.json`. The shared runner also emits heartbeat
  lines, periodic tails of normal-verbosity test logs, and
  `<log_name>.timing.json`, and it enforces `test_timeout_minutes` inside
  the outer GitHub Actions `timeout_minutes` so diagnostic artifacts survive
  a slow shard.
- `nightly-slow-tier.yml` currently runs `Tier=Slow&Category=Emulator`
  because LocalStack S3 + Azurite + Postgres are the only fixtures
  provisioned. The Scale, Cloud, and ExternalService subfamilies need
  dedicated fixtures (multi-node compose, real cloud credentials, Esri
  Geoportal) and are tracked as separate workflows; their attributes still
  emit `Tier=Slow` so a future workflow can opt them in by tightening
  `Category=`.
