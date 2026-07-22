# Check compliance posture

Pull a live SOC 2 / FedRAMP readiness snapshot, export auditor-facing reports, and dry-run your data-residency policy from the admin compliance endpoints.

**Prerequisites:** An admin API key ([Authenticate clients](authentication.md)). Examples use `$HONUA_ADMIN_PASSWORD` and `http://localhost:8080`.

> **Readiness ≠ authorization.** The server reports technical control posture as evidence; it does not claim SOC 2 Type II, FedRAMP, or any other authorization — those require a qualified auditor or agency. Defaults are informational-only, so a fresh deployment shows evidence gaps rather than asserting readiness. Controls whose dependencies (audit logging, SSO, RBAC) are not operational are reported `PartiallyImplemented`, never `Implemented`.

## Steps

### 1. Read the compliance dashboard

In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `GET /api/v1/admin/compliance/dashboard`.

The snapshot rolls per-control evidence up from server configuration, audit-log availability, and encryption posture; the summary shows per-status counts and the readiness percent across applicable controls.

### 2. Declare what the server cannot auto-detect

Audit-log and SSO presence are auto-detected; capabilities outside the process are operator attestations under `Compliance__DependencyOverrides__*`:

```bash
Compliance__Soc2ReadinessClaimed=true
Compliance__DependencyOverrides__TransportEncryptionAttested=true
Compliance__DependencyOverrides__RbacConfigured=true
```

Set an attestation only once it is true in your deployment (e.g. TLS terminates at your load balancer); the gate conservatively reports unsatisfied until you do.

### 3. Export the evidence report

Run `GET /api/v1/admin/compliance/report?format=csv` and save the explorer response as `honua-compliance.csv`.

`format=pdf` (default) renders a self-contained auditor-facing PDF; `csv` is the evidence matrix. Exports are audit-logged as `compliance.report.export`.

### 4. Dry-run the residency policy

Run `POST /api/v1/admin/compliance/residency/evaluate` with `{"region":"us-east-1"}`.

The policy lives under `Compliance__DataResidency__*` (`Enforced`, `PrimaryRegion`, `AllowedRegions__0..n`); the primary region is implicitly allowed. **This is a policy check, not enforcement**: no production egress path currently consults the residency provider, so `Enforced=true` flips the policy view and this dry-run verdict only. Once you wire your own egress guards to the policy, attest it with `Compliance__DependencyOverrides__DataResidencyAttested=true`.

### 5. Record a key-rotation event (posture only)

Run `POST /api/v1/admin/compliance/encryption/rotate-key`.

This advances an auditor-facing key-version counter and writes an `encryption.key.rotate` audit event. **It does not re-encrypt data or rotate cipher material** — the connection registry's real key is `Security__ConnectionEncryption__MasterKey` and requires a redeploy. The version timeline is in-memory and resets on restart; the audit log keeps the durable history.

## Verify

Run `GET /api/v1/admin/compliance/dashboard` again.

```json
{ "implemented": 9, "partiallyImplemented": 4, "notImplemented": 0, "readinessPercent": 69 }
```

Residency evaluations and report exports also appear in the audit feed at `GET /api/v1/admin/observability/audit`.

## Limitations (current release)

- The control catalog is curated to controls with automated server evidence; process controls (training, change management) belong in your system security plan.
- Residency enforcement and some audit-trail features are operator-supplied or MVP-deferred — the dashboard reflects that honestly via dependency gaps rather than claiming them.
- `400` on report export means a bad `format`; `406` means the renderer was trimmed from the build (default builds ship both PDF and CSV).

## Troubleshoot

| Symptom | Fix |
|---|---|
| Controls stuck at `PartiallyImplemented` | A dependency (audit log, SSO, RBAC) is missing or unattested; the evidence rows name the gap. |
| Everything reports `NotApplicable` | `Compliance__Soc2ReadinessClaimed` / `FedRampReadinessClaimed` are still `false`. |
| Key version reset to 1 | Expected after restart — the version timeline is in-memory; reconcile against `encryption.key.rotate` audit events. |
| Residency says allowed but data left the region | The dry-run endpoint evaluates policy only; enforcement is your egress guards' job until wired and attested. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Production security checklist](production-checklist.md)
- [Control access to services and layers](access-control.md)
