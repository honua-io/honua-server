# Evidence-driven CI baseline

This is the pre-change measurement for ADR-0074 and #3213. It was collected at
2026-08-14T02:23:39Z from the most recent 30 runs created on or after 2026-07-14
for each declared workflow. The machine-readable summary is
[`evidence/actions-baseline-2026-08-13.json`](evidence/actions-baseline-2026-08-13.json)
(canonical Git blob SHA-256
`8452667dceba5b71a98d41c9af564506a0d92b4e24863cd6df6e1341e8241ab4`).
The digest is over the committed LF-normalized blob, not a checkout transformed
by platform line-ending settings.

## Reproduce

The collector reads GitHub metadata only. It queries all job attempts, writes a
minimal normalized input envelope when requested, and renders JSON and Markdown
from the same observations.

```powershell
python scripts/ci/measure-actions-baseline.py `
  --workflow pr-gate.yml `
  --workflow merge-train.yml `
  --workflow ci.yml `
  --workflow codeql.yml `
  --workflow serving-image-boundary.yml `
  --workflow worker-gdal-image.yml `
  --created-after 2026-07-14 `
  --limit 30 `
  --api-workers 8 `
  --input-out actions-baseline-input.json `
  --json-out actions-baseline.json `
  --markdown-out actions-baseline.md `
  --format markdown
```

The compact normalized input is intentionally not committed because run and
job identifiers remain available through the summary and GitHub API, while the
input page is over 1 MiB. Re-running from a captured input uses `--fixture` and
does not call GitHub.

## Results

| Workflow | Runs | Success | Failure | Cancelled | Queue p90 | Successful critical p90 | First failure p50 | Raw runner min | Rounded Linux min | Cancelled runner min |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| CI | 30 | 12 | 12 | 5 | 0.1m | 88.6m | 19.1m | 30,744.63 | 31,896 | 5,390.32 |
| CodeQL | 30 | 10 | 0 | 20 | 0.2m | 22.4m | - | 192.90 | 213 | 28.87 |
| Merge Train | 30 | 19 | 3 | 7 | 0.2m | 54.9m | 60.8m | 316.70 | 328 | 19.05 |
| PR Gate | 30 | 6 | 2 | 22 | 0.3m | 25.4m | 23.7m | 212.55 | 228 | 64.13 |
| Serving Image Boundary | 30 | 1 | 0 | 28 | 0.3m | 142.5m | - | 208.05 | 224 | 65.53 |
| GDAL Worker Image | 30 | 1 | 0 | 29 | 0.1m | 14.9m | - | 33.05 | 49 | 18.17 |

Counts can sum to fewer than 30 terminal outcomes when a sampled run was still
in progress at collection time. Completed job durations from those active runs
are included in runner totals because that usage has already occurred. A later
post-change comparison must use the same terminal/outcome and observed-usage
rules or explicitly refresh both samples.

## Findings

1. **Batch CI dominates cost.** Thirty sampled CI runs consumed 30,744.63 raw
   runner-minutes across all attempts. Five canceled runs alone consumed
   5,390.32 runner-minutes.
2. **Failure feedback arrives long before the workflow stops.** The median
   first CI job failure appeared at 19.1 minutes, while successful critical-path
   p90 was 88.6 minutes. The train currently waits for terminal workflow state,
   so deterministic failures can continue charging unrelated shards.
3. **Review churn is visible in every per-head workflow.** PR Gate was canceled
   on 22/30 sampled runs and CodeQL on 20/30. These runs consumed work without
   producing final exact-head evidence.
4. **Native-image triggers are overwhelmingly superseded.** Serving Image
   Boundary was canceled on 28/30 runs and GDAL Worker Image on 29/30. The one
   successful Serving run occupied a 142.5-minute critical path.
5. **Controller completion is not fast failure feedback.** Merge Train's median
   time to first failure was 60.8 minutes. The state machine must make
   normalization visible and stop unrelated execution after a deterministic
   failure rather than treating full workflow completion as the first useful
   signal.

## Methodology and caveats

- Queue time is `created_at` to the earliest non-skipped job start across all
  attempts.
- Critical path is the earliest non-skipped job start to the latest job
  completion across all attempts.
- Runner time is the sum of each non-skipped job's observed
  `completed_at - started_at` duration, including completed jobs in a workflow
  that was still active at collection time. Parallel jobs intentionally add runner
  time while sharing wall time.
- Percentiles use nearest rank. Successful critical-path percentiles include
  only successful runs; failure latency uses the earliest failed/timed-out job.
- GitHub reports zero billable milliseconds for these public-repository timing
  endpoints. `Rounded Linux min` is therefore a transparent estimate that
  rounds each observed Linux/Ubuntu job up to a minute. It is not invoice data.
- All attempts are included. This exposes the cost of retry loops rather than
  presenting only the latest attempt.

The post-change report must retain this methodology, include at least 30 runs,
and compare admission, verification, first-failure, cancellation, and runner
time separately. A faster wall clock that increases runner minutes, or lower
runner minutes that weakens evidence, does not pass ADR-0074's promotion gate.
