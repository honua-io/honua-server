# Server-test shard timeout budgets

Owner: ADR-0037 (CI shard matrix). Source of truth for the numbers:
[`.github/ci-shards.json`](../../../.github/ci-shards.json).

Each `Honua.Server.Tests` shard carries two timeouts:

| Field | Applied by | Purpose |
|---|---|---|
| `test_timeout_minutes` | `scripts/ci/run-server-test-shard.sh` (inner `timeout`) | Bounds the `dotnet test` invocation so the shard's log, TRX and timing artifacts are still written and uploaded. |
| `timeout_minutes` | GitHub Actions job cap | Hard backstop for the whole job (checkout, setup, restore/materialize, test, uploads). |

## Policy

1. **The job cap must clear the test cap by at least 10 minutes.** Measured
   non-test job overhead across recent batch runs is p90 ~7.2 min and max
   ~9.7 min. If the gap is smaller, the runner can cancel the job before the
   inner timeout fires, and the failure surfaces as an unattributable
   cancellation with no log or timing artifact. `scripts/ci/validate-ci-router.sh`
   enforces this statically against `shard_budget_policy.min_job_overhead_minutes`.
2. **Keep measured p90 at or below 70% of the test cap**
   (`shard_budget_policy.target_utilization`). The inner timeout exists to bound
   a genuine *hang*, not to bound normal test growth — a shard that legitimately
   needs more time should be given a bigger budget or split, not left to fail
   intermittently with `exit 124`.
3. **A passing shard at or above 80% of its budget is a defect to schedule**
   (`shard_budget_policy.warn_utilization`). `run-server-test-shard.sh` emits
   `::warning::HONUA_SHARD_LOW_HEADROOM` for that case.

## Signals

`run-server-test-shard.sh` records `capacity_status` in each shard's
`*.timing.json` and emits a matching annotation:

| `capacity_status` | Annotation | Meaning |
|---|---|---|
| `ok` | none | Passed below the warn ratio. |
| `low_headroom` | `::warning::HONUA_SHARD_LOW_HEADROOM` | **Passed**, but consumed >= 80% of its budget. Re-base the budget or split the shard before the next test lands in it. |
| `capacity_exhausted` | `::error::HONUA_SHARD_CAPACITY_EXHAUSTED` | Hit the cap while still producing output. The shard is over capacity; this is not attributable to any single PR. |
| `hang_suspected` | `::error::HONUA_SHARD_HANG_SUSPECTED` | Hit the cap after producing no output for `HONUA_SERVER_TEST_STALL_SECONDS` (default 300s). Treat as a genuine hang. |
| `not_assessed` | `::error::HONUA_SHARD_KILLED` when the run was SIGKILLed | Ran under a budget but neither passed nor hit the cap. A failing run can abort early or run long on retries, so its duration is not a capacity sample and the fleet audit excludes it. |
| `unbounded` | none | No inner timeout configured. |

### Timeout exit codes

`timeout` sends SIGTERM at the cap and reports **124**. A shard wedged hard
enough to *ignore* that SIGTERM is escalated to SIGKILL after
`HONUA_SERVER_TEST_KILL_AFTER_SECONDS` (default 30s), and `timeout` then reports
**137** instead. Both are timeouts; `kill_escalated: true` in the timing artifact
distinguishes the second.

137 on its own is ambiguous — an OOM kill or any other external SIGKILL produces
it too — so it is not blanket-mapped to a timeout. The clock is the
discriminator, and it accounts for the **full** escalation schedule rather than
the cap alone: `timeout` sends SIGTERM at `test_timeout_minutes` and only sends
SIGKILL after `HONUA_SERVER_TEST_KILL_AFTER_SECONDS` (default 30s) has elapsed on
top of that. So the earliest 137 attributable to the runner is at
`timeout_seconds + kill_after_seconds`, less a small tolerance
(`HONUA_SERVER_TEST_KILL_ESCALATION_TOLERANCE_SECONDS`, default 2s) covering
whole-second sampling truncation, and never earlier than the cap itself.

A 137 at or beyond that deadline is a kill-escalated timeout. A 137 **before** it
— including a host OOM kill landing inside the grace window, which is exactly when
a memory-starved runner is most likely to reap the test host — is reported as
`status: killed` with `::error::HONUA_SHARD_KILLED` (suspect an out-of-memory
kill) rather than being laundered into a capacity signal, because the two need
different remediations.

The merge train consumes those markers in
`scripts/ci/merge-train/classify-timeout.sh`. A `hang_suspected`/generic timeout
keeps the historical one-rerun budget; `capacity_exhausted` is treated as a real
failure **immediately and is never rerun**, because rerunning an over-capacity
shard reproduces the exhaustion at full runner cost. It also gets its own
terminal result code so the train escalates the whole batch instead of running
autofix and per-PR attribution, which would drop or escalate an arbitrary member
for a defect none of them introduced.

## Re-basing the budgets

```bash
gh run download <run-id> -p 'server-test-results-*' -D ./artifacts
scripts/ci/audit-shard-headroom.py --timings-dir ./artifacts --markdown
```

Collect several runs into the same directory for a usable p90. Add
`--fail-on-warn` to turn the audit into a gate.

Two things the audit deliberately does not do:

* **It ignores `hang_suspected` runs.** A shard that went silent and was killed
  at its cap has not shown that its normal workload needs more time, so counting
  it as capacity evidence would recommend a bigger budget for a stall and delay
  detection of the hang.
* **It bounds each censored (timed-out) sample by the budget recorded in that
  artifact**, not by the cap configured today. A timeout recorded under a
  20-minute cap proves only that the run exceeded 20 minutes; auditing it
  against a since-raised 29-minute cap would manufacture a ~42-minute
  recommendation from evidence that never existed. The audit also never
  recommends shrinking a budget.

## Measured margins (2026-07-29)

Test-step durations from the `Run server test shard` step of the 20 most recent
`ci.yml` runs (batch CI, trunk and nightly). `p50`/`p90` are nearest-rank
percentiles in minutes and exclude runs that were killed at the cap; `Timeouts`
counts runs that reached the cap. Bold values are the budgets this audit
changed.

| Shard | Runs | Timeouts | p50 | p90 | Old cap | p90/old | New cap | New job cap | p90/new |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| OData Core | 20 | 7 | 19.0 | 19.5 | 20 | 98% | **29** | **39** | 67% |
| Server Features Admin and Console (pre-#3059) | 19 | 5 | 47.6 | 49.1 | 50 | 98% | **72** | **82** | 68% |
| OData Query and Spatial | 20 | 0 | 29.1 | 30.6 | 32 | 96% | **44** | **54** | 70% |
| GeoServices ImageServer | 20 | 1 | 22.6 | 23.1 | 24 | 96% | **35** | **45** | 66% |
| Server Features Collaboration and Content (pre-#3059) | 24 | 0 | 41.6 | 46.1 | 48 | 96% | **66** | **76** | 70% |
| GeoServices MapServer | 20 | 5 | 18.5 | 19.0 | 20 | 95% | **29** | **39** | 66% |
| Infra and Security | 20 | 0 | 25.6 | 26.6 | 30 | 89% | **39** | **49** | 68% |
| Migration | 22 | 2 | 16.5 | 17.0 | 20 | 85% | **29** | **39** | 59% |
| Server Features Admin Auth and Identity (pre-#3059) | 19 | 0 | 35.1 | 37.1 | 45 | 82% | **54** | **64** | 69% |
| Admin & Infrastructure | 20 | 0 | 22.6 | 23.6 | 32 | 74% | 32 | 42 | 74% |
| Server Features Admin Platform and Governance (pre-#3059) | 19 | 0 | 32.5 | 33.5 | 45 | 74% | 45 | 55 | 74% |
| Operator Eval Harness | 23 | 0 | 13.1 | 14.1 | 20 | 70% | 20 | 30 | 70% |
| Scene | 20 | 0 | 18.5 | 20.0 | 30 | 67% | 30 | 40 | 67% |
| STAC Protocol | 20 | 0 | 9.0 | 10.0 | 15 | 67% | 15 | 25 | 67% |
| Server Features Misc | 25 | 0 | 26.1 | 29.1 | 48 | 61% | 48 | 60 | 61% |
| FileImport | 21 | 0 | 11.0 | 11.5 | 20 | 58% | 20 | 30 | 58% |
| WFS | 20 | 0 | 13.5 | 14.5 | 25 | 58% | 25 | 35 | 58% |
| OGC API Tiles Coverages and Processes | 22 | 0 | 11.5 | 12.5 | 22 | 57% | 22 | **32** | 57% |
| Geocoding | 20 | 0 | 7.0 | 8.5 | 15 | 57% | 15 | 25 | 57% |
| Core Endpoints | 20 | 0 | 17.0 | 18.0 | 32 | 56% | 32 | **42** | 56% |
| Core | 23 | 0 | 14.0 | 14.5 | 26 | 56% | 26 | **36** | 56% |
| MCP | 21 | 0 | 7.5 | 8.0 | 15 | 53% | 15 | 25 | 53% |
| OGC API Maps and Tiles | 20 | 0 | 10.5 | 11.5 | 22 | 52% | 22 | **32** | 52% |
| OGC Classic Maps | 20 | 0 | 8.5 | 10.0 | 20 | 50% | 20 | 30 | 50% |
| GeoServices GPServer and NAServer | 22 | 0 | 10.0 | 10.5 | 22 | 48% | 22 | **32** | 48% |
| Server Features Data and Sharing | 25 | 0 | 20.5 | 22.0 | 48 | 46% | 48 | 60 | 46% |
| OGC API Features | 20 | 0 | 9.5 | 10.0 | 22 | 45% | 22 | **32** | 45% |
| WFS Endpoints | 20 | 0 | 9.5 | 10.0 | 22 | 45% | 22 | **32** | 45% |
| Server Features Admin Operations | 20 | 0 | 14.5 | 15.0 | 35 | 43% | 35 | 45 | 43% |
| Core Attachments and Records | 20 | 0 | 9.5 | 10.0 | 26 | 38% | 26 | **36** | 38% |
| OGC Classic WMTS | 20 | 0 | 7.0 | 7.5 | 20 | 38% | 20 | 30 | 38% |
| Server Features Spec Printing and Static Maps | 25 | 0 | 15.0 | 16.0 | 48 | 33% | 48 | 60 | 33% |
| GeoServices Geometry VectorTile and Versioning | 20 | 0 | 6.5 | 7.0 | 24 | 29% | 24 | **34** | 29% |
| OData Mutations and Batch | 20 | 0 | 5.0 | 5.5 | 20 | 28% | 20 | 30 | 28% |
| FeatureServer Tiles and Replica | 20 | 0 | 5.5 | 6.0 | 22 | 27% | 22 | **32** | 27% |
| FeatureServer Maintenance and Temporal | 20 | 0 | 6.0 | 6.0 | 22 | 27% | 22 | **32** | 27% |
| Raster & Scene Analysis | 22 | 0 | 5.0 | 5.0 | 20 | 25% | 20 | 30 | 25% |
| FeatureServer Endpoints | 20 | 0 | 6.0 | 6.5 | 26 | 25% | 26 | **36** | 25% |
| Server Features Admin Tiles and Scenes | 20 | 0 | 6.5 | 7.0 | 35 | 20% | 35 | 45 | 20% |
| STAC and API Governance | 20 | 0 | 3.5 | 3.5 | 20 | 18% | 20 | 30 | 18% |
| OData Client Certification | 20 | 0 | 4.0 | 4.0 | 25 | 16% | 25 | 35 | 16% |
| FeatureServer Replication | 20 | 0 | 3.0 | 3.0 | 20 | 15% | 20 | 30 | 15% |
| RasterImport | 20 | 0 | 2.0 | 2.5 | 18 | 14% | 18 | **28** | 14% |
| Server Features Admin Layer Management | 20 | 0 | 4.5 | 5.0 | 35 | 14% | 35 | 45 | 14% |
| SensorThings | 20 | 0 | 2.0 | 2.0 | 15 | 13% | 15 | 25 | 13% |
| Comprehensive | 20 | 0 | 1.5 | 1.5 | 15 | 10% | 15 | 25 | 10% |
| Geometry Tiles and Terrain | 20 | 0 | 2.0 | 2.0 | 20 | 10% | 20 | 30 | 10% |
| Cloud & Contract | 20 | 0 | 1.0 | 1.5 | 20 | 8% | 20 | 30 | 8% |
| OGC API Features Transactions | 20 | 0 | 1.5 | 1.5 | 22 | 7% | 22 | **32** | 7% |
| Workflow Packages | 20 | 0 | 1.0 | 1.0 | 20 | 5% | 20 | 30 | 5% |
| FeatureServer Query | 20 | 0 | 1.0 | 1.0 | 22 | 5% | 22 | **32** | 5% |
| GP Devkit CLI | 20 | 0 | 0.5 | 0.5 | 10 | 5% | 10 | **20** | 5% |
| Performance | 20 | 0 | 0.5 | 0.5 | 15 | 3% | 15 | 25 | 3% |
| FeatureServer Services | 20 | 0 | 0.5 | 0.5 | 20 | 2% | 20 | 30 | 2% |

## #3059 split baseline and follow-up measurement

#3059 replaces the four oversized rows marked above with 13 semantic children.
Every child uses a 22-minute inner cap and a 32-minute job cap, which are no
larger than the pre-split matrix medians. The 70% headroom line is therefore
15.4 minutes for every child.

| Historical parent | Historical p90 | Children | Required child p90 |
|---|---:|---:|---:|
| Server Features Admin and Console | 49.1 min | 4 | <=15.4 min |
| Server Features Collaboration and Content | 46.1 min | 3 | <=15.4 min |
| Server Features Admin Auth and Identity | 37.1 min | 3 | <=15.4 min |
| Server Features Admin Platform and Governance | 33.5 min | 3 | <=15.4 min |

The child groups are:

* `Server Features Console and Alerts`, `Server Features Admin Network and
  Jobs`, `Server Features Admin Catalog and Configuration`, and `Server
  Features Admin Integrations and Automation`;
* `Server Features Studio and Feature Store`, `Server Features Analytics
  Export and Reporting`, and `Server Features Collaboration Mobile and
  Identity`;
* `Server Features Admin Authentication`, `Server Features Admin Credentials`,
  and `Server Features Admin Authorization`; and
* `Server Features Admin Release Control`, `Server Features Admin Platform and
  Connections`, and `Server Features Admin Governance and Sharing`.

Four recent successful batch TRX profiles were used to balance class placement.
Those artifacts predate the split, so they cannot supply observed child
wall-clock percentiles or prove the new batch critical path. After the split
lands, download at least 20 successful batch artifacts and regenerate this
audit with the new artifact suffixes:

```bash
gh run download <run-id> --dir /tmp/honua-shard-audit
scripts/ci/audit-shard-headroom.py /tmp/honua-shard-audit \
  --config .github/ci-shards.json
```

Replace the required values above with observed child p50/p90 values, and record
the batch `server-tests` critical path before closing #3059. Each child must be
at or below 15.4 minutes p90, and the batch critical path must be lower than the
49.1-minute historical parent p90.

### Findings

* **Nine shards were at or above 75% of their inner cap**, five of which had
  already been killed at the cap in this window: `OData Core` (7/20 runs),
  `GeoServices MapServer` (5/20), `Server Features Admin and Console` (5/19),
  `Migration` (2/22) and `GeoServices ImageServer` (1/20). Migration was the
  reported symptom (#3054), but it was not the worst case — `OData Core` and
  `Server Features Admin and Console` were both sitting at 98%.
* **17 shards had a job-cap gap below 10 minutes** (as small as 5 for
  `GP Devkit CLI`; 16 of them were not otherwise re-capped), so an inner timeout
  there could have been pre-empted by the runner cancelling the job — losing the
  log, TRX and timing artifacts and surfacing as an unattributable cancellation.
  All 63 configured shards are now at gap >= 10.
* **Watchlist.** Of the unchanged shards, `Admin & Infrastructure` (74%) and
  `Operator Eval Harness` (70%) remain closest to the warn line. The 13 #3059
  children need fresh batch samples before they can be ranked. The
  `HONUA_SHARD_LOW_HEADROOM` warning will fire before a sampled shard starts
  failing.
* **Raising a cap costs nothing on the healthy path.** These are caps, not
  durations: a shard that finishes in 17 minutes still finishes in 17 minutes.
  The only case that gets more expensive is a genuine hang, which now also
  self-identifies as `HONUA_SHARD_HANG_SUSPECTED`. Before #3059, the largest
  job cap (82 min, `Server Features Admin and Console`) still fit the merge train's
  6600-second (110 min) CI wait: shards start roughly 13 minutes into a batch
  run (`Build & Format Check` p50 ~12.4 min at ~0.7 min offset), so the
  worst-case hung batch lands near 96 minutes. That margin is another reason to
  split the largest shards rather than keep raising their budgets.
* **Oversized shard split.** #3059 replaces the four historical parent entries
  with 13 children capped at the former matrix median (22 inner / 32 job).
  `.github/ci-shards.json` retains each removed parent filter as an exact
  partition contract, and the coverage guard fails if a child creates a gap,
  overlap, or leak. Observed child p90 values and the new batch critical path
  remain a post-landing measurement as described above.
