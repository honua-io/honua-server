# License Key Rotation Runbook

Use this runbook to rotate the Ed25519 ISV signing keys that issue Honua
license files. Rotation is **additive**: new keys take over signing while
the old key remains valid for verification until every file it signed has
expired plus a margin. No fleet-wide synchronous update is required.

The ticket #338 runtime verifies the license envelope `keyId` against
`Licensing:TrustedKeys:<key-id>`. Public-key inspection and mint-host signing
status routes remain follow-on work.

---

## Scope

This runbook applies to:

- Routine rotation on the published cadence (annual by default, more often
  for compliance frameworks that require quarterly).
- Emergency rotation when a private signing key is suspected compromised.
- Smoke verification on a non-production environment before any production
  rotation.

This runbook does **not** cover:

- Customer-facing license file migration. See `LICENSE_MIGRATION.md`.
- Marketplace-specific lifecycle. See `MARKETPLACE_OPERATIONS.md`.

---

## Status / Prerequisites

This runbook documents the canonical multi-key rotation contract
defined in ADR-0033. Several runtime commands below depend on child
tickets that have **not yet landed on this branch** per ADR-0033 §
"Bounded Child Tickets":

| Command surface | Current status | Lands with |
|-----------------|-------------------------|------------|
| `GET /api/v1/admin/license/keys` | Route is **not yet registered** in `EndpointRegistry`. The CONTROL_PLANE_API contract lists it as a public-key inspection follow-on. | Public-key inspection child ticket. |
| `POST /api/v1/admin/license/upload` | Operational through the same validator as startup load. Default `Licensing:AllowAdminUpload=false` returns HTTP `400`; enable it only for workflows that allow runtime file replacement. | Landed with ticket #338. |
| `GET /api/v1/admin/license/signing/status` | Mint-host-only route. Not registered on customer instances. **Not yet registered** on the mint host either. | Mint host endpoints child ticket. |
| `POST /api/v1/admin/license/refresh` | Mint-host-only route. **Not yet registered.** | Mint host endpoints child ticket. |
| `tests/dotnet/Honua.Server.Tests/Features/Licensing/LicenseKeyRotationSmokeTests.cs` | Test fixture **not yet authored** on this branch. The #338 loader tests cover valid, missing, malformed, unknown-key, invalid-signature, and expired files. | Key-rotation smoke test child ticket. |
| `licenses_issued_total{source=...}` and `licenses_validated_total{result=...}` | Counter shapes are reserved by ADR-0033 but are not emitted by the #338 runtime baseline. | Telemetry counters child ticket. |

The runbook is published ahead of those child tickets so the rotation
contract is reviewable in isolation. Treat every command marked above
as **prerequisite-bound** and confirm the corresponding child ticket
has landed before running it on a customer or mint-host environment.

---

## Concept

Honua's ticket #338 validator resolves the signing key by envelope `keyId`
from `Licensing:TrustedKeys`. There is no baked-in verification key in the
runtime baseline. Adding or removing keys is a configuration change plus
application restart for startup-file load; admin upload, when enabled,
validates against the currently loaded configuration.

The mint host signs with **one** active private key at a time, identified
in configuration by `License:Signing:KeyId`. Switching the signing key is
how Honua actually rotates issuance.

---

## Pre-Rotation Checklist

```bash
# Future mint-host route; available only after the mint-host child ticket lands.
curl -H "X-API-Key: <admin-key>" \
  "https://<mint-host>/api/v1/admin/license/signing/status"

# Future public-key inspection route; not available in the #338 runtime baseline.
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/license/keys"
```

Verify before starting:

- The new Ed25519 keypair has been generated on the offline signing host; do
  not generate signing keys on customer runtime instances.
- The new public key is exported as raw Base64URL with the `base64url:`
  prefix (or as `base64:` standard Base64).
- The new `keyId` follows the convention `honua-<YYYY>-<quarter>` (e.g.,
  `honua-2026-q3`). Numeric collision is forbidden.
- For the #338 runtime baseline, verify trusted keys through the effective
  deployment configuration because `GET /api/v1/admin/license/keys` is not
  registered yet.
- An incident channel is open and a rollback contact is on standby.
- The smoke test (§ "Smoke Test") has passed on a non-production
  environment within the last 30 days.

---

## Routine Rotation

### Step 1 — Add the new public key (verification side)

Add the new key to `Licensing:TrustedKeys` configuration on every Honua
server in the fleet. The change is additive:

```
Licensing:TrustedKeys:honua-2026-q3 = base64url:<32-byte raw Ed25519 public key>
```

Apply the change through the standard configuration channel and restart each
instance. Keep the old key configured until every license signed by that key
has expired or been replaced.

Verify:

```bash
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/config"
```

The effective configuration must include both the old key id and the new key
id. If the new key does not appear, inspect configuration provider precedence
(environment variables > file > defaults).

### Step 2 — Switch the mint host signing key

On the mint host only:

```
License:Signing:KeyId         = honua-2026-q3
License:Signing:PrivateKeyRef = secret://kv/honua/sign/2026-q3
```

Apply the change. The mint host's next issuance uses the new private key
and emits the new `keyId` in the license envelope. Adapter-issued files (AWS / Azure)
re-mint automatically within `Mint:RefreshLeadTimeDays` (default 14 days).

### Step 3 — Re-issue BYOL files

Trigger a BYOL re-issue sweep through the portal (separate repo) over the
next portal cadence (typically 30–60 days). **Do not** force an out-of-
cycle download. Track progress through portal issuance records, support
inventory, and customer admin status until the ADR-0033 issuance counters
land. A trailing tail beyond the cadence indicates customer follow-up is
needed.

### Step 4 — Retire the old key

Once the longest-lived in-flight file signed by the old `keyId` has expired
plus a margin (default: 30 days past the latest known `expiresAt`), remove
`Licensing:TrustedKeys:<old-key-id>` from configuration and restart each
instance. If any old file is still in flight, the instance logs event `10003`
(`UnknownKey`) and runs Community mode for that file.

---

## Emergency Rotation

If a private signing key is suspected compromised:

1. **Page the security on-call.** Treat this as a P1 incident.
2. **Stop signing with the compromised key immediately:**
   - Set `License:Signing:Enabled=false` on the mint host. The mint
     endpoints return `404` while signing is disabled.
   - Confirm through mint-host access logs or portal issuance records that no
     new files are issued. `licenses_issued_total` remains a follow-on counter.
3. **Generate the replacement keypair** on the offline signing host.
4. **Add the new public key** to every Honua server (Step 1 above).
5. **Switch signing** to the new key (Step 2 above) and re-enable
   `License:Signing:Enabled=true`.
6. **Re-issue every in-flight file** signed by the compromised `keyId`:
   - BYOL: portal sweep within hours, not weeks.
   - AWS / Azure: the adapters auto-re-mint within `RefreshLeadTime`; if
     the lead time is too long for the incident, manually trigger
     `POST /api/v1/admin/license/refresh` for each affected customer or
     shorten the lead time temporarily.
7. **Retire the compromised `keyId`.** Remove
   `Licensing:TrustedKeys:<compromised-key-id>` from every instance and
   restart. The retired `keyId` is **not** trusted again, even if the private
   key is later believed safe — treat compromise as irreversible.
8. **File a public security advisory** if any compromised file may have
   reached customers and could not be re-issued before its `expiresAt`.

Optional revocation channel is **out of scope** for v1. A compromised
signing key without a revocation channel means files already in flight
cannot be invalidated before their `expiresAt`. This is a known accepted risk
captured in ADR-0033 § Negative Consequences and § Risks 3.

---

## Smoke Test

The smoke test exercises a full rotation cycle on a non-production
environment. Run before any production rotation and after any change that
touches the validator, resolver, or signer.

### Test environment setup

```bash
# Spin up the test stack (Testcontainers + scripted mint host)
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter "FullyQualifiedName~LicenseKeyRotationSmokeTests"
```

The smoke test (`tests/dotnet/Honua.Server.Tests/Features/Licensing/
LicenseKeyRotationSmokeTests.cs`, lands with the key-rotation runbook
child ticket per the ADR-0033 § "Bounded Child Tickets" decomposition)
exercises:

| Step | Assertion |
|------|-----------|
| 1. Bootstrap with `keyId=A`. | Admin status shows the expected edition and `validationState=Valid`. |
| 2. Issue file signed by `keyId=A`, validate. | Startup load or upload succeeds. |
| 3. Add `keyId=B` to configuration; restart. | Files signed by either key validate. |
| 4. Issue file signed by `keyId=B`, validate. | Admin status shows `validationState=Valid`. |
| 5. Validate the original `keyId=A` file. | Still `Valid`. |
| 6. Remove `keyId=A` from `Licensing:TrustedKeys`; restart. | `keyId=A` is no longer trusted. |
| 7. Validate the original `keyId=A` file. | `UnknownKey` validation state. |
| 8. Validate the `keyId=B` file. | Still `Valid`. |
| 9. Remove `keyId=A` from configuration. | Resolver lists only `keyId=B`. |
| 10. Validate every file signed by `keyId=B`. | All `Valid`. |

The smoke test must pass before any rotation is rolled out to production.
A failed smoke run blocks rotation; investigate before proceeding.

### Manual smoke available on the #338 runtime baseline

When the key-inspection route and rotation harness are not available, use a
staging instance with `Licensing:AllowAdminUpload=true` and a writable
`Licensing:LicensePath`:

```bash
# 1. Confirm the current license state.
curl -H "X-API-Key: <admin-key>" \
  "https://<staging-host>/api/v1/admin/license/status"

# 2. Add the new key id through normal configuration and restart.
# Confirm the effective configuration in your deployment system contains both
# Licensing:TrustedKeys:<old-key-id> and Licensing:TrustedKeys:<new-key-id>.

# 3. Validate a freshly minted file with the new key id.
curl -X POST -H "X-API-Key: <admin-key>" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @new-key-id.honua-license.json \
  "https://<staging-host>/api/v1/admin/license/upload"

curl -H "X-API-Key: <admin-key>" \
  "https://<staging-host>/api/v1/admin/license/status"
# expect isValid=true and validationState="Valid"; logs emit EventId 10006.

# 4. Validate a file signed by the old key id.
curl -X POST -H "X-API-Key: <admin-key>" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @old-key-id.honua-license.json \
  "https://<staging-host>/api/v1/admin/license/upload"

curl -H "X-API-Key: <admin-key>" \
  "https://<staging-host>/api/v1/admin/license/status"
# expect isValid=true and validationState="Valid".

# 5. Remove old key id from Licensing:TrustedKeys and restart.
# If the configured LicensePath still points at the old-key-id file, status
# should show isValid=false with validationState="UnknownKey" or "Expired".

# 6. If LicensePath points at a valid new-key-id file, POST the old-key-id file
# as a negative upload check. Expect HTTP 400 with a validation-failed message;
# rejected uploads do not replace the current snapshot.
```

Capture the manual smoke output in the incident channel before declaring
rotation complete.

---

## Telemetry to Watch

| Signal | Healthy state | Action threshold |
|--------|---------------|-----------------|
| Admin status `validationState` from `/api/v1/admin/license/status` | `Valid` on every rotated instance. | `UnknownKey`, `InvalidSignature`, `Malformed`, or `Expired` after rollout = halt rotation and inspect the configured key set, issued file, and expiry. |
| Runtime log EventId `10003` (`UnknownKey`) | Zero during routine rotation. | Any occurrence after adding a new `keyId` = at least one server does not trust the key that signed the file. |
| Runtime log EventId `10004` (`InvalidSignature`) | Zero. | Any occurrence = the private signing key and configured public key do not match for that `keyId`, or the file was modified after signing. |
| Runtime log EventId `10006` (`LicenseLoaded`) | One successful load/upload per tested file. | Missing success event after upload/startup = check admin upload settings, file path, and validation state. |
| Runtime log EventId `10007` / `10008` | Zero outside intentional negative tests. | Upload rejected or save failed; do not proceed until resolved. |

The `licenses_issued_total`, `licenses_validated_total`,
`licenses_active`, marketplace reconciler counters, and validator key-resolution
counter shapes remain ADR-0033 follow-ons and are not emitted by the #338
runtime baseline.

---

## Rollback

If a rotation triggers any of the action thresholds above:

1. **Do not** retire the old `keyId`.
2. **Switch signing back** to the old `keyId` on the mint host:
   ```
   License:Signing:KeyId = honua-2026-q2
   License:Signing:PrivateKeyRef = secret://kv/honua/sign/2026-q2
   ```
3. Confirm portal issuance or mint-host logs show files being issued by the
   restored `keyId`.
4. Page the licensing on-call. Investigate why the new `keyId` failed to
   propagate before retrying.

The new `keyId` configuration **stays in place** during rollback — it does
no harm because no files are signed by it. Remove the new `keyId` only
after root cause is understood.

---

## Exit Criteria

A rotation is complete when:

- The mint host signs every new license with the new `keyId`.
- The fleet configuration contains the expected trusted key ids.
- BYOL re-issue cadence has reached 100% of known customers.
- The retired `keyId` has been removed from `Licensing:TrustedKeys` after the
  final old-key file expired or was replaced.
- The smoke test was run on the non-production environment within the
  last 30 days.
- The retired `keyId` configuration has been removed in a follow-up change.
- The rotation is recorded in the security log with the new `keyId`,
  activation date, retirement date for the old `keyId`, and the operator
  responsible.
