# CI workflow measurement method

Compare completed workflow runs over fixed UTC windows, including days with zero
runs. Use comparable populations and include cancelled runs when measuring workflow
duration. Measure elapsed workflow time as `updated_at - run_started_at`; measure
job duration as `completed_at - started_at`, excluding skipped jobs. Include all job
attempts when measuring rerun cost.

Calculate percentiles by sorting observations and taking nearest rank `ceil(p * n)`.
Divide counts and total elapsed time by the complete window length for daily rates.
For full-matrix trunk comparisons, select scheduled `CI` runs on `trunk`; exclude
manual dispatches whose selective inputs are unavailable from the Actions runs API.
Bound the run cohort before fetching job attempts. Split large searches into daily
windows to avoid GitHub's 1,000-result search limit.

Historical observations and the exact queries used for the original baseline remain
in the [immutable CI baseline](https://github.com/honua-io/honua-server/blob/8dff1184cc1e9538e209e39fc1266b013a09fc72/docs/internal/ci/baseline-2026-08-29.md).
Store new measurement output in ignored `artifacts/` or CI assets.
