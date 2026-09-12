# Load harness completion

The `soak` profile drives all eight existing `LoadTestScenarios` with 170
virtual users, a five-minute ramp-up, at least 3,600 seconds of steady state,
and a two-minute ramp-down. The candidate receipt is produced by
[capacity-soak-candidate.yml](../../../.github/workflows/capacity-soak-candidate.yml);
see its [operator instructions](../../../docs/ops/capacity-soak-receipt.md).

Bare numeric duration arguments are **seconds**: `--ramp-up 300` is five
minutes, just like `--ramp-up 300s`, `--ramp-up 5m`, or `--ramp-up 00:05:00`.
This matters because the candidate producer exports `RAMP_UP=300` and the
shell runner forwards it without a suffix. Parsing that number directly as
a .NET `TimeSpan` schedules 300 **days**, which caused the apparent soak hang.
Explicit day durations still use a suffix such as `1d`.

The CLI records session start, per-scenario progress each minute, reporting
shutdown, and receipt of final statistics. Progress request counts describe
the current reporting interval; only `--stats-out` contains aggregate run
statistics. Redirected runs disable the interactive console display.

The entire NBomber session has a wall-clock budget: the configured ramp-up,
steady state and ramp-down, plus the request deadline and two minutes for
initialization, draining and final reports. Exceeding it returns **124** and
prints the last observed lifecycle event. An incomplete or empty scenario
result returns **1** without writing `--stats-out`. The CLI removes an
existing statistics file before starting, so an interrupted repeat cannot
reuse an earlier result.

For `soak`, NBomber's default per-scenario 5,000-error early-stop limit is
raised to `int.MaxValue` so a failing candidate can still be measured for the
full window. This is a measurement-duration setting, not a success criterion:
every failure remains counted, the requested maximum failure rate still
determines the exit code, and the receipt producer applies the frozen SLO
thresholds. Other profiles retain NBomber's default early-stop behavior.

Run the completion regressions from the repository root:

```bash
HONUA_MSBUILD_NODE_CAP=4 dotnet test tests/dotnet/Honua.LoadTests -c Release
```

The HTTP fixtures execute the real CLI in a child process. They independently
count received requests and verify the aggregate and per-scenario counts,
duration, throughput denominator, latency bounds and run metadata. They cover
successful responses, headers followed by an unfinished body, more than
5,000 HTTP failures that must finish the soak window and exit red, and an
early-aborted quick run that must remove stale receipt input. A separate
blocked-session fixture verifies the outer deadline refuses a result.
The CLI fixtures use the producer's bare numeric argument form, and parser
tests independently assert the expected seconds for every duration option.

These short regressions establish the failure paths. A full-length candidate
run is still required as evidence that the harness completes at the declared
envelope; a completed run whose measured SLOs fail is a negative capacity
receipt, not capacity qualification.
