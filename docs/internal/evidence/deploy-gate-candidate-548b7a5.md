# Deploy telemetry gate: exact-candidate certification on the 2026.1 pin 548b7a5 (#4617)

This is the candidate-bound acceptance for honua-server#4617, replayed against the **manifest-pinned**
2026.1 candidate. The earlier run in
[`deploy-gate-candidate-3c52a4b.md`](deploy-gate-candidate-3c52a4b.md) proved the gate on a nightly that
contained every fix; this one proves it on the digest the release actually ships.

## What ran

| Item | Value |
|---|---|
| Lane | `Category=CandidateCertification` (`tests/dotnet/Honua.CloudIntegration.Tests/CandidateTelemetryGateCertificationTests.cs`) |
| Candidate image | `ghcr.io/honua-io/honua-server:nightly-548b7a5` = `sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`, the accepted pin (honua-release#349) |
| Previous image | `ghcr.io/honua-io/honua-server:nightly-aot-7ba4226` = `sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd`, the pin this candidate replaces |
| Control plane source | This branch. Every file under `src/Honua.Server/Features/ControlPlane/` is identical to the pin `548b7a5` except the two this PR changes (`YarpRollingDeployBackend.cs`, `SelfHostedRollingProxy.cs`), which add the mount seam the run needs. |
| Pin contains | #4662 `375cfd0`, #4741 `944fefc`, #4759 `008cda5`, #4761 `e848cdd`, #4764 `63834fa`, #4844 `3d82e84` — each verified an ancestor of `548b7a5` |
| Substrate | Real YARP rolling backend and front door, real PostGIS per revision, real Prometheus scraping only the candidate, Redis workflow store, Docker Engine 29.6.2 |
| Run date | 2026-09-15 (UTC) |

```bash
HONUA_CANDIDATE_IMAGE=ghcr.io/honua-io/honua-server:nightly-548b7a5 HONUA_PREVIOUS_IMAGE=ghcr.io/honua-io/honua-server:nightly-aot-7ba4226 HONUA_TEST_REDIS_URL=<host:port> HONUA_CANDIDATE_RECEIPT_DIR=<dir> HONUA_CONTROL_PLANE_SHA=<sha> dotnet test tests/dotnet/Honua.CloudIntegration.Tests -c Release   --filter "Category=CandidateCertification&FullyQualifiedName~CandidateTelemetryGateCertification"
```

## Results

**TRX: 4 total, 4 passed, 0 failed.**

| Scenario | Result | Numbers | Receipt |
|---|---|---|---|
| Healthy candidate, controller restart mid-window | **Passed**, committed | 9 staged cycles carried no exposure stamp. Exposure stamped once at cutover (`04:53:20Z`) and unchanged across the controller restart. The standby container ran `sha256:29974ee7…c675c1`, the pinned digest itself. Gate read 320.9 samples, error rate **0**, p95 69.9 ms. Front door 604/604 → 200, 0 failures. | [`…-healthy-candidate-commit.json.gz`](deploy-gate-candidate-548b7a5-healthy-candidate-commit.json.gz) |
| Candidate database refuses connections after cutover | **Passed**, rolled back on the breach | Prometheus read error rate **1.0** over 46.4 samples, p95 44.2 ms. Rolled back **26.1 s** after the fault, on the measured breach rather than on missing evidence. The front door saw 215 × 503 during the regression; the previous revision served afterwards. | [`…-candidate-error-regression-rollback.json.gz`](deploy-gate-candidate-548b7a5-candidate-error-regression-rollback.json.gz) |
| Candidate never healthy | **Passed**, rolled back without activation | "never passed the backend health gate within the 90-second exposure deadline". `trafficExposedAt` stayed null: the candidate was never exposed. Front door 1270/1270 → 200, 0 failures. | [`…-never-healthy-candidate-rollback.json.gz`](deploy-gate-candidate-548b7a5-never-healthy-candidate-rollback.json.gz) |
| Prometheus removed after cutover | **Passed**, rolled back on missing evidence | The window held `telemetry-evidence-pending` past its deadline and never committed. Gate evidence is null in the receipt — sample count, error rate and p95 all absent, so nothing could satisfy the requirement. Rolled back on the evidence grace bound **58.5 s** after the fault. Front door 941/941 → 200. | [`…-telemetry-outage-rollback.json.gz`](deploy-gate-candidate-548b7a5-telemetry-outage-rollback.json.gz) |

Receipts are JSON with image ids and repo digests, the standby container image id, the operation
timeline, fault and recovery times, the gate's evidence read straight from Prometheus, and front-door
status counts.

## What the first attempt on this pin found

The lane's first run against `548b7a5` was **1 of 4**, and the three failures were not in the gate. The
candidate standby exited **139** seconds after start, in every scenario that needs it to become healthy:

```
Unhandled exception. System.InvalidOperationException: 'Operations:SecretChannel:KeyRingCertificatePath'
is required when the durable operation secret channel is enabled.
   at Honua.Server.Features.Operations.OperationSecretKeyRingProtection.Resolve(IConfiguration)
```

`Program.cs` composes the durable operation secret channel whenever Redis is connected outside
Development and Test, and honua-server#4722 made the operator-supplied key-ring certificate mandatory
there. That startup requirement is intentional and stays as it is (honua-server#4885, #4902): harnesses
mint a throwaway PKCS#12 and mount it.

The gap it exposed is that `YarpRollingDeployBackend` could only pass `env.` parameters to a replica. It
had no way to put a **file** in the container, so the shipped self-hosted rolling backend could not roll
out any 2026.1 image that connects to Redis in Production. Every standby would exit at startup, and the
rollout would fail at the exposure deadline reported as a candidate health failure rather than as
missing configuration. Confirmed both directions on the pinned digest: the replica environment alone
exits 139, and the same image with a mounted PKCS#12 reports `healthy` in 12 s.

This PR adds `mount.<container-path>=<host-path>` read-only mounts to that backend, validated at plan
time, and the lane mints and mounts a per-run key-ring certificate.

Worth recording separately: through all of it the gate itself behaved as specified. It refused to cut
over an unhealthy candidate and rolled back without activating it.

## Coverage against the acceptance criteria

| Criterion | Live proof here | Fast proof (PR Gate) |
|---|---|---|
| Invalid data never satisfies a requirement | Outage scenario: evidence absent, never committed. Healthy: a real 0 error rate passes, so 0 is read as a value and not as absence. | `DeployTelemetrySignalEvaluatorTests`, Prometheus / CloudWatch / Azure Monitor provider negatives |
| Policy validated before mutation | Unsatisfiable replica mounts block at plan time | `DeployWorkflowServiceTests`, `DeployControlEndpointsTests`, `YarpRollingDeployBackendTests` |
| Warmup anchored on exposure and persisted | Healthy: stamped once at cutover, unchanged across the controller restart | Reconciler post-activation tests |
| Bounded pre-exposure hold, bounded post-exposure recovery | Never-healthy (90 s exposure deadline, no activation); outage (evidence grace, 58.5 s) | Evaluator and reconciler health-gate tests |
| Health-only profile, no silent degradation | — | Policy and plan tests |
| Readiness and correctness probes | Staged standby checks ran before cutover in every scenario | Probe tests, `ControlPlaneHttpClientsTests` |
| Provider and endpoint negatives, outages, delayed exposure, restart | All four scenarios | The suites above |
