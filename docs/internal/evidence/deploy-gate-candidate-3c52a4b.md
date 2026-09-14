# Deploy telemetry gate: exact-candidate certification on nightly 3c52a4b (#4617)

This records the live fault and recovery proof for honua-server#4617. The gate ran against a real
server image and a real telemetry backend, not a stub.

## What ran

| Item | Value |
|---|---|
| Lane | `Category=CandidateCertification` (`tests/dotnet/Honua.CloudIntegration.Tests/CandidateTelemetryGateCertificationTests.cs`) |
| Candidate image | `ghcr.io/honua-io/honua-server:nightly-aot-3c52a4b` = `sha256:54926040d8b543cac746289c601fb77202ef4446e00a22fa5539836e4bedc8bb` |
| Previous image | `ghcr.io/honua-io/honua-server:nightly-aot-7ba4226` = `sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd` (the manifest pin on 2026-09-14) |
| Control plane source | `3c52a4bffa8f9b8621a39a8868f839e0e605de78`, the candidate's own commit |
| Includes | #4662 (validity and bounds), #4741 (staged candidates, preset error rate), #4761 (probe client isolation), #4764 (missing-probe diagnostics) |
| Substrate | Real YARP rolling backend and front door, real PostGIS per revision, real Prometheus scraping only the candidate, Redis workflow store, Docker Engine 29.6.2 |
| Run date | 2026-09-14 (UTC) |

The command:

```bash
HONUA_CANDIDATE_IMAGE=ghcr.io/honua-io/honua-server:nightly-aot-3c52a4b \
HONUA_PREVIOUS_IMAGE=ghcr.io/honua-io/honua-server:nightly-aot-7ba4226 \
HONUA_TEST_REDIS_URL=localhost:56417 \
HONUA_CANDIDATE_RECEIPT_DIR=<dir> \
HONUA_CONTROL_PLANE_SHA=3c52a4bffa8f9b8621a39a8868f839e0e605de78 \
dotnet test tests/dotnet/Honua.CloudIntegration.Tests -c Release --filter Category=CandidateCertification
```

## Results

| Scenario | Result | Numbers | Receipt |
|---|---|---|---|
| Healthy candidate, controller restart mid-window | **Passed**, committed | Staged 5 cycles with no exposure stamp. Exposure stamped once at cutover (`07:38:50Z`) and unchanged across the restart. Standby container ran `sha256:54926040…`. Gate read 547 samples, error rate **0**, p95 17 ms. Front door 644/644 → 200. | [`…-healthy-candidate-commit.json.gz`](deploy-gate-candidate-3c52a4b-healthy-candidate-commit.json.gz) |
| Candidate database refuses connections after cutover | **Passed**, rolled back on the breach | Rollback phase: "telemetry detected canary degradation: error rate 0.965 exceeded threshold 0.05". Prometheus read error rate 1.0, 51 samples, p95 78 ms. Rolled back **23.9 s** after the fault. The front door saw 262 × 503 during the regression; the previous revision served afterwards. | [`…-candidate-error-regression-rollback.json.gz`](deploy-gate-candidate-3c52a4b-candidate-error-regression-rollback.json.gz) |
| Candidate never healthy | **Passed**, rolled back without activation | "never passed the backend health gate within the 90-second exposure deadline". Rolled back 94.3 s after the fault. No timeline entry carried an exposure stamp. Front door 1479/1479 → 200. | [`…-never-healthy-candidate-rollback.json.gz`](deploy-gate-candidate-3c52a4b-never-healthy-candidate-rollback.json.gz) |
| Prometheus removed after cutover (rerun) | **Passed**, rolled back on missing evidence | The window stayed `telemetry-evidence-pending` for 15 cycles past its deadline and never committed. It then rolled back "beyond the configured evidence grace window", 54.2 s after the fault. Front door 1076/1076 → 200. | [`…-telemetry-outage-rollback-rerun.json.gz`](deploy-gate-candidate-3c52a4b-telemetry-outage-rollback-rerun.json.gz) |

TRX counters: full run 4 total, 3 passed, 1 failed. Solo rerun of the outage scenario: 1 total, 1 passed.

### The failed first outage run

In the full run, the outage scenario failed one assertion outside the gate: the post-recovery front-door
`/healthz/ready` answered 429, and 213 of 1126 traffic requests got 429. The gate itself behaved as
specified. The window held `telemetry-evidence-pending` past its deadline, never committed, and rolled
back 54.7 s after the fault on the evidence grace bound
([`…-telemetry-outage-rollback.json.gz`](deploy-gate-candidate-3c52a4b-telemetry-outage-rollback.json.gz)).

The 429 envelope comes from `RateLimitingMiddleware`. The shipped `appsettings.Security.json` enables
it with a Redis-backed limit of 1000 requests per minute, and both revisions share one Redis. That
configuration is identical at `a8bcf52`, `7ba4226` and `3c52a4b`. The scenario reran alone with no
change and passed with no 429s. The other three scenarios assert no front-door failures, or none after
recovery, and passed.

## Why the previous failure is fixed

The 2026-09-13 run of this lane had the control plane at `a8bcf52`, before #4761. There the
error-regression scenario rolled back on missing evidence, because the standby probes had opened the
telemetry client's shared circuit breaker. On `3c52a4b` the same scenario rolls back on the measured
breach, in 23.9 s instead of 54 s.

## Coverage against the acceptance criteria

| Criterion | Live proof here | Fast proof (PR Gate) |
|---|---|---|
| Invalid data never satisfies a requirement | The outage scenario never commits on absent evidence. The healthy scenario shows a real 0 error rate passing. | `DeployTelemetrySignalEvaluatorTests`, Prometheus, CloudWatch and Azure Monitor provider negatives |
| Policy validated before mutation | — | `DeployWorkflowServiceTests` plan blocks, `DeployControlEndpointsTests` |
| Warmup anchored on exposure and persisted | Healthy: stamp at cutover, unchanged across restart | Reconciler post-activation tests |
| Bounded pre-exposure hold, bounded post-exposure recovery | Never-healthy (exposure deadline); outage (evidence grace) | Evaluator and reconciler health-gate tests |
| Health-only profile, no silent degradation | — | Policy and plan tests |
| Readiness and correctness probes | Staged standby checks before cutover in every scenario | Probe tests, `ControlPlaneHttpClientsTests` |
| Provider and endpoint negatives, outages, delayed exposure, restart | All four scenarios | The suites above |
