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

The classifier reads the small, paginated exact-check annotations first;
`HONUA_SHARD_CAPACITY_EXHAUSTED` is an error annotation, so no multi-megabyte
log download is needed for the normal capacity path. Other failures use the
Actions job-log REST endpoint, with `gh run view --job` as a fallback. GitHub
can expose a terminal job and its REST log while the parent workflow's aggregate
log remains unavailable; relying on only the aggregate surface can therefore
erase the capacity marker or block classification. If neither exact log surface
is readable, classification stops with a distinct evidence-unavailable result
and escalates the batch without per-PR attribution. Job-name routing alone is
never sufficient evidence that a member caused the failure.

### Ordering guarantee: shard-terminal markers are classified before the pre-existing filter

`train_classify_capacity_guard` (`classify-timeout.sh`) runs for **every**
failing job **before** the merge train's pre-existing-failure filter is allowed
to subtract anything — the ci-gate loop in `train.sh` calls it ahead of
`train_preexisting_filter`.

That filter exists to stop the train escalating PRs for a failure trunk already
carries, and it decides equivalence from job-scoped signatures sampled out of a
**bounded window** of each job log. All three shard markers are emitted as the
shard's **last** error: in job 95149717187 of run 31940825557 the marker was
line 47296 of 47298, far outside the sampled window, and the window itself was
filled with passing-test lines and per-run structured log records — exactly the
noise a red shard on trunk produces too. Classifying after the filter therefore
risked subtracting the shard as "already failing on trunk" and **landing a batch
on tests that never finished running**.

The guard routes each shape to what it deserves:

| Evidence | Guard | Route |
|---|---|---|
| `HONUA_SHARD_CAPACITY_EXHAUSTED` | rc 7, kind `capacity` | terminal; escalate batch, never rerun |
| `HONUA_SHARD_KILLED` | rc 7, kind `shard-killed` | terminal; escalate batch (suspect OOM, not a timeout) |
| `HONUA_SHARD_HANG_SUSPECTED` / generic exit-124 | rc 9, kind `shard-timeout` | bounded hang rerun, but **never** subtracted on the way |
| no readable log after bounded retries | rc 8, kind `evidence-unavailable` | terminal; escalate batch, no attribution |
| anything else | rc 0 | ordinary comparable failure; filter proceeds as before |

Three independent protections hold:

* the guard runs first, so no shard-terminal or evidence-unavailable job can
  reach subtraction, autofix, or per-PR attribution;
* the signature builder (`preexisting.sh`) scans the **whole** job log for all
  three markers and, when one is present, emits a single run-scoped signature
  that can never cancel against another run — so the filter is correct in
  isolation too; and
* marker detection is **anchored** to the emitted `::error::`/`##[error]`
  annotation form followed by ` shard=`. An unanchored token search also matched
  job logs that merely *name* the marker — the merge train's own warning text
  does, so every `CI Router Validation` log contains it — which would have made
  a router failure permanently non-subtractable.

Transient Actions read failures are retried with backoff before the guard
concludes `evidence-unavailable`, so one flaky `gh run view` cannot convert a
landable batch into a whole-batch escalation with sticky `train:escalated`
labels.

`scripts/ci/merge-train/fixtures/validate-capacity-ordering.sh` reproduces the
production shape for every marker and fails if any protection or the ordering
regresses, while a control fixture proves a genuine pre-existing failure is
still subtracted.

## Re-basing the budgets

```bash
gh run download <run-id> -p 'server-test-results-*' -D ./artifacts
scripts/ci/audit-shard-headroom.py --timings-dir ./artifacts --markdown
```

Collect several runs into the same directory for a usable p90. Add
`--fail-on-warn` to turn the audit into a gate.

When the audit says a shard is over capacity, the next question is *which
classes* are the cap and whether splitting them would help.
`scripts/ci/summarize-trx-class-intervals.py` answers that from the same
artifacts:

```bash
scripts/ci/summarize-trx-class-intervals.py --trx-dir ./artifacts
scripts/ci/summarize-trx-class-intervals.py --trx-dir ./artifacts --group-depth 5
```

It reads each `UnitTestResult`'s `startTime`/`endTime` rather than its
`duration` — `duration` excludes fixture and collection setup and so
under-reports an integration shard by more than half — and reports, per class or
namespace group, both the **span** (first start to last end) and the **union**
of busy intervals. It also prints whether the summed per-class spans reproduce
the whole run's span. When they do, the shard is serial and class placement is
directly additive, so a span is what moving that class buys; when they exceed
it, the shard runs collections in parallel and only the union columns are
meaningful. Both #3229 (serial, ratio 1.00) and the `Infra and Security` split
(parallel, ratio 3.59) below were measured with it.

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
| Server Features Data and Sharing (pre-#3229) | 25 | 0 | 20.5 | 22.0 | 48 | 46% | 48 | 60 | 46% |
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

### Findings (2026-07-29 audit)

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
  All configured shards were brought to gap >= 10 (65 then, 66 today).
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
scripts/ci/audit-shard-headroom.py --timings-dir /tmp/honua-shard-audit \
  --config .github/ci-shards.json
```

Replace the required values above with observed child p50/p90 values, and record
the batch `server-tests` critical path before closing #3059. Each child must be
at or below 15.4 minutes p90, and the batch critical path must be lower than the
49.1-minute historical parent p90.

## Infra and Security capacity split (2026-08-16)

The #3197 merge-train canary exposed a deterministic capacity failure in the
historical `Infra and Security` shard. Smart CI run `31940825557`, job
`95149717187`, consumed the full 39-minute inner test budget and emitted
`HONUA_SHARD_CAPACITY_EXHAUSTED` after 2,343 seconds. This was not attributed to
the PR: the merge-train observer classifies the exact job annotation/log as
capacity evidence and escalates the shard instead of dropping the PR.

The nearest scheduled full-run baseline, run `31939301584`, already showed the
same risk before the canary: the shard's test step took 36.02 minutes and the
job took 42.98 minutes. Raising the cap would preserve a large serial critical
path, so the shard is split into three parallel children:

| Child | Test surface | Inner / outer cap |
|---|---|---:|
| Infrastructure and Control Plane | infrastructure, control plane | 30 / 40 min |
| Security and Authorization | security, authorization | 30 / 40 min |
| Caching File Storage and Styling | caching, file storage, styling | 30 / 40 min |

The legacy parent filter remains an exact partition contract. The class-level
coverage guard proves every formerly discoverable class is claimed by exactly
one child with no leakage. The historical GeoServices Catalog filter was dead
in this shard because its default `Honua.Server.Tests` project cannot discover
the catalog classes; those remain covered by `GeoServices ImageServer`, which
targets `Honua.Protocols.GeoServices.Tests` explicitly.

The successful scheduled baseline `31939301584` provides a finer check on the
partition: security/authorization covered 151 tests and about 13 minutes of
active test intervals; infrastructure/control-plane covered 948 tests and
about 10 minutes; caching/file-storage/styling covered 186 tests and about 15
minutes. These are single-run active intervals rather than percentile claims,
but they show why three children are safer than leaving security and
infrastructure combined near the new cap.

This split is evidence-driven but not yet a new percentile baseline. After it
lands, retain at least 20 successful train/full-run artifacts for all three child
suffixes and replace this section with observed p50/p90, timeout utilization,
and the resulting batch critical path. A child reaching the 80% warning line
must be rebalanced or split again rather than silently receiving another cap
increase.

### Multi-run confirmation (measured while re-basing #3229)

The single-run check above was widened to the five most recent
`server-test-results-infra-security` artifacts that recorded a duration
(`31691645087`, `31732911300`, `31761102448`, `31931541793`, `31939301584`).
Pre-split, that sample ran the parent at 33.2–36.0 minutes against its
39-minute inner cap (85–92%), and the #3197 canary `31940825557` reached
39.05 minutes and exhausted it outright. Run `31931541793` is in the sample
because the infra shard itself completed in 33.2 minutes there; that run's
failure was the `Server Features Data and Sharing` timeout analysed in the next
section, not an infra one.

Replaying each run's TRX class intervals through the three children's filters
gives their **union active wall time** — the parent shard runs several
collections concurrently, so per-class intervals must be unioned rather than
summed:

| Child | Tests | p50 (min) | p90 (min) | Cap | p90 / cap |
|---|---:|---:|---:|---:|---:|
| Infrastructure and Control Plane | 948 | 8.8 | 9.7 | 30 | 32% |
| Security and Authorization | 151 | 12.9 | 13.5 | 30 | 45% |
| Caching File Storage and Styling | 186 | 14.5 | 16.3 | 30 | 54% |

The three children's unions (35.3 min summed) reproduce the parent's whole-run
union (34.1 min), so the concurrency is *within* families, not across them, and
the split is close to additive. Every child is well inside the 70% target on
this five-run sample. The capacity failure the merge train surfaced against
#3197 — run `31940825557`, 10:22Z — predates the split commit `7e83d9da5`
(2026-08-16 12:42Z), so no further change to this family is required.
Post-split child artifacts still need collecting before this section can be
restated as an observed child baseline.

## Data and Sharing capacity split (2026-08-16, #3229)

`Server Features Data and Sharing` was the merge-train batch wall-clock floor.
The six-PR run [`31777846581`](https://github.com/honua-io/honua-server/actions/runs/31777846581)
completed only when this shard did: its test step ran 07:00:35Z–07:44:38Z
(44.05 min) against a 48-minute inner cap while every other required job had
already finished.

### Evidence

Nine `*.timing.json` artifacts from the most recent batch and scheduled runs
that executed the shard (`gh run download <id> -n
server-test-results-server-features-data-sharing`):

| Run | Test step (min) | `capacity_status` |
|---|---:|---|
| `31691645087` | 41.05 | low_headroom |
| `31732911300` | 42.05 | low_headroom |
| `31761102448` | 33.03 | ok |
| `31774376922` | 45.55 | low_headroom |
| `31777846581` | 44.05 | low_headroom |
| `31921511274` | 43.63 | low_headroom |
| `31931541793` | **48.05** | **capacity_exhausted** (exit 124) |
| `31939301584` | 37.70 | ok |
| `31940825557` | 43.88 | low_headroom |

`scripts/ci/audit-shard-headroom.py --timings-dir <collected> --config
<pre-split ci-shards.json>` scores that sample **p50 43.6 min, p90 48.0 min,
100% of cap, `over_capacity`**, with a recommended cap of 69 minutes — i.e. the
audit's only lever is raising the timeout, which #3229 rules out.

### Cause: stable serial workload, not a hang or contention

The eight successful runs' TRX files give per-class wall intervals
(`UnitTestResult/@startTime..@endTime`). Two facts fall out:

1. **The shard is strictly serial.** The sum of the per-class intervals
   reproduces the whole test step to within 4–11 seconds on every run, so there
   is no parallel slack to recover and no idle gap to explain. Class placement
   is therefore directly additive: moving a class moves its whole interval.
2. **Three Streaming classes are 45% of it.** Per-class medians across the eight
   runs:

| Class group | p50 (min) | p90 (min) | Share |
|---|---:|---:|---:|
| `Streaming.*` (3 classes) | 18.0 | 20.7 | 45% |
| — of which `FeatureStreamSnapshotEndpointsTests` | 12.3 | 14.2 | 31% |
| `Sharing.*` (7 classes) | 15.4 | 17.5 | 38% |
| `DataEnrichment.*` (7 classes) | 3.6 | 5.1 | 9% |
| `Capabilities.*` (6 classes) | 1.9 | 2.3 | 5% |
| `Grounding.*` (10 classes) | 1.5 | 1.8 | 4% |
| `Orchestration.*` (7 classes) | 0.1 | 0.1 | <1% |

The growth is real workload: the `#3038` controlled-conformance mutation
workflow landed `FeatureStreamConformanceEndpointsTests` and grew
`FeatureStreamSnapshotEndpointsTests`, which is why the 2026-07-29 row above
(p90 22.0 min, 46% of cap) no longer describes this shard. That row is
superseded by the table in this section.

### The split

| Child | Test surface | p50 / p90 (min) | Inner / outer cap | p90 / cap |
|---|---|---:|---:|---:|
| Server Features Streaming Snapshot and Conformance | `Streaming.FeatureStreamSnapshotEndpointsTests`, `…ConformanceEndpointsTests`, `…CursorDurabilityTests` | 18.0 / 20.7 | 30 / 40 | 69% |
| Server Features Data Enrichment and Sharing | `DataEnrichment.*`, `Orchestration.*`, `Sharing.*`, `Grounding.*`, `Capabilities.*` | 23.6 / 25.3 | 38 / 48 | 67% |

Both children are at or below the 70% target, and both job caps clear their
inner cap by the required 10 minutes.

Two children rather than three: each shard pays a full restore+build of
`Honua.Server.Tests`, and the next batch tail after this split is
`Server Features Misc` at 25.9–28.4 minutes of test step (measured on runs
`31940825557`, `31939301584`, `31777846581`). Splitting `Sharing.*` off as a
third child would drop this family's tail to 20.7 minutes but buy no batch
wall-clock, because `Server Features Misc` would still gate the batch.

`FeatureStreamEndpointsTests` deliberately stays in `Server Features Misc`. It
is the single heaviest class in the suite (median 12.2 min of that shard's 26.6
min); folding it into the new Streaming child would produce a ~33-minute shard
and recreate the tail this change removes.

### Expected before/after

* **Batch critical path (this family):** 44.05 min test / 51.1 min job →
  ~25.3 min test / ~32 min job at p90. With `Server Features Misc` at ~28.4 min
  test, the batch `server-tests` tail moves from ~51 min to ~36 min of job wall
  clock.
* **Runner minutes:** one additional job. GitHub bills whole minutes per job, so
  the family goes from one ~51-minute job to a ~32-minute and a ~28-minute job
  running in parallel: roughly +9 rounded runner minutes on a full run, and
  fewer than that on targeted runs where only one child is selected (a
  Streaming *test* change now wakes the Streaming child and the two catch-alls,
  but no longer the 25-minute enrichment/sharing child).
* **Censored-timeout risk:** the `capacity_exhausted` sample that escalated a
  whole batch on run `31931541793` had no attributable owner. Both children now
  keep real headroom — 9.3 minutes for the Streaming child (20.7 p90 against a
  30-minute cap) and 12.7 for the enrichment/sharing child (25.3 against 38) —
  where the parent had none.

### Rollback

Delete both children from `.github/ci-shards.json`, delete the
`Server Features Data and Sharing` entry from `shard_partitions`, and restore a
single shard named `Server Features Data and Sharing` with `artifact_suffix`
`server-features-data-sharing`, `log_name`
`server-tests-server-features-data-sharing`, `timeout_minutes` 60,
`test_timeout_minutes` 48, the pre-#3229 `paths` list (the broad
`tests/dotnet/Honua.Server.Tests/Features/` prefix plus the 33 inherited source
prefixes listed in that shard's `_paths_comment`), and the filter recorded
verbatim as that partition's parent filter. Then, in
`scripts/ci/validate-ci-router.sh`, rename the three fixtures that carry the
child's name back to the parent (`zarr-server-source-excludes-*`,
`data-enrichment-source-retains-owner`, `core-capability-registry-targeted`),
drop the three fixtures added here (`streaming-source-exact-owners` reverts to
its two-owner form, and `streaming-test-exact-owners` /
`streaming-test-excludes-data-enrichment-sharing` are deleted), and restore the
five redundant `zarr-server-source-excludes-*` assertions if you want them back.

### Follow-up (not done here)

The shard's wall clock is dominated by fixture work, not assertions. Summing
TRX test-body durations gives only ~16.4 CPU-minutes against ~42.4 minutes of
class intervals, so roughly 60% of the shard is per-test host/fixture setup
inside serialized collections. `FeatureStreamSnapshotEndpointsTests` is the
clearest case: 34 tests, 322 s of test-body duration, 779 s of wall interval.
A shared/pooled fixture for the streaming and sharing collections would cut the
shard far more than any further re-partitioning. That is a test-code change and
is deliberately out of scope for this CI-config PR.

Two other shards are over the 80% warn line on the same evidence pass
(`audit-shard-headroom.py` over runs `31939301584` and `31921511274`) and need
their own tickets rather than a scope grab here:

| Shard | p50 | p90 | Cap | p90 / cap |
|---|---:|---:|---:|---:|
| Server Features Admin Operations | 29.2 | 29.2 | 35 | 83% |
| Server Features Collaboration Mobile and Identity | 16.5 | 17.7 | 22 | 80% |

TODO: file a rebalance issue for `Server Features Admin Operations` and one for
`Server Features Collaboration Mobile and Identity`, and link them here. Both
need a 20-run sample of their own before a cap or a split is chosen; neither is
on the batch critical path today, so neither blocks #3229.
