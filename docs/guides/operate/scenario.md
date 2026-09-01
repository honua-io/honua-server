# Operate scenario: evidence before action

This is the executable 2026.1 subset of the bounded Operate loop. Every command
below was run against local candidate `a3e1fd8ce` on September 1, 2026. A
successful request is not proof that its evidence is actionable: inspect the
server-authored `evidencePosture` before diagnosing or proposing anything.

The complete intended loop is:

`observe → deterministic finding → diagnose → sealed proposal → separate approval → typed actuator → verify`

Only the observe and fail-closed decision portions are currently executable end
to end on the certified Local Docker placement. The blocked steps are retained
below so their absence cannot be mistaken for product behavior.

## Start and pin the candidate

Use a dedicated checkout and record the full identity before starting it:

```bash
candidate_sha=a3e1fd8cee11e010d98844ab11c22b98134cc7ef
candidate_image="honua-server:operate-${candidate_sha}"
test "$(git rev-parse HEAD)" = "$candidate_sha"
GITHUB_ACTOR="${GITHUB_ACTOR:-$(gh api user --jq .login)}" GH_TOKEN=$(gh auth token) \
  bash scripts/docker/build-with-github-packages.sh -t "$candidate_image" .
HONUA_ENABLE_OBSERVABILITY_TEST_SEED=true \
HONUA_OPERATE_FIXTURE_SEED_ON_STARTUP=true \
POSTGRES_PORT=15432 REDIS_PORT=16379 \
HONUA_HTTP_PORT=18090 HONUA_GRPC_PORT=18091 \
HONUA_SERVER_IMAGE="$candidate_image" \
docker compose -p honua3302 up -d --no-build --wait --wait-timeout 180
test "$(docker inspect --format '{{.Config.Image}}' \
  "$(docker compose -p honua3302 ps -q honua)")" = "$candidate_image"
curl --fail http://localhost:18090/healthz/ready
```

The final command returned `Ready`. Use a secret-backed admin credential outside
local development; the examples assume `HONUA_ADMIN_PASSWORD` is already set.

```bash
export HONUA_URL=http://localhost:18090
export HONUA_KEY="$HONUA_ADMIN_PASSWORD"
```

## 1. Observe through REST

```bash
curl --fail --silent --show-error \
  -H "X-API-Key: $HONUA_KEY" \
  "$HONUA_URL/api/v1/admin/observability/ops-health" |
  jq '{generatedAt, evidencePosture, overallStatus, health, servingLatency, geoprocessing, alertDispatch, deploy, database}'

curl --fail --silent --show-error \
  -H "X-API-Key: $HONUA_KEY" \
  "$HONUA_URL/api/v1/admin/observability/findings" |
  jq '{generatedAt, evidencePosture, findings}'

curl --fail --silent --show-error \
  -H "X-API-Key: $HONUA_KEY" \
  "$HONUA_URL/api/v1/admin/observability/events?pageSize=5" |
  jq '{partialResult, sourceErrors, evidencePosture, items}'
```

On the verified candidate, the first two calls returned HTTP 200 but the posture
was `unavailable`: `honua_ops_health.alert_dispatch` and
`honua_ops_findings.workflow_operations` were `notConfigured`. The fixture event
page was also stale/truncated. That data remained useful for bounded diagnosis,
but it was not proposal evidence.

The decision gate is mechanical. Continue only when every id in a finding's
`requiredSourceIds` is `complete`, has a non-`unverified` backend, valid
`observedAt` and `lastSuccessfulAt`, complete coverage, and has not passed
`validUntil`. `generatedAt` is only response time.

## 2. Read the same truth through MCP

The HTTP MCP transport returns Server-Sent Event framing. This helper prints the
JSON-RPC message:

```bash
mcp_call() {
  curl --fail --silent --show-error \
    -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' \
    -H "X-API-Key: $HONUA_KEY" \
    --data "$1" "$HONUA_URL/mcp" |
    sed -n 's/^data: //p' | jq .
}

mcp_call '{"jsonrpc":"2.0","id":"health","method":"tools/call","params":{"name":"honua_ops_health","arguments":{}}}'
mcp_call '{"jsonrpc":"2.0","id":"findings","method":"tools/call","params":{"name":"honua_ops_findings","arguments":{}}}'
mcp_call '{"jsonrpc":"2.0","id":"alerts","method":"tools/call","params":{"name":"honua_alert_events","arguments":{"pageSize":5}}}'
mcp_call '{"jsonrpc":"2.0","id":"timeline","method":"tools/call","params":{"name":"honua_operate_events","arguments":{"pageSize":5}}}'
mcp_call '{"jsonrpc":"2.0","id":"kinds","method":"tools/call","params":{"name":"honua_supported_operation_kinds","arguments":{}}}'
```

All five calls executed on the candidate. The four reads returned
`isError:false` with the same posture semantics as REST. The local placement
returned an empty `supportedKinds` array, so it advertised no typed actuator and
the run stopped before proposal. Never reconstruct a hidden executable payload
from a finding or replace this catalog result with an assumed operation kind.

The bounded `honua-devops` stdio workflow consumes these same tools: observe,
explain the stable finding and evidence references, and emit zero proposals when
the posture gate fails. It is an agent client, not another control plane.

## 3. Console is the independent human seat

Open Console's `/operate` page against the same server. It inspects status,
health, findings, alerts, and the fused timeline, and is the human approval seat.
It must not reinterpret a non-actionable posture as healthy or let the proposer
self-approve.

The Console image is intentionally not bundled by default in Local Docker, so no
Console click transcript is claimed here. A compatible `HONUA_CONSOLE_IMAGE` is
required. Canonical terminal identity and separate-approver evidence remain
blocked by #3430 and #3431.

## 4. Proposal, approval, execution, and verification

Do not execute these stages from this candidate transcript:

- #3411 blocks the canonical durable operation/evidence join and approval bridge.
- #3430 blocks canonical MCP principal/tenant/session binding.
- #3431 blocks preserved OAuth scope narrowing and safe approved replay.
- #3475 blocks completion of the live outage/recovery receipt, although the
  additive posture contract and fail-closed proposal gate are shipped.

When those blockers close, the receipt must join the stable finding id,
evidence-source ids and observation window, proposal id, separate approval
identity, typed operation id, policy decision, actuator receipt, verification
window, audit/correlation ids, and release id. Prose such as `ready` or `applied`
is not an actuator receipt.

Verification must re-read health and findings across the documented observation
window and prove fix-forward convergence. A stale, partial, unavailable, or
backend-unverified source must still create zero proposals and zero actuator
calls.

## Rollback wiring by placement

Rollback is capability truth, not a universal command.

| Placement | Wiring | Candidate-backed claim |
|---|---|---|
| Local Docker | Compose/image pin plus operator-managed prior tag | Fix forward or manually redeploy the prior pinned image; no typed rollback kind was advertised in this run. |
| AWS ECS-small via Terraform | `honua-iac` declares the serving artifact; ECS/GitOps hand-off remains operator controlled | Treat `SupportsRollback=false` as manual intervention. Do not claim an automatic revert without a backend receipt. |
| Helm/Kubernetes | Chart values pin the image; GitOps hand-off owns revision change | The hand-off adapters report rollback unsupported. Argo Rollouts may report support only when that real adapter and a prior revision are configured. |

EKS and Azure qualification are not 2026.1 prerequisites. Terraform or Helm can
wire a previous artifact, but infrastructure declarations do not turn a manual
revert into a server actuator.
