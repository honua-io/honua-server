---
{
  "name": "honua-ops",
  "description": "Use when checking a Honua Compose release, proposing an upgrade or rollback, or acting on an operational finding."
}
---

# Check and change a running install

Read the install `AGENTS.md`. Day-2 calls go to that install's MCP. Release status, upgrade, and rollback use the management endpoint (`HONUA_MANAGER_PORT`, 18082 unless `.env` changed it). Findings use the serving endpoint. Use a human-provisioned agent credential accepted by the selected endpoint, through the install's protected credential channel without printing it. Follow [agent key provisioning](https://honua.gitbook.io/honuaio/drive-honua-with-an-agent/honua-server/ai-agents-mcp#connect-a-client) for serving MCP: the human creates the key outside the agent session with the `agent` grant and the grants required for the requested tools. Do not assume a serving key authenticates to management MCP. If an appropriate credential is missing or refused, stop and ask the human to provision one for that endpoint. Do not read administrator password files, create your own key, or use an unmarked administrator credential. If `tools/list` lacks `honua_platform_release_status`, `honua_ops_findings`, `honua_propose_platform_release_convergence` or `honua_propose_rollback`, reconnect that endpoint with `?view=operate`. Use the live `tools/list` schemas. Read [release readback](references/releases.md) before calling a release done.

Findings: `honua_ops_findings` lists active findings, or one finding when `findingId` is set. `honua_propose_finding` requires `findingId` and `candidateId`. For a deploy, or for `compose.restart_service`, set `candidateId` to `subject.targetId`. Do not pass the finding id. If that action has no `subject.targetId`, stop. Alert redrive, channel pause, and DB admission have no deployment target; the server does not bind `candidateId` to one. Follow the live `tools/list` schema for those, and do not invent a target.

For an access review (API keys, roles, users and their effective permissions, service access policy), reconnect the serving endpoint with `?view=access`; read with `honua_admin_api_key_effective_permissions` and `honua_admin_user_effective_permissions` before proposing a change.

A proposal result is not the change. A human using a password or OIDC browser session can approve their own proposal. An agent or API key cannot approve, and the admin password must not be given to an agent; ask the human to review the proposal in the operator console. Approval authorizes the recorded operation and does not prove it has completed. Poll `honua://proposals/{id}` until `Succeeded` or `Completed`, or stop on `Failed`, `Rejected`, or `Cancelled`. On `Indeterminate`, stop and report that the outcome is uncertain; retain the original proposal identity and do not propose the change again. After successful completion, read release status or the target health again.

Upgrade and rollback stay on the path in the install `AGENTS.md` and [Upgrade & rollback](https://honua.gitbook.io/honuaio/deploy-and-operate/honua-server/deploy-and-operate/upgrade-and-rollback). Never run `docker compose down --volumes` and never delete `backups/`.

Common failure: proposing convergence before `releaseVersion` is the target. The server rejects that with `platform_release_version_mismatch`. Set `HONUA_RELEASE_IMAGE`, `HONUA_RELEASE_WORKER_IMAGE`, and `HONUA_RELEASE_VERSION` from the new lock, pull the serving image, and run `python3 quickstart.py` so the manager advertises that version before the proposal.
