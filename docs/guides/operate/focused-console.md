# Operate with the focused Console client

Console is the required human client for inspection, approvals, operational
health, and recovery. It consumes the same server control plane as agents and
`honua admin`; it is not the administration completeness or route-parity boundary.

## Sign in

Prefer OIDC bearer sign-in for people. Map the operator's identity-provider role
to the server's operator RBAC grants. The proposal decision path still enforces
the RBAC `approve` decision after authentication.

For a service-bound Console or focused candidate smoke, mint a scoped key with
exactly `admin:read` and `admin:approve`:

```bash
honua admin secure createAdminApiKey \
  --body '{"name":"focused-console","permissions":["admin:read","admin:approve"],"expiresAt":null}' \
  --yes --json
```

The secret is shown once. Store it in the Console deployment's secret facility,
then verify the stored grants remain distinct:

```bash
honua admin secure getAdminApiKeyEffectivePermissions \
  --path id=KEY_ID --json
```

Do not add `admin:write`, `admin:manage`, or `admin:*`. The narrow approval grant
is deliberately read-level everywhere except:

- `POST /api/v1/admin/proposals/{id}/approve`
- `POST /api/v1/admin/proposals/{id}/reject`

A missing grant returns 403 Problem Details naming `admin:approve`. The ordinary
admin write grants continue to authorize proposal decisions for full operators.

## Know the focused boundary

The focused key can inspect admin GET surfaces and decide proposals. It cannot
perform general mutations such as changing a service access policy. Some
historical read-like workflows are POST-shaped and therefore also remain denied:

- saved-connection tests (`connections/{id}/test`);
- external-service discovery (`external-services/discover`);
- GeoServices import start (`import/geoservices/start`).

Use a separately scoped automation key, an operator bearer identity with the
right RBAC grants, `honua admin`, or the policy-governed agent path for those
operations. Do not widen the Console key merely to make every form work.

## Inspect the journey

After the API/MCP lane has completed setup, use Console to inspect its exact:

1. deployment and release version;
2. connection, service, and published layers;
3. geoprocessing job, logs, and result artifact;
4. saved Studio draft/content identity and version;
5. publication or destructive-operation proposal;
6. health, findings, audit, backup, and recovery state.

Cross-check identifiers rather than creating parallel resources in Console.

## Approve or reject

Open the proposal inbox and review the operation id, requester, plan, diff,
dry-run output, risk, warnings, and audit/correlation identifiers. Separation of
duties applies: the requester cannot approve their own proposal.

Approve only if the proposal matches the intended resource and blast radius.
Reject with a reason when it does not. Record the terminal proposal state and
decision audit id in the delivery receipt.

## Diagnose and recover

- Use Operate health, findings, events, release, and deploy views for current
  server-owned state.
- Follow [Monitoring](../deploy/monitoring.md) for metrics, traces, job logs, and
  audit feeds.
- Follow [Upgrade and rollback](../deploy/upgrade-and-rollback.md) for release
  failures and controlled rollback.
- Follow [Backup and restore](../deploy/backup-and-restore.md) for durable-state
  recovery and restore drills.
- Configure identities and keys in [Authentication](../secure/authentication.md),
  and RLS in [Access control](../secure/access-control.md).

## Verify

A focused candidate check passes when Console signs in, reads the resources and
artifacts created by the API/MCP journey, approves or rejects one protected
proposal, and exposes actionable health/release/audit/recovery state. Full admin
form or route parity is not part of that check.
