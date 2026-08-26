# CI Workflow Inventory

> Canonical inventory of **every** workflow in `.github/workflows/` in this
> repository (77 files). Other Honua repositories keep their own inventories;
> this page no longer mirrors the SDK repos, because a copy here could not be
> verified against their trees and had already drifted.
>
> Last updated: 2026-08-17.
>
> To re-derive the file/name/trigger columns after adding or removing a
> workflow:
>
> ```bash
> python3 - <<'PY'
> import pathlib, yaml
> for f in sorted(pathlib.Path('.github/workflows').glob('*.yml')):
>     d = yaml.safe_load(f.read_text())
>     on = d.get(True, d.get('on'))
>     triggers = on if isinstance(on, list) else ([on] if isinstance(on, str) else list(on))
>     print(f"{f.name} | {d.get('name','')} | {', '.join(triggers)}")
> PY
> ```

> **Completeness is enforced.** `scripts/ci/fixtures/validate-review-first-dispatch.py`
> (run by `scripts/ci/validate-ci-router.sh`) fails if any file in
> `.github/workflows/` has no row below. Adding a workflow means adding a row;
> resolving a conflict in these tables means keeping every row.

**Branch protection requires `PR Gate` and `Review Gate` together**: unprivileged
verification plus trusted exact-head admission. `CI Gate` remains train-only and
is deliberately not a per-PR required context. Nothing else in this inventory is
a required context — the merge train selects on `mergeable`, not on
`mergeStateStatus`, so a red advisory check does not by itself block landing.
PR-time CodeQL security analysis comes from `codeql.yml`'s own
`pull_request` lane: code-scanning **default setup is `not-configured`** for this
repository, and every entry in `/code-scanning/analyses` carries
`analysis_key: .github/workflows/codeql.yml:analyze`. The separate
`dynamic/github-code-scanning/codeql` runs titled `Code Quality: PR #N` are
GitHub **Code Quality**, a different product that publishes no security
analysis.

## Required PR lane

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `pr-gate.yml` | PR Gate | `pull_request` (base `trunk`), `workflow_dispatch` | Required verification context (#2865). Admission tier first: a ~3-second sweep asserting every tracked **text** blob decodes as UTF-8 (`validate-tracked-file-encoding.py`, #3321) -- binary fixtures are excluded by git's own text/binary classification rather than an extension denylist, except that auto-detected binaries are still screened for a UTF-16/UTF-32 byte-order mark, since NUL-dense Unicode text would otherwise pass as binary. A `.gitattributes` declaration remains the escape hatch. Then one service-free ubuntu-latest runner: whole-solution warnings-as-errors build, `dotnet format --verify-no-changes`, `Tier=Fast` smoke, architecture enforcement, and the Server governance/drift tests (one ephemeral Testcontainers Postgres for the `EndpointRegistry` drift check, #2882). Deliberately un-path-filtered. In review-first enforce mode, attempt 1 stops before the expensive steps and the trusted reviewer releases attempt 2 exactly once. When `HONUA_PR_GATE_BUILD_REUSE_SHADOW=true`, a best-effort post-gate step packages at most two repeated registered test projects from that already-paid Release build; packaging/upload is non-authoritative and any failure is a cache miss. Shares its steps with `ci.yml`'s `Merge Queue Gate` via `.github/actions/lean-gate`. |
| `review-gate.yml` | Review Gate Attestation | `pull_request_target`, `issue_comment`, trusted `repository_dispatch`, `workflow_run` [PR Gate, Review Event Bridge] | Required admission context. Publishes `Review Gate` on the exact current head only when Codex has exact-head evidence and no unresolved Codex threads. Serializes every event by resolved PR number, pins the exact trusted workflow-policy SHA, and is the only authority allowed to release expensive verification. In observe mode it retains an immutable decision receipt; merge-train selection and pre-land independently re-attest source evidence. |
| `claude-review.yml` | Claude Review | completed `PR Gate` `workflow_run`, `issue_comment` containing `@claude review` | Second attesting reviewer for the required `Review Gate` context (#3213, #3314; rebuilt on a trusted trigger by #3341), so a PR can still land while Codex is rate-limited. Trusted default-branch lane: **never** triggered by `pull_request`, read-only `GITHUB_TOKEN`, and it posts comments and inline threads only — never a review verdict. `scripts/ci/review-gate-evidence.js` accepts `claude` evidence alongside Codex. Inert until an auth secret exists. See [gate-model.md → Attesting reviewers](gate-model.md#attesting-reviewers). |
| `review-event-bridge.yml` | Review Event Bridge | `pull_request_review`, `pull_request_review_comment` | Best-effort latency hint only. GitHub runs these event workflows from the PR merge branch, so the bridge is credential-free/no-checkout and is never trusted for invalidation or landing. |

## Merge and landing

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `merge-train.yml` | Merge Train | `schedule` (`*/15`, dry-run), `workflow_dispatch` | **Sole merge authority** (ADR-0055). Requires exact-head `PR Gate` + `Review Gate` at selection and again immediately before the compare-and-swap land. Automatic triggers are dry-run-only; landing needs an explicit `train_apply=true` dispatch. With the build-reuse shadow enabled, only an exact one-member batch may carry the canonical successful PR Gate run/attempt/PR/head identity into Smart CI. `scripts/ci/validate-single-merge-authority.sh` proves no second workflow can merge. |
| `merge-train-rerun-recovery.yml` | Merge Train Rerun Recovery | `workflow_run` [CI] | Resumes the active immutable batch when a failed batch CI is rerun green. Uses its **own** per-source-run concurrency group (sharing the train's group made GitHub evict queued recoveries — 12 of 15 consecutive runs were `cancelled`); exclusion against a live train is re-established by an explicit idle wait plus the durable Merge Train State issue. See `docs/internal/contributor/merge-coordination-runbook.md`. |
| `ci.yml` | CI | `schedule` (09:00 UTC), `merge_group`, `workflow_dispatch` | No `pull_request` trigger. Its `CI Gate` context is produced only by the train's `train/batch/*` dispatch, so it never appears on a PR head SHA (#2865). Core build, test, architecture gate, CI-router validation, JS typecheck, and Postgres compatibility. Per ADR-0037 the `targeted-shards` job runs `scripts/ci/honua-server-targeted-tests.sh` and emits a JSON `matrix_include` drawn from `.github/ci-shards.json`; `server-tests` consumes it via `strategy.matrix.include: fromJson(...)`, so unselected shards never instantiate a runner. The full shard matrix runs on scheduled/manual full integration runs and PRs labeled `ci/full`. Separately from that shadow, `server-tests` shards opportunistically materialize their own run-scoped exact-head binary payload on attempt 1 (one designated writer per project publishes; siblings do a single fail-open lookup and never wait), rolled back with the repository variable `HONUA_SERVER_TEST_ATTEMPT1_REUSE=false` — contract in `docs/internal/ci/server-test-binary-artifacts.md`. `scripts/ci/run-server-test-shard.sh` composes each shard filter as `(matrix.filter)&Tier!=Slow&Tier!=Fast`, emits heartbeat/tail diagnostics, writes `.timing.json`, and enforces the inner `test_timeout_minutes` cap before the job-level `timeout_minutes` cancels the runner. The `merge_group` event runs only the lean `Merge Queue Gate` (the queue itself is disabled, ruleset 17808547). `pr-template-check` and `pr-readiness` short-circuit to success on every non-`pull_request` event, so today they are no-op roll-ups into `CI Gate`. |

## PR-triggered (advisory, not required)

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `openapi-contract-governance.yml` | OpenAPI Contract Governance | `pull_request`, `workflow_dispatch` | Path-scoped to the API surface; enforces the breaking-change policy (`OPENAPI_ALLOW_BREAKING_CHANGES` is the deliberate escape). |
| `openapi-drift.yml` | OpenAPI Drift Check | `pull_request`, `workflow_dispatch` | Regenerates the OpenAPI document and fails on drift from the committed contract. |
| `control-plane-sdk-governance.yml` | Control Plane SDK Governance | `pull_request`, `workflow_dispatch`, `release` | PR governance for the control-plane SDK surface, separate from release publishing. |
| `import-fidelity-scorecard-governance.yml` | Import Fidelity Scorecard Governance | `pull_request`, `workflow_dispatch` | Path-scoped to parity/baseline/perf-budget assets; smoke-tests the perf-parity gate (#1249) with pass/fail fixtures. |
| `capability-impact-comparison.yml` | Capability Impact Comparison | `pull_request`, `workflow_dispatch` | Capability-graph completeness plus a report-only comparison of ADR-0037 shard routing against the capability selector. |
| `capability-matrix-aggregation.yml` | Capability Matrix Aggregation | `pull_request`, `push` (trunk), weekly `schedule`, `workflow_dispatch` | Joins `feature-catalog.json`, `docs/cite-status.md`, the GeoServices REST parity index, capability crosswalks, and committed client-compat envelopes into `docs/gis/data/capability-matrix.v1.json` (#2892/#2893). |
| `serving-image-boundary.yml` | Serving Image Boundary | `pull_request` (base `trunk`, **image-defining paths only**), `workflow_dispatch` | Builds and boundary-verifies the generic, Lambda, and Azure Functions Native-AOT serving images for the exact head. Since #3204 the trigger carries only inputs that DEFINE the image (the three AOT Dockerfiles, `docker/cloud/azure-functions/**`, `.dockerignore`, the in-image restore helper, the boundary verifier and its fixture harness, this workflow); managed source (`src/**`, `eng/**`, solution, build props) is deliberately not a trigger and is placed on the lanes in the table below. The trigger paths and the in-workflow variant `case` arms are parsed and cross-checked by `scripts/ci/native-image-impact.py`, which fails closed on drift from `.github/native-image-impact.json`. Deliberately isolated from the required lean `PR Gate`. |
| `worker-gdal-image.yml` | GDAL Worker Image | `pull_request` (base `trunk`, path-filtered), weekly `schedule`, `workflow_dispatch` | Builds the GDAL worker image, smokes the entrypoint, and enforces Trivy vulnerability policy for the exact head; publishes SARIF. Re-proved on the nightly security and release/deploy lanes. |
| `geoarrow-interop-fixture.yml` | GeoArrow Interop Fixture | `pull_request`, `workflow_dispatch` | Produces the GeoArrow 0.2 interop fixture. |
| `normalize-derived-artifacts.yml` | Derived Artifact Normalization | `pull_request` | Untrusted producer: may execute PR code but can only read the repo/packages and upload a bounded data artifact (#3219). |
| `release-bundle-tooling.yml` | Release Bundle Tooling | `pull_request`, `push` (trunk), `workflow_dispatch` | Verifies the deterministic, locally-runnable core of the release-bundle orchestrator (manifest generator, evidence collector, dispatch helper, suite registry). |
| `issue-capability-check.yml` | Issue Capability Key Check | `issues` | Advisory comment when a bug/feature issue's capability key is missing or unrecognized (#2896). Never labels or fails. |

`codeql.yml` also runs on a path-filtered `pull_request`; it is listed under
*Nightly and scheduled test lanes* because the same file owns the weekly deep
scan.

### Native-image evidence placement and measured savings (#3204)

Measured on trunk for the 2026-08-13 -> 2026-08-17 window (100 most recent runs
per workflow, plus 74 distinct trusted observation receipts):

| Signal | Serving Image Boundary | GDAL Worker Image |
|---|---:|---:|
| Runs sampled | 100 | 100 |
| Successful | 19 | 22 |
| Cancelled by a newer push | 80 | 78 |
| Median successful wall time | 140 min | 18 min |
| Total successful wall time in window | 2667 min | 405 min |
| Median cancelled wall time | 0.7 min | 0.7 min |

Cancellation is already cheap: `concurrency: cancel-in-progress` stops a
superseded run at a median of 0.7 minutes, so the 80/78 cancelled runs are not
where the money goes. The cost is the ~2670 serving minutes and ~405 worker
minutes actually spent on completed builds, at roughly 785 and 119 runner
minutes per day.

Path routing cannot recover any of it. Across all 74 distinct observed heads
the graph-derived candidate selected exactly the same images and the same
serving variants as the legacy path filters: 40/74 serving-impacted and 35/74
worker-impacted under both policies, with zero narrowed, zero avoided, and
zero candidate-only heads. Only 6 of 33 `src/**` projects sit outside the
serving closure, so the theoretical avoidance ceiling is small and the observed
rate is zero.

Repeat pushes are where the money goes. Grouping the impacted heads by their
selected image-input set shows 24 of 40 serving-impacted heads (60%) and 25 of
35 worker-impacted heads (71%) repeat an input set already built on the same
pull request; one review-heavy pull request contributed 21 serving builds
across 3 distinct input sets. The independent run sample agrees: 19 successful
serving runs across 11 branches and 22 successful worker runs across 10
branches.

The worker figure is a build-time bound only: the GDAL worker's Trivy scan is
enforcing and its verdict depends on the vulnerability database at scan time, so
it is re-run on every head and is never reusable.

#### Serving-image evidence placement moved (2026-08-25)

The two findings above are the same fact seen from opposite ends. A
graph-derived *router* cannot narrow this trigger, because `src/**` genuinely is
in the serving closure — and that is also why 60% of serving-impacted heads
rebuild an input set already built on the same pull request: the trigger is
keyed on managed source, which changes on essentially every review-fix push and
invalidates all three variants at once. Routing accuracy was never the lever;
*placement* is.

`serving-image-boundary.yml` therefore now fires only on inputs that DEFINE the
image — the three production AOT Dockerfiles, `docker/cloud/azure-functions/**`,
`.dockerignore`, the in-image restore helper, the boundary verifier and its
fixture harness, and the workflow itself. Managed source keeps its evidence, on
lanes that already existed or were extended here:

| Risk class | Proved by | When |
|---|---|---|
| Native-AOT compile (`src/**`, `eng/**`, build props, solution) | `ci.yml` `aot-build` | pre-merge, on the batch that lands |
| Boundary detector correctness (all clean and injected-rootfs fixtures) | `pr-gate.yml` | every push on every pull request |
| Final rootfs — generic AOT, Lambda AOT | `nightly-container-build.yml` | daily, on the exact digest, before its manifest publishes |
| Final rootfs — Azure Functions AOT | `nightly-container-build.yml` `verify-functions-aot` (added with this change) | daily, verification only; publishes nothing |
| Final rootfs — every published variant | `deploy.yml`, `deploy-platform-images.yml`, `release-bundle.yml` | on the exact digest, before promotion |

The Azure Functions row is the one that had to be added: before this change the
only lane that BUILT that variant post-merge was `deploy-platform-images.yml`
(release tags plus one weekly scan-only schedule), so deferring source-driven
runs without it would have widened that variant's detection window from a push
to a week. #3204's warning that deleting the PR triggers would trade cost for
delayed defects is honoured by keeping compile risk pre-merge, detector risk
per-push, and rootfs risk nightly — not by removing a class of evidence.

Expected effect: the workflow fires only on pull requests that touch an
image-defining file. The baseline sample does not break its 40 serving-impacted
heads down by trigger class, so the ~785 completed-build runner-minutes per day
is the **ceiling** on the saving, not a prediction of it; AC#7's post-change
30-run comparison must measure the realised figure. Note also that the
observation receipt's cohorts invert: managed-source heads now report
`serving_candidate_only`, because the graph-derived candidate would re-add the
per-push builds this change removes. Promoting that candidate router as written
would undo the narrowing, so its promotion criteria need restating before any
enforcement decision.

Exact-input build reuse (follow-on #3) is still worth doing — it is what would
recover the remaining repeat-push cost on the GDAL worker lane, whose Trivy
verdict depends on the vulnerability database at scan time and is never
reusable. The observation receipt already carries per-image content digests over
the merge tree the images are actually built from, so reuse eligibility is
measured before anything is enforced; see `native-image-impact-routing.md`.

## Trusted observers and evidence ledgers (read-only)

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `pr-gate-impact-observe.yml` | PR Gate Impact Observation | `workflow_run` [PR Gate], `workflow_dispatch` | Trusted default-branch, read-only classification of the exact gate-time diff. Retains bounded docs-only/full receipts and validates PR Gate build metadata plus exact payload artifact identity without downloading or executing the payload. |
| `native-image-impact-observe.yml` | Native Image Impact Observation | `workflow_run` [PR Gate], `workflow_dispatch` | Read-only comparison of graph-derived image inputs with legacy path triggers. The Serving/GDAL image workflows stay authoritative in observe mode. |
| `server-test-prebuild-observe.yml` | Server Test Prebuild Observation | `workflow_run` [Review Gate Attestation] (`branches-ignore: trunk`), `workflow_dispatch` | Trusted default-branch read-only shadow producer for #3226. Only `pull_request_target` Review Gate runs are usable, and those are the only ones whose run record carries a PR head branch — hence the `branches-ignore` filter, which drops the `issue_comment`/bridge runs that used to materialise as skipped runs. |
| `server-test-prebuild-parity.yml` | Server Test Prebuild Parity Observation | `workflow_run` [PR Gate], `workflow_dispatch` | Read-only post-verification shadow: does one already-ready exact prebuild produce the same bounded proof results as an independent restore/build? Publishes no status. |
| `server-test-prebuild-evidence-ledger.yml` | Server Test Prebuild Evidence Ledger | daily `schedule`, `workflow_dispatch` | Audits retained prebuild parity receipts. |
| `review-first-evidence-ledger.yml` | Review-first Evidence Ledger | daily `schedule`, `workflow_dispatch` | Read-only audit of retained Review Gate observation receipts; replays the production dispatch helper, deduplicates exact heads, separates policy cohorts, and reports promotion readiness. Cannot change mode, status, labels, runs, train state, or merge state. |
| `impact-routing-evidence-ledger.yml` | Impact Routing Evidence Ledger | daily `schedule`, `workflow_dispatch` | Read-only audit of attempt-bound PR Gate and native-image impact receipts; reconciles native decisions with successful exact-head Serving/GDAL image outcomes. |
| `normalize-derived-artifacts-consumer.yml` | Derived Artifact Normalization Consumer | `workflow_run` [Derived Artifact Normalization] | Default-branch validator for the untrusted producer's artifact. Observe mode deliberately holds no `contents: write` and no write secret. |
| `stranded-merge-detector.yml` | Stranded Merge Detector | weekly `schedule`, `workflow_dispatch`, `workflow_call` | Read-only sweep for payload that never reached the default branch (#3248, #3316). Merged PRs whose merge commit is not an ancestor are adjudicated by **content** (patch identity, then blob equality, then presence of the PR's added lines) and split into `stranded` / `edits-missing` / `superseded` / `landed` / `indeterminate`; open PRs whose base has already landed or been deleted are reported as `needs-retarget` with the `gh pr edit` remedy. Files or updates a single tracking issue only on actionable findings. Reusable via `workflow_call` (`default-branch`, `limit`, `open-limit`, `tooling-ref`); **no external consumer yet** — honua-sdk-js runs its own `scripts/stranded-merge-detector.mjs`, so those inputs are exercised only from this repo. JSON output is `schemaVersion: 2`. |

## Nightly and scheduled test lanes

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `flaky-detection.yml` | Flaky Test Detection | daily `schedule` (05:00 UTC), `workflow_dispatch` | Bounded, **incremental** flake hunt (ADR-0037). Each run takes a rotating window of `.github/ci-shards.json` shards (default 6), re-runs each shard's own filter under its own inner budget via `scripts/ci/run-server-test-shard.sh` (default 2 iterations), and reports per-shard flake candidates through `scripts/ci/summarize-flaky-detection.py`. The whole shard set is covered every `ceil(shards / shard_count)` days. Reports only; it never gates. |
| `nightly-slow-tier.yml` | Nightly Slow Tier (Emulator) | daily `schedule` (04:00 UTC), `workflow_dispatch` | `--filter "Tier=Slow&Category=Emulator"` across `Honua.Server.Tests`, `Honua.Db.Postgres.Tests`, `Honua.Core.Tests` — `[EmulatorTest]` only. LocalStack + Azurite come from `EmulatorFixture` (Testcontainers); Postgres from a service container. Asserts `HONUA_TEST_DB_URL` before dispatch. |
| `load-soak-nightly.yml` | Load/Soak Nightly | daily `schedule` (03:00 UTC), `workflow_dispatch` | Scheduled load/soak tests. |
| `security-nightly.yml` | Security Nightly | daily `schedule` (02:00 UTC), `workflow_dispatch` | Consolidated NuGet vulnerability scan, Trivy filesystem scan, and container security scan (Hadolint, Trivy, structure tests, runtime constraints). |
| `nightly-container-build.yml` | Nightly Container Build | daily `schedule` (06:00 UTC), `workflow_dispatch` | Scheduled container build. Publishes the generic AOT, Lambda AOT, and JIT images, boundary-verifying each AOT digest before its manifest. `verify-functions-aot` additionally builds and boundary-verifies the Azure Functions AOT rootfs and publishes nothing — it is the daily source-driven proof for the one production variant `deploy-platform-images.yml` alone publishes (#3204). |
| `nightly-migration-evidence.yml` | Nightly Migration Evidence Pack | daily `schedule` (07:15 UTC), `workflow_dispatch` | Drives the fixture-based GeoServer migration apply path end-to-end (#1015) and uploads the deterministic evidence pack. |
| `protocol-harness-certification.yml` | Protocol Harness Certification | daily `schedule` (10:41 UTC), `workflow_dispatch` | Executes the exact governed server integration-test roster outside PR CI. Separately checks out the producer contract and candidate source, binds a SHA-labeled immutable image identity, rejects incomplete TRX, and emits digest-bound operation receipts with exact `test_ids` for nightly/release aggregation. |
| `provider-http-smoke.yml` | Provider HTTP-Stack Smoke | daily `schedule` (06:30 UTC), `workflow_dispatch` | Interface-level smoke that boots a real host per secondary provider (DuckDB in-process; MySQL and SQL Server via Testcontainers) over FeatureServer/OGC API Features/OData/tiles, plus the gated Oracle real-database lane (#2947). |
| `cloud-integration-harness.yml` | Cloud Integration Harness | daily `schedule` (05:00 UTC), `workflow_dispatch` | Docker-backed cloud-integration tests (#2163) against emulated backends (kind, LocalStack). `Category=CloudIntegration` only; excluded from every PR run. |
| `real-aws-certification.yml` | Real AWS Certification | weekly `schedule` (Mon 06:00 UTC), `workflow_dispatch` | `Category=RealAwsCertification` against a LIVE AWS account. Gated on a maintainer OIDC role variable, budgeted, teardown-guaranteed. |
| `cross-server-consume-nightly.yml` | Cross-Server Consume Nightly | daily `schedule` (07:00 UTC), `workflow_dispatch` | Honua-as-client WMS/WFS/WMTS reads against reference GeoServer and MapServer containers; best-effort commits the refreshed gap report. |
| `client-interop-nightly.yml` | Real-Client Interop Matrix | daily `schedule` (07:00 UTC), `workflow_dispatch` | `docker/client-compat` matrix (`gdal`, `pyqgis`, `openlayers`, `cesium`, `arcgis-stub`); diffs per-lane `.cert.json` envelopes against `tests/baselines/client-compat/` and fails strict mode on any baseline regression. Promote to PR-blocking only after 30 consecutive nightly passes (#806). |
| `client-compat-smoke-nightly.yml` | Generic Client Compatibility Smoke | daily `schedule` (07:15 UTC), `workflow_dispatch` | Full CERT-\* matrix (18 cases × 4 protocol lanes) with per-protocol `.cert.json` envelopes, `overall-summary.json`, transcripts, and `pack/`. |
| `pyqgis-client-compat-nightly.yml` | PyQGIS Client Compatibility Certification | daily `schedule` (07:30 UTC), `workflow_dispatch` | PyQGIS desktop compatibility using real QGIS providers against `client-compat-v1.sql`. |
| `gdal-driver-e2e.yml` | GDAL Driver End-to-End | daily `schedule` (07:45 UTC), `workflow_dispatch` | `ogrinfo` + `ogr2ogr` against honua-server via GDAL's `OAPIF:` stand-in driver (ADR-0034). |
| `routing-nightly.yml` | Routing Nightly (pgRouting) | weekly `schedule` (Sun 05:00 UTC), `workflow_dispatch` | `Category=Routing` with `HONUA_ROUTING_TEST=1`; `PgRoutingFixture` manages its own `pgrouting/pgrouting` Testcontainers image. |
| `warehouse-nightly.yml` | Warehouse Providers Nightly (Creds-Gated) | weekly `schedule` (Sun 06:00 UTC), `workflow_dispatch` | Matrix over Snowflake/Redshift/Databricks/SqlServer test projects using optional repository secrets; a missing secret reads as "not configured", not "absent from CI". |
| `sdk-server-compatibility.yml` | SDK Server Compatibility | weekly `schedule` (Mon 08:35 UTC), `workflow_dispatch` | Manifest-driven last-3-servers × last-3-SDK-sets matrix from `docs/developer/sdk-compatibility-versions.json`; runs SDK sources from an isolated `$RUNNER_TEMP` copy and publishes `sdk-compatibility-matrix-<run-id>`. |
| `codeql.yml` | CodeQL | path-filtered `pull_request` (base `trunk`), weekly `schedule` (Mon 00:00 UTC), `workflow_dispatch` | Two lanes in one workflow, and the **only** CodeQL security analysis this repository has. On `pull_request` it uses C# `build-mode: none` extraction with the default high-precision suite to stay on the PR critical path; on the weekly schedule it performs a full instrumented build with `security-extended` to catch lower-confidence findings and dependency churn. |
| `geoservices-import-fidelity-external.yml` | GeoServices Import Fidelity (External) | `workflow_dispatch` | External parity against live Esri services; deliberately on-demand because upstream data drifts. Enforces the correctness regression gate and the perf-parity latency gate (#1249). |

## OGC CITE / conformance

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `cite-conformance-common.yml` | CITE Conformance (reusable) | `workflow_call` | Shared checkout/build/run/parse/upload/fail skeleton for single-suite CITE runs. |
| `cite-conformance.yml` | OGC CITE Conformance Tests (Features) | weekly `schedule` (Mon 06:00 UTC), `workflow_dispatch` | |
| `cite-tiles-conformance.yml` | OGC API Tiles CITE Conformance | weekly `schedule` (Tue 06:00 UTC), `workflow_dispatch` | |
| `cite-wfs20-conformance.yml` | WFS 2.0 CITE Conformance | weekly `schedule` (Mon 03:00 UTC), `workflow_dispatch` | Standalone (not the reusable wrapper) because of suite-specific setup. |
| `cite-wms-conformance.yml` | OGC WMS CITE Conformance | weekly `schedule` (Wed 06:00 UTC), `workflow_dispatch` | WMS 1.3. |
| `cite-wms11-conformance.yml` | OGC WMS 1.1.1 CITE Conformance | weekly `schedule` (Thu 06:00 UTC), `workflow_dispatch` | |
| `cite-wmts-conformance.yml` | OGC WMTS CITE Conformance | weekly `schedule` (Thu 06:00 UTC), `workflow_dispatch` | |
| `cite-wcs20-conformance.yml` | OGC WCS 2.0 CITE Conformance | weekly `schedule` (Wed 07:30 UTC), `workflow_dispatch` | |
| `cite-wps20-conformance.yml` | WPS 2.0 CITE Conformance | weekly `schedule` (Wed 07:00 UTC), `workflow_dispatch` | |
| `cite-kml22-conformance.yml` | OGC KML 2.2 CITE Conformance | weekly `schedule` (Fri 03:00 UTC), `workflow_dispatch` | |
| `cite-gml32-conformance.yml` | OGC GML 3.2 CITE Conformance | weekly `schedule` (Sat 06:00 UTC), `workflow_dispatch` | |
| `cite-gpkg12-conformance.yml` | OGC GeoPackage 1.2 CITE Conformance | weekly `schedule` (Sat 03:00 UTC), `workflow_dispatch` | |
| `ogc-maps-conformance.yml` | OGC API Maps Conformance | weekly `schedule` (Fri 06:00 UTC), `workflow_dispatch` | |
| `cng-conformance.yml` | Cloud-Native-Geospatial Conformance | weekly `schedule` (Wed 06:00 UTC), `workflow_dispatch` | COG/GeoParquet/PMTiles-class CNG conformance. |
| `cite-classic-conformance.yml` | Classic OGC CITE Conformance | `workflow_dispatch` | On-demand combined WMS 1.3 + WFS 2.0 classic lane. |
| `cite-evidence-report.yml` | CITE Evidence Report | weekly `schedule` (Fri 08:00 UTC), `workflow_dispatch` | Runs the public CITE suite set and builds `artifacts/cite-evidence/` (summary JSON, badge SVG, static index, full TeamEngine HTML) with optional Pages deployment. Also asserts `docs/cite-status.md` freshness and opens/updates an issue when the reviewed snapshot is >14 days stale (#2944). |

## Release and deploy

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `deploy.yml` | Build & Publish Images | `push` (`v*` tags), `workflow_dispatch` | Multi-arch (and AOT) images. After `publish-manifests`, `dispatch-geobench` sends a `repository_dispatch` to `honua-io/geobench` with the tag and image ref; skips with a notice when `GEOBENCH_DISPATCH_TOKEN` is absent (#1596). |
| `deploy-platform-images.yml` | Build & Publish Platform Images | `push` (`v*` tags), weekly `schedule`, `workflow_dispatch` | Platform image deployment. |
| `nuget-publish.yml` | Publish Honua.Core Package | `push` (`honua-core-v*`, `v*.*.*`), `workflow_dispatch` | Release-only publishing. |
| `release-bundle.yml` | Release Bundle (Compatibility Train) | `workflow_dispatch` | Foundation-first release-train orchestrator: one Native AOT RC image, integration/conformance and Esri-SDK evidence against that exact digest, every SDK cut against the same candidate (dry-run by default), then the release-train manifest plus the honua-devops validator. |
| `release-migration-performance.yml` | Release Migration Performance Evidence | `release`, daily `schedule`, `workflow_dispatch` | Runs the migration performance harness against the fixture-driven baseline (#1033) and uploads the website-linkable evidence artifact. |
| `geobench-release-trigger.yml` | Trigger Geobench Release Benchmarks | `release`, `workflow_dispatch` | `repository_dispatch` to the geobench repo on every published release. |
| `cloud-post-apply-validation.yml` | Cloud Post-Apply Validation | `workflow_call`, `workflow_dispatch` | Post-deploy validation. |
| `notify-evidence.yml` | Notify honua-evidence | `push` (trunk) | `repository_dispatch` (`producer-updated`) to `honua-io/honua-evidence` when the capability-matrix/keys snapshots or CITE status change, so aggregation does not wait for its daily fallback. |

## Maintenance, reusable, and manual benchmarks

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `trunk-sanity.yml` | Trunk Sanity | `push` (trunk), `workflow_dispatch` | Cheap post-merge restore/build only; heavy CI does not run on merge-to-trunk pushes. |
| `label-sync.yml` | Capability Label Sync | `push` (trunk), `workflow_dispatch` | Creates/updates `cap/<category>` labels from the canonical category list; never deletes or renames (#2896). |
| `reusable-sdk-pr-gate.yml` | SDK PR Gate | `workflow_call` | Reusable gate consumed by `honua-sdk-js`, `honua-sdk-dotnet`, and `honua-sdk-python`. |
| `server-test-reuse-benchmark.yml` | Server Test Reuse Benchmark | `workflow_dispatch` | Bounded A/B of an overlapped reuse producer against independent baselines. Manual, read-only, publishes no status. |
| `server-test-prebuild-benchmark.yml` | Server Test Prebuild Benchmark | `workflow_dispatch` | Manual read-only A/B proof for #3226; cannot affect branch protection or the train. |

## Recently retired

| Workflow file | Retired | Why |
|---|---|---|
| `pr-merge-train.yml` | 2026-07-21 (`d2afeb9d5`) | Second merge authority. `merge-train.yml` is the only lander; `scripts/ci/validate-single-merge-authority.sh` enforces it. |
| `auto-rerun-flaky.yml` | 2026-08-16 | Job guarded on `workflow_run.event == 'pull_request'`, which `ci.yml` has not emitted since #2865 removed its `pull_request` trigger. Every run for months was `skipped`. Bounded flake reruns live in the train (`scripts/ci/merge-train/classify-flake.sh`). |
| `ci-failure-triage.yml` | 2026-08-16 | Same dead guard. Its deterministic classifier (`scripts/ci/ci-failure-classifier.js`) and Bedrock helper (`scripts/ci/bedrock-triage.js`) had no other consumer and were removed with it; the train owns attribution (`attribute.sh`) and timeout classification (`classify-timeout.sh`). |
| `server-test-shard-cache-proof.yml` | 2026-08-16 | One-off hosted proof for the #2735 shard-local cache, triggered only by pushes to a merged experiment branch. The shipped cache lives in `ci.yml` and is still guarded by `scripts/ci/validate-server-test-shard-cache.sh`; the recorded proof stays in `server-test-binary-artifacts.md`. |
| `server-test-transfer-benchmark.yml` | 2026-08-16 | One-off #2722 benchmark whose design ADR-0074 rejected, triggered only by pushes to `ci/2722-hosted-transfer-benchmark`. Its config and evaluator went with it; the measured result stays in `server-test-transfer-benchmark.md`. Later producer designs are measured by `server-test-reuse-benchmark.yml` and `server-test-prebuild-benchmark.yml`. |

## Historical change log

The sections below record why earlier workflow changes were made. They are
history, not a description of the current tree — the tables above are.

### Changes Made in This Audit (Ticket #485)

#### Conformance workflows moved off PR path

The following workflows had `pull_request` and `push` triggers removed, leaving only `schedule` and `workflow_dispatch`:

- `cite-tiles-conformance.yml` (was PR + push + schedule)
- `cite-wfs20-conformance.yml` (was PR + schedule)
- `cite-wms-conformance.yml` (was PR + push + schedule)
- `cite-wmts-conformance.yml` (was PR + push + schedule)
- `ogc-maps-conformance.yml` (was PR + push + schedule)

Additionally, `cite-conformance.yml` (already schedule-only) had dead PR comment steps and a stale `pull-requests: write` permission removed to match.

**Rationale**: Conformance suites are external, heavyweight, and non-deterministic. They belong in the nightly certification lane, not the PR-blocking path. Regressions are caught by the weekly schedule and can be tested on-demand via `workflow_dispatch`.

#### CodeQL moved off PR path

`codeql.yml` no longer triggers on `pull_request` or merge-to-trunk push. It runs on a weekly schedule. This avoids adding a slow, non-deterministic security scan to routine PR or merge cycles. **(Superseded: a path-filtered `pull_request` lane using `build-mode: none` was later reinstated and is now the repository's only PR-time CodeQL security analysis — see the table above.)**

#### PR template and validation redesigned

The PR template now includes explicit sections for gate impact, docs/contract impact, release/deploy impact, and breaking changes. The `pr-template-check` job at the top of `ci.yml` validates these sections directly and replaced the previous standalone `pr-validation.yml` workflow. (It no longer gates the downstream graph — `pr-readiness` was decoupled from it — and since `ci.yml` lost its `pull_request` trigger both jobs short-circuit to success on every event that still reaches them.)

#### Issue templates redesigned

All issue forms now require acceptance criteria, affected repos, gate-tier impact, and release/deploy impact. This ensures grooming inputs match the workflow contract.

#### Reusable SDK PR gate added

`reusable-sdk-pr-gate.yml` provides a shared `workflow_call` contract for SDK repo PR gates. It accepts repo-specific build/test/lint commands and follows the toolchain and artifact conventions in `config-conventions.md`.

#### Composite actions extracted

Five composite actions were added to `.github/actions/` for shared CI setup and evidence handling:

- `setup-dotnet-ci` — .NET SDK, NuGet cache *(active)*
- `setup-node-ci` — Node.js setup, npm cache *(future: SDK workflows)*
- `setup-python-ci` — Python setup, pip cache *(future: conformance/script workflows)*
- `upload-ci-evidence` — artifact upload with standard naming and tier-based retention *(active)*
- `run-conformance-stack` — Docker bootstrap/teardown for CITE workflows *(future: conformance workflows)*

### Changes Made in Workflow Refactor (2026-04-25)

#### Security workflows consolidated

`container-security.yml` and `trivy-nightly.yml` were folded into `security-nightly.yml`. The consolidated nightly now owns NuGet vulnerability scanning, Trivy filesystem scanning, and container security validation in one security lane.

**Rationale**: The previous split created three scheduled security workflows with overlapping vulnerability-scan responsibilities and separate artifact conventions. One workflow keeps the security lane easier to monitor while preserving separate jobs for dependency, filesystem, and container concerns.

#### CITE wrappers normalized

`cite-conformance.yml` and `cite-tiles-conformance.yml` now call `cite-conformance-common.yml`, matching the single-suite CITE wrappers for GML, GeoPackage, KML, WMS, and WMTS.

**Rationale**: Features and Tiles used the same checkout/build/run/parse/upload/fail skeleton as the reusable CITE workflow. Keeping only suite-specific inputs in the dispatcher files reduces drift in cache scopes, artifact upload behavior, and failure handling.
