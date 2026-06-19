# Demo B — Ops Champion Recording Runbook

> **Audience for the recording:** the platform/SRE/DevOps "ops champion" who
> decides whether Honua is something their team is happy to *operate*. The story
> is **"a geospatial server that behaves like modern infrastructure"** — one
> container, scale-to-zero, secured by default, observable, benchmarked,
> Helm/GitOps-shippable, and driven by an AI DevOps agent that plans changes you
> approve.
>
> **Audience for this document:** the founder doing the screen-record. Every
> beat below gives you the exact command/URL, what to show, the expected output
> (captured from a real run — see "Verified" stamps), and a one-line "why the
> ops champion cares."

## Status legend

- **REAL-today** — shipped on `trunk` / live on the demo right now; record it live.
- **Preview** — implemented on a feature branch or behind a default-off toggle;
  real code, not yet on `trunk` or not enabled on the live demo. Record from the
  branch worktree or narrate it; say "shipping."
- **Placeholder** — being built; clearly marked, do not record as done.

## Live demo + the admin key (read this first)

- Live demo: **https://demo.honua.io** — one container on AWS Lambda, render +
  query working. It is **scale-to-zero**, so the *first* request after idle is a
  cold start (~2–3 s). **Warm it before you hit record** (see Pre-flight).
- The admin surface is gated by an `X-API-Key` header. The key is the value of
  the AWS Secrets Manager secret **`honua-demo-demo/admin-password`**. **Never
  type or paste the key on screen.** Export it into your shell *off-camera*:

  ```bash
  export HONUA_DEMO_API_KEY="$(aws secretsmanager get-secret-value \
    --secret-id honua-demo-demo/admin-password --query SecretString --output text)"
  ```

  Then every key-gated `curl` uses `-H "X-API-Key: $HONUA_DEMO_API_KEY"` and the
  key never appears in the capture.

## Pre-flight (off-camera, ~2 min before recording)

Run the bundled probe helper to warm the Lambda and confirm every beat will
return what this runbook says it does:

```bash
export HONUA_DEMO_API_KEY="$(aws secretsmanager get-secret-value \
  --secret-id honua-demo-demo/admin-password --query SecretString --output text)"
./docs/internal/demo/scripts/demo-b-probes.sh
```

Expect `ALL PROBES OK`. (Verified 2026-06-17 — full output in
[`scripts/demo-b-probes.sh`](scripts/demo-b-probes.sh) header; the script is the
single source of truth for the live probes in Beats 1–3 and 6.)

---

## Recording sequence

Full run is ~8 beats. For a **5–8 min cut**, record Beats 1, 2, 3, 6 live and
splice pre-rendered captures for 4 (geobench), 5 (Helm), 7 (release). Beat 8 is a
placeholder — mention it as "what's next," don't demo it.

| # | Beat | Status | Live or pre-rendered | Compress for short cut? |
|---|------|--------|----------------------|-------------------------|
| 1 | Stand-up / serverless | REAL-today | Live | Keep |
| 2 | Security posture | REAL-today | Live | Keep |
| 3 | Telemetry / observability | REAL-today (live) + Preview (CW dashboard/X-Ray) | Live | Keep |
| 4 | geobench | REAL-today | Pre-rendered | Splice 15 s |
| 5 | Container upgrade (Helm/AKS) | REAL-today | Pre-rendered terminal | Splice 20 s |
| 6 | GitOps + AI DevOps | REAL-today (deploy-control, plan mode) + Preview (Bedrock, actuation, Console approval) | Live (server) + branch worktree (agent) | Keep |
| 7 | Release cadence + deliverables | REAL-today | Pre-rendered | Splice 15 s |
| 8 | Flagship: safe layer evolution | **Placeholder** | — | Narrate only |

---

## Beat 1 — Stand-up / serverless  **[REAL-today]**

**Why the ops champion cares:** "I don't want to babysit a cluster for a
geospatial server." This is *one container* on Lambda that scales to zero — no
idle compute bill (~$25/mo for the demo footprint), and it answers a standard
liveness/readiness contract their existing tooling already knows how to probe.

**What to show on screen:** a terminal running two health probes, then the one
Prometheus line that proves it's literally running on Lambda.

```bash
curl -i https://demo.honua.io/healthz/live
curl -i https://demo.honua.io/healthz/ready
```

**Expected output (verified live 2026-06-17):**

```
HTTP/2 200
Healthy
...
HTTP/2 200
Ready
```

**The serverless cost story (say this over the terminal):**

- demo.honua.io is **one Lambda container image + API Gateway HTTP API**, backed
  by a `db.t3.micro` RDS PostgreSQL. Scale-to-zero ⇒ no standing compute
  reservation; the demo footprint runs **~$25/mo**.
- Source of truth: the Terraform module
  `honua-iac/infrastructure/terraform/modules/aws-serverless/main.tf` (Lambda
  container, API Gateway, RDS, optional ElastiCache Redis).
- The health endpoints are mapped in
  `honua-server/src/Honua.Server/Features/HealthCheck/HealthEndpoints.cs`:
  `/healthz/live` (process liveness) and `/healthz/ready`
  (`IReadinessCheckService` — migrations applied, providers reachable).

**Proof it's serverless (key-gated, shown in Beat 3):** the Prometheus scrape
exposes `honua_lambda_memory_limit_mib_MiB{function_name="honua-demo-demo-honua",...} 2048`.
You can flash it here or save it for Beat 3.

---

## Beat 2 — Security posture  **[REAL-today]**

**Why the ops champion cares:** "Before this goes near my network, prove it's not
a liability." Honua ships **secure-by-default**: hardened response headers on
every request, TLS via CloudFront, an authenticated admin gate, and a nightly
supply-chain scan — without anyone toggling anything on.

**Show 1 — security headers + TLS:**

```bash
curl -I https://demo.honua.io/
```

**Expected output (verified live 2026-06-17 — abridged):**

```
HTTP/2 404
server: Kestrel
x-frame-options: DENY
x-content-type-options: nosniff
x-xss-protection: 1; mode=block
cross-origin-opener-policy: same-origin
strict-transport-security: max-age=63072000; includeSubDomains; preload
referrer-policy: strict-origin-when-cross-origin
via: 1.1 ...cloudfront.net (CloudFront)
```

(The `404` is just the bare root path having no landing page — the *headers* are
the point. HSTS = 2 years + preload, frames fully denied, COOP isolates the
context, MIME-sniffing off.) Headers come from
`honua-server/src/Honua.Server/Features/Infrastructure/Middleware/SecurityHeadersMiddleware.cs`.

**Show 2 — the admin gate (401 → 200):**

```bash
# No key → rejected
curl -s -o /dev/null -w "%{http_code}\n" https://demo.honua.io/metrics
# With key → allowed   (key is in $HONUA_DEMO_API_KEY, never on screen)
curl -s -o /dev/null -w "%{http_code}\n" \
  -H "X-API-Key: $HONUA_DEMO_API_KEY" https://demo.honua.io/metrics
```

**Expected output (verified live 2026-06-17):**

```
401
200
```

The gate is `X-API-Key` validated with a constant-time compare in
`honua-server/src/Honua.Hosting/Features/Authentication/ApiKeyAuthenticationHandler.cs`
(env var `HONUA_ADMIN_PASSWORD`, sourced from the
`honua-demo-demo/admin-password` secret). The admin metrics group applies
`RequireAdminAuthorization()` in
`.../Features/Infrastructure/Monitoring/MetricsEndpoints.cs`.

**Show 3 — supply chain (point, don't run):** open
`honua-server/.github/workflows/security-nightly.yml` on GitHub. Nightly
(`0 2 * * *`) it runs **Trivy** (filesystem + container image), **SBOM**
(SPDX JSON), NuGet vulnerability scan, Hadolint, and container runtime
constraint tests — uploading SARIF to GitHub code scanning. **CodeQL** runs in
its own `.github/workflows/codeql.yml`.

---

## Beat 3 — Telemetry / observability  **[REAL-today live; CW dashboard + X-Ray Preview]**

**Why the ops champion cares:** "If I can't see it, I can't run it." Honua emits
standard Prometheus metrics *and* a structured admin metrics API — drop-in for an
existing Grafana/Prometheus stack — plus first-class AWS observability when you
want it.

**Show 1 — Prometheus (key-gated):**

```bash
curl -s -H "X-API-Key: $HONUA_DEMO_API_KEY" https://demo.honua.io/metrics | head -20
```

**Expected output (verified live 2026-06-17 — sample):**

```
# TYPE honua_db_connections_active gauge
# HELP honua_lambda_memory_limit_mib_MiB Configured Lambda memory limit in MiB (0 when not running on Lambda).
honua_lambda_memory_limit_mib_MiB{function_name="honua-demo-demo-honua",init_type="on-demand",memory_limit_mib="2048"} 2048 ...
process_memory_usage_bytes{...} 304046080 ...
process_cpu_time_seconds_total{...,process_cpu_state="user"} 1.044188 ...
```

That `honua_lambda_*` family is the callback to Beat 1 — the metrics endpoint
itself confirms the serverless runtime. Registered via `MapPrometheusEndpoint()`
in `honua-server/src/Honua.ServiceDefaults/Extensions.cs`.

**Show 2 — structured admin metrics API:**

```bash
curl -s -H "X-API-Key: $HONUA_DEMO_API_KEY" \
  https://demo.honua.io/api/v1/metrics/health | jq .
```

**Expected output (verified live 2026-06-17):**

```json
{"status":"healthy","memoryUsageMB":56.04,"memoryPressurePercent":11.0,
 "gcCollections":27,
 "migration":{"status":"succeeded","isReady":true,"message":"No pending migration scripts."}}
```

Sibling routes (same admin gate) in
`.../Features/Infrastructure/Monitoring/MetricsEndpoints.cs`:
`/api/v1/metrics/{health,performance,database,cache,memory,streaming}`.

**Show 3 — AWS-native observability (Preview — narrate):** the serverless module
now ships two opt-in toggles in
`honua-iac/.../modules/aws-serverless/variables.tf`:

- `enable_dashboard` (default `false`) — a **CloudWatch dashboard** for the
  Honua Lambda: duration / errors / throttles / concurrency + cold-start +
  custom Honua metrics.
- `enable_xray_tracing` (default `false`) — **AWS X-Ray** active tracing with
  least-privilege `xray:PutTraceSegments` / `GetSamplingRules` IAM, paired with
  the app-side `Tracing__XRay__Enabled` flag.

> Status: these live on the `feat/cloudwatch-dashboard` branch in `honua-iac`,
> default-off. Frame as **"flip one Terraform var for full CloudWatch + X-Ray"**,
> shipping — don't claim it's live on the demo.

---

## Beat 4 — geobench  **[REAL-today — pre-render]**

**Why the ops champion cares:** "Is it actually fast, or just new?" geobench runs
each server in full isolation (its own PostGIS, no shared buffers) against the
same data and publishes the numbers. We benchmark ourselves honestly and ship
the harness publicly.

**What to show on screen:** the one command, then the headline table from the
generated `report.md`. Pre-render this — a full run is minutes of k6 load.

**Command (verified-run 2026-06-17):**

```bash
cd geobench
SERVERS="honua" TESTS="attribute-filter" ./scripts/run-benchmark.sh
# (the recording was captured with RUNS=2 for speed; default is RUNS=5)
```

The harness stands up an isolated PostGIS, loads **100,000** `bench_points`,
publishes them as a Honua layer with expression indexes "to match Honua's real
OGC query shape," runs the k6 `attribute-filter` scenarios, tears down, then
emits `results/<timestamp>/report.md`.

**Headline numbers (captured from the real run — `honua` / `attribute-filter`):**

<!-- GEOBENCH_RESULTS_START -->
Verified run 2026-06-17 — dataset: small (100K points), 2 runs (median
reported), images `honuaio/honua-server:latest` + `postgis/postgis:17-3.5` +
`grafana/k6:0.54.0`, `baseline` cache tier (no response caching), pool size 6:

| Query type | req/s | p50 (ms) | p95 (ms) | p99 (ms) | error % |
|---|---:|---:|---:|---:|---:|
| equality (CQL2 `=`) | 1677.8 | 2.1 | 9.5 | 30.8 | 0 |
| range (numeric)     | 1760.1 | 1.9 | 8.6 | 33.5 | 0 |
| like (literal prefix) | 2007.0 | 2.3 | 7.9 | 33.4 | 0 |

Headline to say on screen: **~1.7–2.0k attribute-filter req/s, sub-2.5 ms p50,
single-digit-ms p95, zero errors** — on a single small node with no response
caching. (Full run: `results/<ts>/report.md`; the raw k6 run logged 979,988
requests at 2,333 req/s with 0.00% errors before the per-variant median split.)
<!-- GEOBENCH_RESULTS_END -->

**On-screen takeaway:** "Same data, isolated stack, published harness — these are
our own numbers and you can re-run them."

---

## Beat 5 — Container upgrade (Helm + AKS)  **[REAL-today — pre-render terminal]**

**Why the ops champion cares:** "Can I run this in *my* Kubernetes the way I run
everything else — Helm, GitOps, with a clean rollback?" Yes: a linted chart,
safe-by-default pod security, schema-guarded values, and a smoke harness that
proves an install→upgrade→rollback lifecycle before it touches prod.

**Show 1 — lint + render (verified-run 2026-06-17):**

```bash
cd honua-helm
helm lint honua -f honua/ci-values/base.yaml
helm template honua ./honua -f honua/ci-values/base.yaml | grep '^kind:' | sort | uniq -c
```

**Expected output (verified):**

```
==> Linting honua
[INFO] Chart.yaml: icon is recommended
1 chart(s) linted, 0 chart(s) failed

      2 kind: ConfigMap
      1 kind: Deployment
      1 kind: Job
      1 kind: Pod
      2 kind: Secret
      1 kind: Service
      1 kind: ServiceAccount
```

**Show 2 — safe-by-default securityContext** (rendered, verified):

```yaml
securityContext:
  runAsNonRoot: true
  runAsUser: 1001
  allowPrivilegeEscalation: false
  readOnlyRootFilesystem: true
  capabilities:
    drop: [ALL]
```

From `honua-helm/honua/values.yaml`. Values are guarded by
`honua-helm/honua/values.schema.json` (e.g. image digest must match
`^sha256:[0-9a-f]{64}$`).

**Show 3 — AKS smoke harness (install→upgrade→rollback), dry-run (verified-run):**

```bash
cd honua-helm
scripts/aks-smoke.sh --dry-run     # render + lint + kubeconform, no cluster
```

**Expected output (verified):**

```
== DRY RUN: render + lint only, no AKS cluster contacted ==
+ helm lint  .../honua -f .../aks-smoke-install.yaml ...   1 chart(s) linted, 0 chart(s) failed
+ helm lint  .../honua -f .../aks-smoke-upgrade.yaml ...   1 chart(s) linted, 0 chart(s) failed
+ helm template honua ... -f .../aks-smoke-install.yaml ...
+ helm template honua ... --is-upgrade -f .../aks-smoke-upgrade.yaml ...
Dry-run evidence written to: .../evidence/aks-<ts>
```

**The lifecycle path (narrate):** against a real cluster the harness installs the
chart, `helm test`s readiness, then renders the **upgrade** overlay
(`--is-upgrade`); the rollback path is the standard `helm rollback honua <rev>`
documented in `honua-helm/docs/MIGRATION.md` (`helm history` → `helm rollback`).
"Every upgrade has a one-command undo, and we smoke that path in CI."

---

## Beat 6 — GitOps + AI DevOps  **[REAL-today: deploy-control + plan mode; Preview: Bedrock, actuation, Console approval]**

**Why the ops champion cares:** "An AI touching my deploys terrifies me unless
it's plan-first and I approve." Honua's DevOps agent **defaults to plan mode** —
it proposes a GitOps rollout, you review, and *actuation only happens through the
server's admin deploy-control endpoints after explicit approval.* Safe by
construction.

**Show 1 — the agent plans a rollout (plan mode is the default).**

> The agent lives in **`honua-devops`** (`src/Honua.DevOps.Agent`), not
> honua-server. The Bedrock provider + GitOps actuation are on the
> `feat/devops-agent-gitops-actuation` branch (commits
> `add AWS Bedrock (Claude) provider (#93)` and
> `actuate GitOps sync/promote/rollback via server deploy-control`) — **Preview**,
> record from that branch worktree.

```bash
cd honua-devops
# Plan mode is the default (HONUA_DEVOPS_EXECUTION_MODE defaults to "plan").
dotnet run --project src/Honua.DevOps.Agent -- \
  --prompt "plan a GitOps rollout of roads-api dev->staging"
```

**What to show:** the agent's startup banner makes the safety posture explicit —
`mode=plan` is printed, and it produces a plan (no mutation). The provider is
selectable with `--provider <codex|claude|local-llama|bedrock>`; **`bedrock`
runs Claude on Amazon Bedrock via the Converse API using the AWS IAM credential
chain** (model from `HONUA_DEVOPS_BEDROCK_MODEL`). Say: *"same agent, now with a
Bedrock provider, so ops teams standardized on AWS keep their model in their
account."*

**Show 2 — the server-side actuation surface (REAL-today, live on the demo).**
This is the honest framing of "actuation": the agent never pokes prod directly —
it goes **plan → approve → execute via the server's admin deploy-control
endpoints**, which are gated by the same `X-API-Key`.

```bash
# Coordinated-deploy readiness probe (gated; 401 without key)
curl -s -o /dev/null -w "%{http_code}\n" https://demo.honua.io/api/v1/admin/deploy/preflight
curl -s -H "X-API-Key: $HONUA_DEMO_API_KEY" \
  https://demo.honua.io/api/v1/admin/deploy/preflight | jq .
```

**Expected output (verified live 2026-06-17):**

```
401
{"status":"ready","readyForCoordinatedDeploy":true,
 "message":"Instance is ready for coordinated deployment."}
```

The deploy-control endpoints are on `trunk` in
`honua-server/src/Honua.Server/Features/Admin/DeployControlEndpoints.cs`
(all under `/api/v1/admin/deploy`, all `RequireAdminAuthorization()`):

| Method | Route | Purpose |
|---|---|---|
| GET  | `/preflight` | readiness for a coordinated deploy |
| POST | `/plan` | produce a deploy plan |
| POST | `/operations` | create a deploy operation (201) |
| GET  | `/operations/{id}` | inspect an operation |
| POST | `/operations/{id}/submit` | submit for execution |
| POST | `/operations/{id}/rollback` | roll an operation back |

**Show 3 — Console approval surface (Preview — narrate):** the human-in-the-loop
approval UI in `honua-console` is being wired (server task "Approval loop +
Console approval UI", in progress). Frame as **"approvals land in the Console;
the agent waits for the green light."** Do not record a finished UI.

**The honest one-liner:** *"The AI plans. A human approves. The server executes
and can roll back. The agent has no path to prod that bypasses that gate."*

---

## Beat 7 — Release cadence + deliverables  **[REAL-today — pre-render]**

**Why the ops champion cares:** "Will upgrades be a fire drill?" Every release
candidate is validated against a manifest of gates and lanes, and ships with a
machine-readable **evidence bundle** — so an upgrade comes with proof, not vibes.

**Show 1 — the release-bundle pipeline smoke (honua-server, verified-run 2026-06-17):**

```bash
cd honua-server
./scripts/release/smoke-release-bundle-pipeline.sh
```

Validates the bundle registry + evidence merge + manifest generator
consistency (`collect-evidence.sh` → `build-release-manifest.sh`).

**Expected output (verified):**

```
[OK] registry valid
[OK] evidence merged (gates=6 lanes=8)
[OK] manifest built; all server gates passed
release-bundle pipeline smoke check passed.
```

**Show 2 — RC validation against the train manifest (honua-devops):**

```bash
cd honua-devops
./scripts/compat-train-release-validation.sh \
  ../honua-server/release/honua-<train-id>.json
```

This consumes the canonical release-train manifest and evaluates every signal —
`releaseGates[]`, `repositoryLanes[]`, `releaseLaneCriteria[]`, and the immutable
`candidate.image` — emitting per-check `[PASS]`/`[WAIVED]`/`[FAIL]` plus a
`release-validation-bundle.json` evidence file.

**Context (devops#41 / #91):** devops**#41** is the manifest-driven
release-candidate validation gate for the compatibility train; devops**#91**
extends it with **active live-probe re-verification** (ensures the evidence is
current, not stale) and RC image validation. On-screen takeaway: *"the release
gate is the same machinery, and it hands you the evidence bundle."*

> Sample evidence bundle:
> `honua-devops/compatibility/scoreboard/release-validation-2026-05-preview.json`.
> The first release train is `honua-2026-05-preview`.

---

## Beat 8 — Flagship: Safe layer evolution with reversible rollback  **[PLACEHOLDER — being built]**

> **DO NOT RECORD AS DONE.** This is the flagship ops moment we're building: a
> schema/layer change applied as an **additive, reversible** operation, with a
> one-click rollback that restores the prior layer shape — the geospatial
> equivalent of a database migration with a guaranteed `down`.
>
> **Why it will matter to the ops champion:** "Evolving a production layer is the
> scariest thing I do. If Honua makes it additive-by-default and reversible, that
> changes the risk math."
>
> **Status:** additive path under active development. When ready, this section
> slots in after Beat 6 (it builds directly on the deploy-control
> plan→approve→execute→rollback spine). Until then, narrate it as **"what's
> next"** and stop.
>
> _Recorder: leave this beat out of the cut entirely, or end on it as a
> roadmap teaser. Do not show a half-built UI._

---

## Appendix — what was verified-run for this runbook

Captured 2026-06-17 against live `https://demo.honua.io` and local tooling:

- **Beats 1, 2, 3, 6 (live):** all probes green via
  [`scripts/demo-b-probes.sh`](scripts/demo-b-probes.sh) → `ALL PROBES OK`
  (health 200/200; 5 security headers present; `/metrics` 401→200;
  `honua_lambda_*` metric present; `/api/v1/metrics/health` 200; MapServer export
  → 400×400 PNG; `maui-roads` FeatureServer count = **7071**; deploy preflight
  401→`readyForCoordinatedDeploy:true`).
- **Beat 4 (geobench):** `SERVERS="honua" TESTS="attribute-filter"` run executed
  to completion (exit 0; 100k features, 2 runs, ~980k requests/run, 0.00%
  errors); per-variant medians in the Beat 4 table (equality 1677.8 req/s, range
  1760.1, like 2007.0 req/s; p95 7.9–9.5 ms).
- **Beat 5 (Helm):** `helm lint` → `0 chart(s) failed`; `helm template` → 9
  objects; rendered securityContext confirmed restrictive; `aks-smoke.sh
  --dry-run` → exit 0, install+upgrade overlays rendered.
- **Beat 7 (release):** `smoke-release-bundle-pipeline.sh` → `gates=6 lanes=8`,
  "manifest built; all server gates passed", exit 0.
- **Code/config presence confirmed on `trunk`:** health endpoints, security
  headers middleware, API-key handler, metrics endpoints, deploy-control
  endpoints, `security-nightly.yml`, `codeql.yml`, release-bundle scripts.
- **Preview (branch / default-off, not on trunk):** Bedrock provider + GitOps
  actuation (`feat/devops-agent-gitops-actuation` in honua-devops); CloudWatch
  dashboard + X-Ray toggles (`feat/cloudwatch-dashboard` in honua-iac); Console
  approval UI (in progress).
