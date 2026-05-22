# Compliance Framework: SOC 2 and FedRAMP Readiness

> **Readiness ≠ authorization.** The server reports technical control posture so
> auditors and procurement teams can review evidence. It does **not** claim
> SOC 2 Type II, FedRAMP, CJIS, HITRUST, IRS 1075, or CMMC authorization — those
> require a qualified auditor or agency.

This guide explains how to configure and operate the compliance framework that
ships with Honua Server (#352). It pairs with three prerequisite slices:

- Audit logging (#350) — the substrate that backs every evidence claim.
- SSO / OIDC (#348) — identity-aware evidence ("who did what").
- RBAC (#349) — role-based access control primitives.

If any of those is missing the dashboard reports the dependent controls as
`PartiallyImplemented` and lists the gap. **The server never asserts
"Implemented" for a control whose dependencies are not operational.**

---

## What the framework provides

- **Automated control evidence.** Every SOC 2 / FedRAMP control in the curated
  catalog has its evidence rolled up from server configuration, audit-log
  availability, and the encryption posture.
- **Compliance dashboard.** Admin API surface at
  `GET /api/v1/admin/compliance/dashboard` returns the structured snapshot the
  Admin UI renders for control status, evidence gaps, and audit readiness.
- **Data residency enforcement.** A configurable policy at `Compliance:DataResidency`
  that egress paths consult; the policy can be informational (audit only) or
  enforcing (deny non-listed regions).
- **Zero-downtime key rotation.** `POST /api/v1/admin/compliance/encryption/rotate-key`
  appends a new active key version without interrupting running requests; older
  ciphertexts decrypt against the version that produced them.
- **Compliance report export.** `GET /api/v1/admin/compliance/report?format=pdf|csv`
  renders the snapshot to PDF (auditor-facing) or CSV (evidence matrix). The
  default format is PDF; both are produced from the same `ComplianceSnapshot`.
- **FIPS 140-2 posture signal.** The encryption section of the snapshot reports
  whether FIPS mode is enabled and from what source (operator attestation,
  runtime environment variable, or unverified).

## Configuration

All settings live under `Compliance` in `appsettings.json` or environment
variables. Defaults produce an *informational-only* posture so a fresh
deployment shows evidence gaps rather than claiming false readiness.

```jsonc
{
  "Compliance": {
    "Soc2ReadinessClaimed": true,
    "FedRampReadinessClaimed": true,
    "PrimaryRegion": "us-gov-west-1",

    "DataResidency": {
      "Enforced": true,                                  // Set false for informational mode
      "PrimaryRegion": "us-gov-west-1",                  // Implicitly in AllowedRegions
      "AllowedRegions": ["us-gov-east-1"]                // Additional regions data may flow to
    },

    "Encryption": {
      "Algorithms": ["aes-256-gcm", "pbkdf2-sha256-100000"],
      "FipsModeAttested": true                           // Operator attests host FIPS mode
    },

    "DependencyOverrides": {
      "AuditLogConfigured": null,                        // null = let the gate auto-detect
      "SsoConfigured": null,
      "RbacConfigured": null,
      "TransportEncryptionAttested": true                // Operator attests upstream TLS
    }
  }
}
```

### When to use `DependencyOverrides`

The dependency gate inspects the DI container — it can detect whether
`IAuditLog`, `IRoleStore`, `IOidcProviderStore`, and `IConnectionEncryptionService`
are registered (and whether the audit log is the `NullAuditLog` fallback). Use
overrides only when the server cannot probe a dependency directly:

- **`TransportEncryptionAttested`** — terminate TLS at an upstream load balancer
  (the application binds to plain HTTP behind the LB). Set this to `true` to
  acknowledge that in-transit encryption is the operator's responsibility.
- **`AuditLogConfigured` / `SsoConfigured` / `RbacConfigured`** — set when the
  capability is supplied by a sidecar or external service rather than registered
  in this process.

## Endpoints

| Method | Path                                                        | Purpose |
|--------|-------------------------------------------------------------|---------|
| `GET`  | `/api/v1/admin/compliance/dashboard`                        | JSON snapshot for the Admin UI dashboard. |
| `GET`  | `/api/v1/admin/compliance/report?format=pdf\|csv`           | Render and download the report. PDF is the default. |
| `POST` | `/api/v1/admin/compliance/residency/evaluate`               | Evaluate a region against the active residency policy. Body: `{"region": "us-east-1"}`. |
| `POST` | `/api/v1/admin/compliance/encryption/rotate-key`            | Rotate the active encryption-at-rest key version. Zero-downtime — existing ciphertext stays readable. |

All endpoints require admin authentication. Report export is also audit-logged
as `compliance.report.export`; residency evaluation as
`compliance.residency.evaluate`; key rotation as `encryption.key.rotate`.

## How readiness is computed

For each control:

1. If the framework's `ReadinessClaimed` flag is `false`, the control is
   reported as `NotApplicable`.
2. Otherwise, each declared dependency is probed (or read from
   `DependencyOverrides`). Each dependency contributes one evidence row.
3. Framework-specific evidence is appended:
   - FedRAMP SC-8 / SC-13 / SC-28 add an `encryption-posture` row reflecting
     FIPS mode and algorithms.
   - SOC 2 CC6.7 and FedRAMP SC-7 add a `residency-policy` row.
4. The control's status is the worst case across its evidence rows
   (`Implemented` → `PartiallyImplemented` → `NotImplemented`). If all
   dependencies are missing the status downgrades to `NotImplemented`.

The dashboard summary shows per-status counts and the **readiness percent**:
implemented controls as a percentage of applicable controls (excluding N/A
and Unknown).

## Key rotation procedure

The compliance framework maintains an in-memory key ring that tracks every
historical encryption-at-rest version. Rotation flow:

1. Operator calls `POST /api/v1/admin/compliance/encryption/rotate-key`.
2. The provider appends a new version under a single lock, updates the
   active-version pointer, and returns. No request is paused; no cache is
   invalidated.
3. The previous version is marked "retired" but stays in the ring so existing
   ciphertext keeps decrypting.
4. A `ConfigChange` audit event with action `encryption.key.rotate` is recorded.

> **Persistence note.** The key ring is in-memory by design — it tracks
> *compliance* posture, not the key material `IConnectionEncryptionService`
> uses for the secure-connection registry. Rotating the connection-registry's
> master passphrase still requires a redeploy with a new
> `Security:ConnectionEncryption:MasterKey` (see
> [`SecureConnectionEndpoints`](../../src/Honua.Server/Features/Admin/SecureConnectionEndpoints.cs)).
> The compliance framework's rotation records the *event* and advances the
> auditor-facing version counter so dashboard evidence reflects rotation
> activity.

## Data residency enforcement

When `Compliance:DataResidency:Enforced` is `true`, the residency provider
denies any region not in the allowed set. The primary region is always
implicitly allowed (so a deployment "in" the primary region cannot block its
own writes). Empty region strings are always denied — egress requires an
explicit region.

Egress integration points consult `IDataResidencyPolicyProvider.Evaluate(region)`
and audit-log the decision. The admin endpoint exists so operators can dry-run
policies before flipping `Enforced` to `true`.

## FedRAMP readiness inputs

The encryption section of the dashboard surfaces the inputs FedRAMP Moderate
(SC-8, SC-13, SC-28) auditors expect to see:

- **FIPS mode.** Resolved in priority order: operator attestation
  (`Compliance:Encryption:FipsModeAttested`) → runtime hint
  (`DOTNET_SYSTEM_SECURITY_CRYPTOGRAPHY_USEFIPS=1`) → `unverified`.
- **Algorithm inventory.** Reported verbatim from `Compliance:Encryption:Algorithms`.
- **Key version timeline.** Active version, retained versions, last rotation
  timestamp.

The boundary control (SC-7) draws on the residency policy: primary region,
allowed regions, and enforcement status.

## Validating a deployment

After configuring `Compliance` settings, validate the posture with:

```bash
# Dashboard JSON
curl -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
  https://honua.example/api/v1/admin/compliance/dashboard | jq .data.summary

# CSV evidence matrix
curl -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
  "https://honua.example/api/v1/admin/compliance/report?format=csv" \
  -o honua-compliance.csv

# PDF report
curl -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
  "https://honua.example/api/v1/admin/compliance/report?format=pdf" \
  -o honua-compliance.pdf
```

The CSV opens cleanly in Excel (UTF-8 BOM); the PDF is a self-contained PDF 1.4
document using the built-in Helvetica font — no external rendering toolchain
required.

## Limitations

- **No persistence for the compliance key ring across restarts.** Version
  numbers reset to 1 on each process start. The audit log keeps the historical
  rotation events; durable storage of the key ring lives behind the connection
  encryption service.
- **Curated control catalog.** Only controls with automated server evidence are
  surfaced. Auditor-required process controls (training, change-management,
  vendor management) belong in the system security plan, not in this dashboard.
- **No claimed authorization.** This server cannot, and does not, claim a
  granted SOC 2 Type II report, FedRAMP ATO, or any other authorization.
