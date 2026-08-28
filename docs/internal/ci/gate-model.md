# CI Gate Model

> Defines the five-tier quality gate model governing all CI workflows across the Honua project.
> Last updated: 2026-08-17

## Tier Definitions

| Tier | Purpose | Characteristics | Merge-blocking |
|---|---|---|---|
| **PR** | Deterministic pre-merge confidence | Fast, path-filtered when possible, no flaky external dependencies | Yes |
| **nightly** | Broad regression and compatibility coverage | Expensive, long-running, external-system-heavy, certification-style | No |
| **release** | Packaging and release certification | Publish/package/sign/smoke-test tied to tags or release branches | No (for routine PRs) |
| **deploy** | Environment promotion and post-apply validation | Manual or protected-branch workflows tied to environments | No (for routine PRs) |
| **maintenance** | Repo automation and housekeeping | Version automation, metadata updates, scheduled hygiene | No |

## Governing Rules

1. **PR gates must be deterministic and directly actionable by the author.** If a check fails, the author must be able to reproduce and fix it locally without external system access.

2. **External conformance suites, soak tests, and long security scans do not belong in the PR lane.** These run on schedule or manual dispatch only.

3. **Release and deploy workflows remain strict** but do not burden everyday feature merges.

4. **New checks default to nightly** unless explicitly justified as PR-blocking. Justification requires: deterministic behavior, sub-5-minute runtime, and author-actionable failure messages.

   **Named exceptions — `PR Gate` and `Review Gate`.** Together, `PR Gate` and `Review Gate` are the required PR lane on `trunk`: the former is unprivileged verification and the latter is trusted exact-head admission. `PR Gate` is deterministic and author-actionable, but it does not meet the sub-5-minute bar because its whole-solution warnings-as-errors build dominates runtime. That is accepted deliberately, because the only cheaper option is to gate no code verification and the only stricter option is the full CI matrix (every server-test shard in `.github/ci-shards.json` plus the AOT/Docker/browser/MCP/provider lanes) whose per-PR fan-out caused the 2026-06-18 runner-starvation spiral. Judge it on **runner count (one, service-free)**, not wall-clock. `Review Gate` is a short GitHub-evidence evaluation and adds no build runner. Any further PR-blocking check still owes the sub-5-minute justification above and should first be considered inside an existing required workflow.

5. **AOT verification is full-CI only until trim debt is retired.** The `AOT Build Verification` job in `ci.yml` runs in the full scheduled/manual integration lane or on PRs explicitly labeled `ci/full`, not on routine PRs or merge-to-trunk pushes, because its current failures are not consistently fast or author-actionable within the default PR lane.

6. **The `Tier=Fast|Integration|Slow` test trait is a sub-tier inside this gate model, not a replacement for it.** ADR-0037 splits the .NET test suite by execution cost so that PRs run the `Tier=Fast` foundation tests plus a targeted subset of `server-tests` shards selected by `scripts/ci/honua-server-targeted-tests.sh`. The `targeted-shards` job emits a JSON `matrix_include` drawn from `.github/ci-shards.json` and `server-tests` consumes it via `strategy.matrix.include: fromJson(...)`, so unselected shards never instantiate a runner. The shared shard runner composes `&Tier!=Slow&Tier!=Fast` onto the matrix filter so Slow-tagged tests inside a shard's namespace are skipped and Fast tests run only once in the foundation lane; it also emits heartbeat/tail diagnostics over normal-verbosity test logs, writes timing artifacts, and enforces the inner `test_timeout_minutes` cap before the job-level timeout cancels the runner. `Tier=Slow&Category=Emulator` runs nightly via `nightly-slow-tier.yml`, and `flaky-detection.yml` re-runs a bounded rotating window of the same `.github/ci-shards.json` shards several times a night for flake reporting (the whole shard set is covered every ceil(shards / shard_count) days). The Scale/Cloud/External slow subfamilies need dedicated workflows once their fixtures are wired up. The five-tier PR/nightly/release/deploy/maintenance gate model above still defines *where* a workflow lives; the trait defines *which subset of tests* runs inside the workflow.

## PR Lane (Required Checks)

These workflows are merge-blocking for all PRs to trunk:

| Workflow | What it validates | Path filter |
|---|---|---|
| `pr-gate.yml` | **Required verification context `PR Gate`.** Admission tier: every tracked text blob must decode as UTF-8 (#3321). Then one runner: affected-scope warnings-as-errors build, solution-wide `dotnet format --verify-no-changes`, `Tier=Fast` unit smoke, architecture-enforcement tests (incl. the `feature-catalog.json` drift guard). Shares its steps with `ci.yml`'s `Merge Queue Gate` via `.github/actions/lean-gate`; the only input that differs is `build-scope` (`affected` here, `full` there — see [Affected-scope gate build](#affected-scope-gate-build)). | None — a required context must never be path-filtered, or non-matching PRs block forever waiting for a status that will not report |
| `review-gate.yml` | **Required admission context `Review Gate`.** Immutable default-branch workflow policy publishes exact-head review evidence from an attesting reviewer (Codex or Claude — see [Attesting reviewers](#attesting-reviewers)) and, in enforce mode, releases one admitted PR Gate rerun. In observe mode it retains a SHA-bound decision receipt for the read-only `review-first-evidence-ledger.yml`; the ledger reports but cannot promote. Review events arrive through the read-only `review-event-bridge.yml`; no PR-authored workflow has status or Actions write authority. | None — it must publish on every PR head |
| `ci.yml` | **No `pull_request` trigger** (deliberate — see the header comment). PR template compliance, CI router validation, build, .NET foundation tests, targeted server-test shards, architecture gate, JavaScript typecheck, baseline Postgres compatibility all run on the train's `train/batch/*` `workflow_dispatch` and the nightly schedule. Its aggregator context `CI Gate` is produced by the batch CI only and never appears on a PR. | n/a — not PR-triggered |
| `openapi-contract-governance.yml` | OpenAPI spec stability | `src/**/api-specs/**`, `*.openapi.*` |
| `control-plane-sdk-governance.yml` | Control plane SDK governance | SDK/control-plane paths |
| `import-fidelity-scorecard-governance.yml` | Import-fidelity baseline stability + perf-parity gate smoke test (pass/fail fixtures) | Parity/baseline/perf-budget asset paths |
| `docs-link-gate.yml` | Relative links **and** `#fragment` anchors across `docs/**/*.md` (GitHub slug algorithm, ported from `geospatial-mcp`'s `tools/check_links.py`), plus `scripts/ci/code-referenced-anchors.v1.json` — the absolute `docs.honua.io` URLs product code and shipped config hand to an operator or an agent at runtime (`remediationRef`, SCIM `documentationUri`, Prometheus `runbook_url`). A URL served only through a `.gitbook.yaml` redirect warns; a `docs.honua.io` URL in a scanned source root that the manifest does not list is an error; a `pendingPr` entry is warn-until-present so an anchor introduced by an open PR is enforced only once it lands. Pre-existing rot lives in `scripts/ci/doc-link-rot-allowlist.v1.json` with a stale-entry ratchet. Stdlib Python, ~15s, no external dependencies. | `docs/**`, `scripts/ci/check-doc-links.py`, `scripts/ci/check-doc-links.test.py`, the workflow itself |

### Affected-scope gate build

`PR Gate` is the only gate a change must clear before a human looks at it, so its
wall-clock is the tax on every PR in the repo. On the 2026-08-26 baseline the
shared lean gate was 18m09s, split roughly:

| Step | Duration |
|---|---:|
| Build (warnings as errors), full solution | 6m30s |
| `dotnet format Honua.sln --verify-no-changes` | 5m51s |
| Server Fast tier | 2m05s |
| Architecture tests | 1m31s |
| Governance/Drift (Testcontainers PostGIS, warm) | 1m15s |
| Fixtures, policy checks, restore | ~1m |

Two of those are addressed here.

**Build scope.** `.github/actions/lean-gate` takes a `build-scope` input.
`PR Gate` passes `affected`; `Merge Queue Gate` keeps `full`.
`scripts/ci/compute-lean-gate-build-scope.sh` writes a solution filter (`.slnf`)
holding the union of

1. `scripts/ci/compute-affected-projects.sh` — the changed projects plus their
   reverse-dependency (consumer) closure, and
2. the leaf test projects the gate's own `dotnet test --no-build` steps execute
   (`Honua.Server.Tests`, `Honua.Architecture.Tests`, `Honua.Ai.Tests`), which
   must be compiled even for a docs-only diff.

MSBuild resolves each listed project's `<ProjectReference>` graph itself, so
forward dependencies are built without being enumerated. A filter is used rather
than a project list because `dotnet build a.csproj b.csproj` is rejected outright
(MSB1008); the filter also keeps the build to a single MSBuild invocation with
full cross-project parallelism.

The floor is the forward closure of those three test projects: 53 of the 83
`.csproj` in the tree. The ~30 projects a narrowed gate stops compiling are
sibling test assemblies (`Honua.Core.Tests`, the provider test projects,
`Honua.LoadTests`, …) plus `Honua.AppHost`, `Honua.Analyzers`,
`Honua.ControlPlane.Lambda`, the geoprocessing CLI and the custom-code harness —
none of which any step of this gate consumes. **Expect a meaningful but bounded
saving, not a step change**: the gate still compiles `Honua.Server` and its whole
dependency graph on every PR, because its own tests need them.

*Miss-risk contract.* Anything that could invalidate the closure forces a full
build: `compute-affected-projects.sh` force-fulls on `Directory.*.props`,
`Honua.sln`, `global.json`, `NuGet.config`, `.github/` and `scripts/ci/`, and the
scope script force-fulls on any event without a trustworthy diff base, any
checkout too shallow to hold the base commit, and any failure of the underlying
script. A residual miss is caught by the merge train's full-solution build
**before the change lands** — it is fixed forward from the train and never
reaches trunk unverified. This is why `Merge Queue Gate` and the train's `build`
job must stay full-solution; do not "align" them with `PR Gate`.

`pr-gate.yml` must check out with `fetch-depth: 2`. The checked-out ref is
`refs/pull/N/merge` and its first parent is the current `trunk` tip, which is the
diff base. At the default depth 1 those parent commits are absent, the scope
computation force-fulls, and the optimization silently never fires.

**Format scope.** `dotnet format` is deliberately left solution-wide even though
it is co-dominant with the build. It takes exactly one workspace — a project or a
solution, never a list — and most of its cost is loading that workspace rather
than analysing documents, so narrowing it is a separate, measured change rather
than a free win.

**PostGIS pre-pull.** `scripts/ci/prepull-testcontainers-postgis.sh` starts a
background `docker pull` of the image `Honua.TestKit/PostgresFixture.cs` names,
as the gate's first step, and a later step waits for it immediately before the
Governance/Drift test. That step is ~1.5 min warm and ~3-4 min cold, and the
delta is almost entirely the pull; overlapping it with the fixtures/restore/build
makes the cold case look like the warm one. The tag is read out of the fixture
rather than hardcoded, so bumping `PostgresFixture` cannot leave CI warming a
stale image. The drift test itself does not move (#2882). Every failure path in
the script is a no-op — composite steps cannot set `continue-on-error`, so it
must never be able to fail the required gate.

### Import-fidelity gates: correctness and performance

The migration-reconciliation import-fidelity suite (`GeoservicesImportFidelityIntegrationTests`) emits a scorecard that
carries two independent gate lanes per sampled operation:

- **Correctness gate** — the `Checks[]` array (query/geometry/statistics/error-shape parity, etc.) is
  gated by `scripts/ci/check-import-fidelity-scorecard-regression.sh` against
  `tests/dotnet/Honua.Server.Tests/Import/import-fidelity-scorecard-baseline.json` (any `pass`→`fail` flips the
  gate).
- **Performance gate (issue #1249)** — the suite already measures Honua-vs-source p95/p99 latency
  ratios; `GeoServicesPerfParityGate` (in `Honua.Core`) grades those ratios against a configurable
  `PerfParityBudget` and embeds a Pass/Warn/Fail `PerfParity` verdict into each scorecard case.
  `scripts/ci/check-import-fidelity-perf-budget.sh` enforces that verdict (and re-derives it from the raw
  ratios) against `tests/dotnet/Honua.Server.Tests/Import/import-fidelity-perf-budget.json`, failing the gate
  when latency regresses past the fail budget (default: p95 ≥ 2.0×, p99 ≥ 2.5× the source). Thresholds
  are configurable via the budget JSON, the `HONUA_PERF_PARITY_*` env vars (CI), or the
  `HONUA_TEST_PERF_PARITY_*` env vars (the integration suite). The gating logic, verdict emission, and
  scorecard shape are covered offline by `GeoServicesPerfParityGateTests` and smoke-tested on every PR
  via pass/fail fixtures under `scripts/ci/fixtures/`; the live latency measurement runs in
  `geoservices-import-fidelity-external.yml` (on-demand).

## Nightly Lane

These workflows run on schedule and can be dispatched manually:

| Workflow | Schedule | What it validates |
|---|---|---|
| `cite-conformance.yml` | Mon 6am UTC | OGC CITE Features conformance |
| `cite-tiles-conformance.yml` | Tue 6am UTC | OGC API Tiles CITE conformance |
| `cite-wfs20-conformance.yml` | Mon 3am UTC | WFS 2.0 CITE conformance |
| `cite-wms-conformance.yml` | Wed 6am UTC | OGC WMS CITE conformance |
| `cite-wmts-conformance.yml` | Thu 6am UTC | OGC WMTS CITE conformance |
| `ogc-maps-conformance.yml` | Fri 6am UTC | OGC API Maps conformance |
| `cite-kml22-conformance.yml` | Fri 3am UTC | OGC KML 2.2 CITE conformance |
| `cite-gml32-conformance.yml` | Sat 6am UTC | OGC GML 3.2 CITE conformance |
| `cite-gpkg12-conformance.yml` | Sat 3am UTC | OGC GeoPackage 1.2 CITE conformance |
| `geoservices-import-fidelity-external.yml` | On-demand (`workflow_dispatch`) | External GeoServices parity + Geoportal import vs live Esri services: runs the correctness regression gate **and** the perf-parity latency gate (issue #1249) over the freshly measured scorecard (deterministic parity stays in Import Fidelity Scorecard Governance); also runs `GeoservicesGeoportalImportIntegrationTests` |
| `routing-nightly.yml` | Weekly Sun 5:00am UTC | pgRouting provider + NAServer routing integration (`Category=Routing`); `PgRoutingFixture` manages its own Testcontainers `pgrouting/pgrouting` image |
| `warehouse-nightly.yml` | Weekly Sun 6:00am UTC | Snowflake/Redshift/Databricks/SqlServer creds-gated provider tests; self-skips cleanly without configured secrets, surfacing pass/fail/skip counts in the run summary |
| `cross-server-consume-nightly.yml` | Daily 7:00am UTC | Honua-as-client WMS/WFS/WMTS reads against GeoServer and MapServer reference containers |
| `client-compat-smoke-nightly.yml` | Daily 7:15am UTC | Full CERT-\* matrix certification (18 test cases × 4 protocol lanes) with `.cert.json` envelopes + reusable evidence pack |
| `pyqgis-client-compat-nightly.yml` | Daily 7:30am UTC | PyQGIS desktop client compatibility (OGC Features + WFS) with per-protocol `.cert.json` envelopes |
| `sdk-server-compatibility.yml` | Monday 8:35am UTC | Manifest-driven last-3 server refs x last-3 SDK sets compatibility matrix through `honua-sdk-js`, `honua-sdk-python`, and `honua-sdk-dotnet`; manual dispatch can pin `server_current_ref` for release-candidate evidence; copies checked-out SDK refs to `$RUNNER_TEMP/sdk-compat` before smoke execution so server repo build policy does not affect SDK source builds; records SDK versions, server commit/image field, seed profile, exercised surfaces, migration automation surface status, and diagnostics; publishes the `sdk-compatibility-matrix-<run-id>` table artifact and fails on supported-cell regressions |
| `client-interop-nightly.yml` | Daily 7:00am UTC | Real-client interop matrix via Docker harnesses (`gdal`, `pyqgis`, `openlayers`, `cesium`, `arcgis-stub`); diffs per-lane `.cert.json` envelopes against `tests/baselines/client-compat/` plus `expected-pairs.json`, refreshes `docs/gis/gap-report.md`, and fails strict mode on baseline pass regressions, missing current envelopes, missing committed baselines, or new unbaselined failures. Non-blocking until 30 consecutive nightly passes (#806) |
| `gdal-driver-e2e.yml` | Daily 7:45am UTC | GDAL `ogrinfo` + `ogr2ogr` round-trip against honua-server (ADR-0034 stand-in until `honua-gdal` plugin ships) |
| `load-soak-nightly.yml` | Scheduled | Load and soak testing |
| `nightly-slow-tier.yml` | Daily 4am UTC | `Tier=Slow&Category=Emulator` .NET tests across `Honua.Server.Tests`, `Honua.Db.Postgres.Tests`, `Honua.Core.Tests` (`[EmulatorTest]` only). LocalStack and Azurite are provisioned by `EmulatorFixture` (Testcontainers) and Postgres by a service container; the workflow asserts `HONUA_TEST_DB_URL` before dispatch. `[ScaleTest]`, `[ExternalServiceTest]`, `[CloudTest]` need dedicated fixtures and are tracked as separate workflows (ADR-0037) |
| `flaky-detection.yml` | Daily 5am UTC | Re-runs a bounded rotating window of `.github/ci-shards.json` shards (default 6/night, 2 iterations each, so the whole set is covered every `ceil(shards / shard_count)` days) and uploads a per-shard flake-candidate report. Each iteration gets a fresh database. An ad-hoc `project`/`filter` dispatch bypasses the shard plan. Reporting only — it never fails on a candidate, but it DOES fail on missing evidence (ADR-0037) |
| `security-nightly.yml` | Daily 2am UTC | NuGet vulnerability scan, Trivy filesystem scan, and container security validation |
| `nightly-container-build.yml` | Scheduled | Container build validation |
| `codeql.yml` | Mon 0am UTC | CodeQL security analysis |

## Release Lane

| Workflow | Trigger | What it does |
|---|---|---|
| `nuget-publish.yml` | Push (tags) / manual | NuGet package publishing |
| `control-plane-sdk-governance.yml` (release) | Release event | SDK release certification |

## Deploy Lane

| Workflow | Trigger | What it does |
|---|---|---|
| `deploy.yml` | Tags / manual | Environment promotion |
| `deploy-platform-images.yml` | Tags / manual | Platform image deployment |
| `cloud-post-apply-validation.yml` | Workflow call / manual | Post-deploy validation |

## Maintenance Lane

| Workflow | Trigger | What it does |
|---|---|---|
| `release-please.yml` (SDK repos only) | Push | Version automation |

## Attesting reviewers

`Review Gate` goes green only on exact-head review evidence from a bot identity
distinct from the PR author. `scripts/ci/review-gate-evidence.js` owns that
identity set and is the single source of truth: it exports `ATTESTING_LOGINS`,
`scripts/ci/merge-train/select.sh` reads it via `--print-logins`, and the
`.github/workflows/claude-review.yml` lane reads its body template through
`scripts/ci/claude-review-body.js`. Nothing else may restate a login or a
marker — including this document, which describes the registry rather than
duplicating it.

Two identities are accepted today: **Codex** (`chatgpt-codex-connector`) and
**Claude** (`claude`). Either alone can attest, which is the point: when Codex is
rate-limited ("You have reached your Codex usage limits for code reviews") the
gate would otherwise deadlock and nothing could land. GitHub Copilot code review
is deliberately **not** accepted; the reasoning is in the block comment in
`review-gate-evidence.js`.

### Identity spelling is load-bearing

Evidence reaches the evaluator in two shapes that spell bot logins differently:
REST reports `claude[bot]`, GraphQL reports `claude`. **The gate reads GraphQL**
(`review-gate-snapshot.js`), so the suffix-less spelling is the one that appears
in production — a registry that accepted only `claude[bot]` would make every
review the lane posts invisible to the gate.

`claude` is also a real GitHub User, and a User can hold a PAT, so it is accepted
only when GitHub additionally types the author as a `Bot`. That is why every
`author` selection in the snapshot query carries `__typename`, and why
`reviewerFor(login, typename)` takes two arguments. Dropping `__typename` from
the query silently disables the Claude identity.

### The Claude lane

`.github/workflows/claude-review.yml` produces the `claude` half.

**It never runs from the pull request it reviews.** It triggers on `workflow_run`
(completed `PR Gate`) and on `issue_comment`, both of which execute the
default-branch copy, so the workflow, the prompt and the `CLAUDE.md` the reviewer
follows all come from trunk. A `pull_request` trigger would hand a candidate its
own reviewer definition plus the secrets and `id-token: write` — enough to attest
to itself and to exfiltrate the credentials. `review-gate.yml` uses
`pull_request_target` + `github.workflow_sha` for the same reason. The PR is
resolved through `scripts/ci/trusted-pr-workflow-run.js`, which binds the PR and
its exact head to the immutable GitHub-managed check association and fails closed
once the PR has moved. The PR tree is checked out into `pr/` as data, with its
`CLAUDE.md` / `AGENTS.md` / `.claude` / `.github` deleted first so a candidate
cannot smuggle reviewer instructions through a nested memory file. The diff is
still untrusted text; the tool allowlist is the control that contains it.

**It cannot merge, push, or edit a workflow.** The action authenticates as the
Claude GitHub App, whose org installation carries write scopes, so the reviewer
gets read-only analysis tools plus exactly two append-only publishing tools
(`gh pr comment` and inline review comments) — no `gh api`, no general Bash, no
git. `validate-single-merge-authority.sh` cannot see runtime API calls, so this
allowlist is the control, and `scripts/ci/claude-review-lane.test.js` asserts it.
The workflow's own `GITHUB_TOKEN` stays read-only.

**It only ever posts comments — never a review verdict.** A
`CHANGES_REQUESTED` is identity-scoped and is cleared only by a *newer positive
from the same identity*; an optional reviewer that later stops running (secret
revoked, App uninstalled, API outage) would leave such a PR blocked permanently,
with no operator override short of editing the evaluator on trunk. So:

- no blocking findings → one comment carrying the generated clean body, which
  attests through the clean-comment path;
- blocking findings → inline review comments only. Those become review threads
  and hold the gate red through `unresolvedCount` until a human resolves them.

Note the honest limitation, which the lane shares with Codex: `unresolvedCount`
is **head-scoped** — a thread is counted only while its comment sits on the
current head, so findings threads drop out of the gate's view on the next push
and the new head is judged on its own evidence.

A `@claude review` comment re-runs the lane; it is gated on a human commenter
with `OWNER`/`MEMBER`/`COLLABORATOR` association, because the action hard-fails
its own permission check otherwise and would turn a courtesy trigger into a red
check. Re-review is suppressed only by an existing clean attestation *for the
same head*, so a prior findings run never blocks a re-request.

**Enabling the lane (repository owner only).** Add **one** repository secret:

- `CLAUDE_CODE_OAUTH_TOKEN` — run `claude setup-token` locally and paste the
  token (subscription billing); or
- `ANTHROPIC_API_KEY` — an API key from console.anthropic.com (usage billing).

Until one exists the job exits 0 with a `::notice::` naming the secrets; it never
fails a PR. The `claude` GitHub App must stay installed on the org with
`pull_requests: write`. The lane is **not** merge-blocking itself and publishes
no check context — it only feeds evidence to `Review Gate` — but it does
consume one runner per PR-Gate completion, which is why it is documented here
rather than treated as a silent addition (rule 4).

## Adding New Checks

Before adding a new workflow or check:

1. **Identify the tier.** Default to nightly unless the check meets PR-lane criteria.
2. **PR-lane criteria:** deterministic, < 5 min, author-actionable, no external dependencies.
3. **Document the new check** in this file and in `workflow-inventory.md`.
4. **Use path filters** for PR-lane checks that only apply to specific code areas.
5. **Follow artifact conventions** from `config-conventions.md`.
