# One incident, every Operate surface

This scenario follows a deploy that reaches `ManualInterventionRequired`. It is
deliberately based on a finding and operation that exist on trunk. A generic
tile-cache degradation does not currently have a deterministic Ops finding or
auto-safe repair executor, so this guide does not invent one.

Set the server URL and use an `ops:read` credential for reads. Proposal and
approval calls require the corresponding admin/operator permissions, and the
proposer cannot approve their own proposal.

```bash
export HONUA_URL=http://localhost:8080
export HONUA_TOKEN='replace-with-token'
```

## 1. Observe

```bash
curl -fsS -H "Authorization: Bearer ${HONUA_TOKEN}" \
  "${HONUA_URL}/api/v1/operate/status"

curl -fsS -H "Authorization: Bearer ${HONUA_TOKEN}" \
  "${HONUA_URL}/api/v1/admin/observability/ops-health"
```

In Console, open **Operate → Health**. In MCP, call `honua_ops_health`; the
resource `honua://ops/health` exposes the same read posture. These are three
views of server-owned state, not separate health evaluators.

## 2. Diagnose the finding

```bash
curl -fsS -H "Authorization: Bearer ${HONUA_TOKEN}" \
  "${HONUA_URL}/api/v1/admin/observability/findings"
```

Find a `deploy-manual-intervention` result and retain its `id`, evidence refs,
target, and prior revision. MCP uses `honua_ops_findings`; Console shows the
same explanation and evidence. If the operation did not record a prior
revision, no rollback action is offered—investigate manually.

## 3. Propose, review, and approve

For a finding that contains a recommended action:

```bash
curl -fsS -X POST \
  -H "Authorization: Bearer ${HONUA_TOKEN}" \
  "${HONUA_URL}/api/v1/admin/observability/findings/${FINDING_ID}/propose"
```

An MCP agent can route the same server-declared action with
`honua_propose_operation` (or the focused `honua_propose_rollback` after
checking `honua_deploy_operations`). The response either records an executed
action, blocks it, or returns a proposal ID. It never creates a second approval
channel.

Use a separate authorized operator identity to inspect
`GET /api/v1/admin/proposals/{id}` and approve in Console's inbox or through:

```bash
curl -fsS -X POST \
  -H "Authorization: Bearer ${APPROVER_TOKEN}" \
  "${HONUA_URL}/api/v1/admin/proposals/${PROPOSAL_ID}/approve"
```

## 4. Verify the result

```bash
curl -fsS -H "Authorization: Bearer ${HONUA_TOKEN}" \
  "${HONUA_URL}/api/v1/admin/deploy/operations/${OPERATION_ID}"

curl -fsS -H "Authorization: Bearer ${HONUA_TOKEN}" \
  "${HONUA_URL}/api/v1/admin/observability/ops-health/history"
```

MCP uses `honua_deploy_operations` and `honua_operate_events`; Console uses the
deploy and timeline views. Verify the exact backend outcome. A GitOps hand-off
backend reports manual intervention and `rollbackSupported: false`; it cannot
be treated as an automatic rollback.

## Deployment-tool boundary

- Helm's `--atomic` is Helm's own release rollback behavior. It does not turn a
  Honua GitOps hand-off target into an automatic server rollback backend.
- Terraform/IaC declares `ControlPlane:DeployTargets` (environment form
  `ControlPlane__DeployTargets__...`). The manifest reports `deploy.rollback`
  available only when at least one configured target's registered backend
  advertises real rollback support.
- The honua-devops agent can use the same REST/MCP contract from a local CLI or
  stdio seat. A separately hosted public agent service is not a 2026.1 claim.

## Revert and learn

If a real rollback backend completes but the old revision is also unhealthy,
deploy a reviewed known-good revision through the normal plan/submit path.
Keep the finding, proposal, audit record, operation timeline, metrics, and
traces together as incident evidence. Do not erase a manual-intervention
outcome by relabeling it as rolled back.
