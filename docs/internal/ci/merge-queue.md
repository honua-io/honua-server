# Merge Queue + CI Speed — Operator Runbook

This documents the CI-throughput work: the **merge queue** (automates the manual
"bundle these PRs" dance), the **test-suite speed** changes, and the **shard-routing**
findings. CI workflow changes can only be fully validated by an actual GitHub run —
follow the validation steps before relying on them.

## 1. Merge queue

`ci.yml` now triggers on `merge_group` (in addition to `pull_request`). A
`merge_group` event is a non-`pull_request` event, so the existing job logic
already routes it down the **full** integration lane (`changes` /
`targeted-shards` else-branches → `run_all`; the PR-only jobs
`pr-template-check` / `pr-readiness` short-circuit to success). Net design:

| Lane | Trigger | Scope | Who waits on it |
|------|---------|-------|-----------------|
| **PR** | `pull_request` | Selective (affected shards) — fast | the author, per push |
| **Merge queue** | `merge_group` | Full CI on the **batched** commit | nobody blocks; auto-merges on green |

This is the fix for "I have to ask for PRs to be bundled": GitHub batches approved
PRs, runs CI **once** on the combined result, and fast-forwards `trunk` only if green.

### Enable it (one-time, repo settings — not in code)
1. **Settings → Branches → Branch protection rule for `trunk`** → enable
   **"Require merge queue"**.
2. Set the **required status check** to **`CI Gate`** (the aggregating job in `ci.yml`).
   Remove individual job names from "required checks" — `CI Gate` already aggregates them.
3. Merge-queue settings: merge method = your convention (squash); start with
   **max batch = 5**, **min = 1**, **wait = 5 min**; "only merge if combined check passes".
4. (Optional) `gh` after enabling: `gh pr merge <n> --auto --squash` queues a PR.

### Validate before trusting it
- Open a throwaway PR, approve it, click **"Merge when ready"**.
- Watch the `CI` run triggered by the **`merge_group`** event (not the PR event).
  Confirm `CI Gate` reports success and the PR fast-forwards trunk.
- If the `merge_group` run errors in `pr-template-check`/`pr-readiness`, those
  jobs' PR-only steps weren't skipped — re-check their `if:` guards.

## 2. Test-suite speed (per-class fixtures)

Integration test classes were creating a **new `WebAppFixture` per test method**
(xUnit instantiates the class once per test). Each `InitializeAsync` builds an
isolated Postgres schema and applies the **122 KB `tests/seed/server.yaml`** seed
(58 `CREATE TABLE` + 99 `CREATE INDEX`), and for `.ReplaceService(...)` classes
**rebuilds the whole `WebApplicationFactory<Program>` host** — per test.

Fix: read-only / idempotent-setup classes were converted to
`IClassFixture<WebAppFixture>` (or a per-class wrapper fixture for ones that
configure services), so setup runs **once per class** instead of per test.
Classes whose tests mutate shared state (feature edits, per-test metadata
changes, per-test mocks) were intentionally **left per-test** to preserve isolation.

Measured: **Stac 12m36s → 7m21s (−42%)**, under CPU contention (true gain larger).

## 2b. Local precheck / pre-push (the local-loop killer)

The `pre-push` git hook runs `scripts/ci/pre-pr-check.sh` on **every push** to a
non-trunk branch. That precheck uses the *same* router as CI, so a normal source
change escalated to **all 30 server-test shards run sequentially** (each with the
slow per-test fixture) **plus an AOT publish** — potentially hours per push,
before CI even starts.

Changes:
- **FAST tier (`HONUA_PRE_PR_FAST=1`)** — build + format + affected unit tests +
  architecture only; **skips the heavy Honua.Server.Tests shards and AOT** (those
  run in CI / the merge queue). **The pre-push hook now defaults to FAST**, so the
  push loop is quick. Override per-push with `HONUA_PRE_PR_FAST=0 git push`.
- **Parallel shards** — when you *do* run the full/smart precheck (before opening a
  PR), shards now run concurrently (`HONUA_PRE_PR_SHARD_PARALLELISM`, default 2)
  with per-shard logs + a pass/fail summary, instead of one-at-a-time.
- It already inherits the routing + suite-speed fixes above (same `ci-shards.json`,
  same test projects), so localized changes select fewer shards and each is faster.

Recommended local flow:
- routine push → FAST (automatic via the hook).
- before opening a PR → `HONUA_PRE_PR_FAST=0 bash scripts/ci/pre-pr-check.sh`
  (smart/affected) or `HONUA_PRE_PR_FULL=1 …` before a release / large refactor.

## 2c. Remaining (bigger, riskier) levers — not yet done
- **Cheaper per-test isolation** for the mutating/isolated classes (template-DB
  clone or a seeded-schema pool with data-reset) — removes the per-test 58-table/
  99-index DDL. Constrained by the schema-header routing + the global seed
  advisory lock; needs dedicated design + many parallel runs to prove non-flaky.
- **Shared-host service overrides** so `.ReplaceService` classes stop rebuilding
  the host per test.

## 3. Shard routing — `run_all` over-escalation (the big CI-time finding)

Evidence from real PRs (count of `Server Tests` shards run, of 30):

| PR | kind | shards |
|----|------|--------|
| docs / CI-only / test-only | — | **1** |
| esri empty-string fix (1 file) | source | **30** |
| migration-batch bg service | source | **30** |
| WCS temporal scaling | source | **30** |
| PMTiles batch (tile-cache) | source | **30** (`reason: infrastructure_change`) |
| BIM/CityGML ingest | source | **30** |

**Verdict: the selective router works for docs/CI/test diffs but escalates ~every
source PR to all 30 shards.** Causes in `.github/ci-shards.json`:
- `infrastructure_paths` is broad (whole `Features/Infrastructure/`, `Queries/`,
  `Shared/` trees + every listed `.csproj` + `Honua.sln`).
- `unmapped_source_run_all_prefixes` fail-safe: any source dir not mapped to a
  shard's `paths` → `run_all`. Many real dirs are unmapped.

### Done (conservative, this change)
Added `paths` mappings so these route to owning shards instead of `run_all`:
`OgcClassic/Wcs20/` → WFS + Classic-Maps shards; `Honua.Scene/` → Scene + harness
shards.

### How to verify routing improvements (diagnostic)
For a PR run, count shards: 
```
gh api "repos/<owner>/<repo>/actions/runs/<run_id>/jobs?per_page=100" \
  --jq '[.jobs[]|select(.name|startswith("Server Tests ("))]|length'
```
A localized source PR should now run a handful, not 30. **Under-tightening (still
`run_all`) is safe; over-tightening that skips a shard which needed the change is
not** — so map uncertain dirs to *all* plausibly-affected shards, and validate
each change with `scripts/ci/validate-ci-router.sh` (Python helper runs in CI).

## 4. Finer sharding of the heaviest protocol suites (CI wall-time lever)

The per-class fixture work (§2) cut per-test setup, but the slow protocol suites
are **request/server-bound** (instrumented: per-test DB setup ≈ 4% of ~6.4s/test),
so the remaining lever for CI wall-time is **more parallel runner jobs** — split
the heaviest shards so each runner runs fewer tests. CI already shards across
runners; this just makes the slowest shards smaller.

Split (in `.github/ci-shards.json`), 30 → 38 shards:

| Original shard (cap) | Now |
|----------------------|-----|
| FeatureServer Endpoints (55m) | **FeatureServer Query** + **FeatureServer Tiles and Replica** + **FeatureServer Maintenance and Temporal** + **FeatureServer Endpoints** (catch-all) |
| GeoServices ImageServer (42m) | **GeoServices ImageServer** (ImageServer+Catalog, catch-all) + **GeoServices GPServer and NAServer** |
| OGC API Features (35m) | **OGC API Features** (catch-all) + **OGC API Features Transactions** |
| OGC API Maps and Tiles (40m) | **OGC API Maps and Tiles** (Maps+Records, catch-all) + **OGC API Tiles Coverages and Processes** |
| WFS (35m) | **WFS** (Wfs20-minus-Endpoints **+ Wcs20**, catch-all) + **WFS Endpoints** (isolates the 74-test `Wfs20EndpointsTests`) |
| OGC Classic Maps (30m) | **OGC Classic Maps** (Wms, catch-all) + **OGC Classic WMTS** |

**Bug fixed in passing:** `Wcs20EndpointsTests` (~28 `[IntegrationTest]`s, Tier=Integration) matched **no** shard `filter` before this change (WFS filtered `~Wfs20`, Classic Maps filtered `~Wms|~Wmts`), so WCS 2.0 tests never ran in CI even though the Wcs20 source path was mapped to those shards. The WFS catch-all now includes `~Wcs20`, so they run.

### The catch-all partition rule (why no test is silently dropped)
All FeatureServer endpoint classes share ONE namespace, so they're partitioned by
**class-name substring**, not sub-namespace. The design is gap-proof:
- Each carved sibling selects an explicit cluster (`~FeatureServerQuery`, `~MvtTile|~Replica`, …).
- The shard that **keeps the original name** is the **catch-all**: its filter
  `!~`-excludes exactly the siblings' selectors, so it picks up everything else —
  including any newly added class. Union of the four = the original suite; the
  intersections are empty.
- Namespace-based splits (ImageServer, OGC Maps) are inherently disjoint.

The original shard NAME is always preserved on the catch-all so
`scripts/ci/validate-ci-router.sh`'s name assertions and branch-protection
required checks keep resolving. Each sibling repeats the parent's source `paths`
so a source change co-selects all siblings (the whole split suite runs).

### Validate before trusting it (needs a GitHub run)
Locally verified: `jq` structural validation, unique name/suffix/log_name, and a
**partition simulation** (every FeatureServer + OGC Features class maps to exactly
one shard — proven, incl. classes not in any hand list), plus router dry-runs
(`honua-server-targeted-tests.sh`) showing a FeatureServer change selects all 6
FeatureServer shards. NOT yet validated on CI:
- `scripts/ci/validate-ci-router.sh` end-to-end (needs `python3`/PyYAML — runs in
  the `ci-router-validation` job).
- Actual green run + real per-shard timings. Balance is by class count (a proxy);
  **retune `filter` membership from the `*.timing.json` artifacts** the runner
  emits once you have a real run. Over/under-balanced is safe; a dropped test is
  not — and the simulation shows none are dropped.

Diagnostic after a run — confirm the split shards appear and are faster:
```
gh api "repos/<owner>/<repo>/actions/runs/<run_id>/jobs?per_page=100" \
  --jq '[.jobs[]|select(.name|startswith("Server Tests ("))|{name,minutes:((.completed_at|fromdate)-(.started_at|fromdate))/60}]'
```

### Remaining routing opportunities (do with care)
Map these localizable orphans to owning shards (mirroring the `_comment`'s
"map shared-but-localizable areas to every exercising shard" rule):
`Server/Features/Admin/TileOperations/` → tile/admin shards; verify why
`Import/Features/Migration/` still escalated despite the Migration shard mapping.
Keep truly-shared infra (`Program.cs`, `Startup/`, shared `FeatureStore` reader)
escalating — those legitimately need broad runs.
