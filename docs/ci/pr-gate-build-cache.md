# PR Gate build cache

The repository variable `PR_GATE_BUILD_CACHE` controls the optional `bin`/`obj`
cache for PR Gate's build-and-tests job. It defaults off when unset. The workflow
passes `enable-build-cache` to `setup-dotnet-ci`, which owns the cache and runs
before `lean-gate`.

Set `PR_GATE_BUILD_CACHE` to `true` to start a trial. Delete the variable or set
it to `false` to roll back for subsequent runs; neither operation needs a code
change. This replaces the former `HONUA_PR_GATE_BUILD_CACHE` variable, which no
longer controls this workflow.

Promote only when **PR Gate p90 is strictly better over one week** against a
comparable cache-off window. Use the workflow-duration population and percentile
method in the [CI baseline](baseline-2026-08-29.md), with one-week windows. Cache-hit
rate is not the acceptance criterion: the #2708 lesson was that a good hit rate
can still regress time to first test. Include cache restore/save overhead on
hits and misses, disk usage, and key churn when interpreting the result. If p90
is not strictly better, roll back and leave caching off.
