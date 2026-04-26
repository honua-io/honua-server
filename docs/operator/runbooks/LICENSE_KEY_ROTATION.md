# License Key Rotation Runbook

Use this runbook to rotate the Ed25519 ISV signing keys that issue Honua
license files. Rotation is **additive**: new keys take over signing while
the old key remains valid for verification until every file it signed has
expired plus a margin. No fleet-wide synchronous update is required.

The runbook also exercises the smoke test that proves rotation works
end-to-end — required by the acceptance criteria of ADR-0033.

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

## Concept

Honua's license validator resolves the signing key by `kid` (RFC 7517) from
a key set that combines:

1. A **baked-in primary** key compiled into `Honua.Core` at release time.
2. A **configuration-additive** list (`License:Keys`) loaded by
   `IOptionsMonitor<LicenseKeysOptions>`.

A key is active when `now ∈ [NotBefore, NotAfter ?? +∞)`. Adding a new key
takes effect on the next configuration reload. Retiring a key requires
setting `NotAfter` to a past instant (or removing it from configuration);
the resolver returns `null` for any `kid` outside its window.

The mint host signs with **one** active private key at a time, identified
in configuration by `License:Signing:KeyId`. Switching the signing key is
how Honua actually rotates issuance.

---

## Pre-Rotation Checklist

```bash
# Confirm current signing kid (mint host only)
curl -H "X-API-Key: <admin-key>" \
  "https://<mint-host>/api/v1/admin/license/signing/status"

# Confirm public-key set on a customer instance
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/license/keys"
```

Verify before starting:

- The new keypair has been generated on the offline signing host (no
  online generation; libsodium / NSec keypair, 32-byte private key).
- The new public key is exported as Base64URL (the wire format the
  resolver expects).
- The new `kid` follows the convention `honua-<YYYY>-<quarter>` (e.g.,
  `honua-2026-q3`). Numeric collision is forbidden.
- An incident channel is open and a rollback contact is on standby.
- The smoke test (§ "Smoke Test") has passed on a non-production
  environment within the last 30 days.

---

## Routine Rotation

### Step 1 — Add the new public key (verification side)

Add the new key to `License:Keys` configuration on every Honua server in
the fleet. The change is additive:

```
License:Keys:1:Kid          = honua-2026-q3
License:Keys:1:PublicKey    = base64url:<32-byte raw Ed25519 public key>
License:Keys:1:NotBefore    = 2026-07-01T00:00:00Z
License:Keys:1:NotAfter     = 2027-07-01T00:00:00Z
```

Apply the change through the standard configuration channel. No restart
is required — `IOptionsMonitor` publishes a `LicenseKeysChanged` event
which invalidates the public-key cache (`license:keys:current`) and the
license snapshot cache.

Verify:

```bash
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/license/keys"
```

The response must list both the old `kid` and the new `kid` with their
windows. If the new key does not appear, inspect the configuration
provider precedence (env var > file > defaults) and the resilience event
log (`6100-6199` band).

### Step 2 — Switch the mint host signing key

On the mint host only:

```
License:Signing:KeyId         = honua-2026-q3
License:Signing:PrivateKeyRef = secret://kv/honua/sign/2026-q3
```

Apply the change. The mint host's next issuance uses the new private key
and emits the new `kid` in the JWS header. Adapter-issued files (AWS / Azure)
re-mint automatically within `Mint:RefreshLeadTimeDays` (default 14 days).

### Step 3 — Re-issue BYOL files

Trigger a BYOL re-issue sweep through the portal (separate repo) over the
next portal cadence (typically 30–60 days). **Do not** force an out-of-
cycle download. Track progress via:

```
licenses_issued_total{source="byol-portal"}
```

versus the known BYOL customer count. A trailing tail beyond the cadence
indicates customer follow-up is needed.

### Step 4 — Retire the old key

Once the longest-lived in-flight file signed by the old `kid` has expired
plus a margin (default: 30 days past the latest known `exp`):

1. Set `NotAfter` on the old key to a past instant (or remove it from
   configuration entirely):

   ```
   License:Keys:0:NotAfter = 2027-07-01T00:00:00Z   # already in the past
   ```

2. Confirm via `licenses_validated_total{result="unknown_key_id"}` that no
   files signed by the retired `kid` are still in flight.

3. After 14 consecutive days at zero rate, remove the retired entry from
   configuration in a follow-up change.

The baked-in primary key remains in code until the next release
(`Honua.Core` compile-time resource). It is **not** removed mid-cycle;
removal requires a release.

---

## Emergency Rotation

If a private signing key is suspected compromised:

1. **Page the security on-call.** Treat this as a P1 incident.
2. **Stop signing with the compromised key immediately:**
   - Set `License:Signing:Enabled=false` on the mint host. The mint
     endpoints return `404` while signing is disabled.
   - Confirm via `licenses_issued_total` flat-lining at the mint host's
     ingress.
3. **Generate the replacement keypair** on the offline signing host.
4. **Add the new public key** to every Honua server (Step 1 above).
5. **Switch signing** to the new key (Step 2 above) and re-enable
   `License:Signing:Enabled=true`.
6. **Re-issue every in-flight file** signed by the compromised `kid`:
   - BYOL: portal sweep within hours, not weeks.
   - AWS / Azure: the adapters auto-re-mint within `RefreshLeadTime`; if
     the lead time is too long for the incident, manually trigger
     `POST /admin/license/refresh` for each affected customer or shorten
     the lead time temporarily.
7. **Retire the compromised `kid`** by setting its `NotAfter` to a past
   instant. The retired `kid` is **not** trusted again, even if the
   private key is later believed safe — we treat compromise as
   irreversible.
8. **File a public security advisory** if any compromised file may have
   reached customers and could not be re-issued before its `exp`.

Optional revocation channel is **out of scope** for v1. A compromised
signing key without a revocation channel means files already in flight
cannot be invalidated before their `exp`. This is a known accepted risk
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
LicenseKeyRotationSmokeTests.cs`, child ticket #TBD-10) exercises:

| Step | Assertion |
|------|-----------|
| 1. Bootstrap with `kid=A`. | `licenses_active{edition="enterprise"}` == 1. |
| 2. Issue file signed by `kid=A`, validate. | `licenses_validated_total{result="valid"}` ticks. |
| 3. Add `kid=B` to configuration; reload. | Resolver lists both keys. |
| 4. Issue file signed by `kid=B`, validate. | `licenses_validated_total{result="valid"}` ticks. |
| 5. Validate the original `kid=A` file. | Still `valid`. |
| 6. Set `kid=A` `NotAfter` to a past instant. | Resolver returns `null` for `kid=A`. |
| 7. Validate the original `kid=A` file. | `unknown_key_id`. |
| 8. Validate the `kid=B` file. | Still `valid`. |
| 9. Remove `kid=A` from configuration. | Resolver lists only `kid=B`. |
| 10. Validate every file signed by `kid=B`. | All `valid`. |

The smoke test must pass before any rotation is rolled out to production.
A failed smoke run blocks rotation; investigate before proceeding.

### Manual smoke (without the test harness)

When the harness is not available (e.g., during incident response):

```bash
# 1. Capture the current state
curl -H "X-API-Key: <admin-key>" \
  "https://<staging-host>/api/v1/admin/license/keys" > before.json

# 2. Add the new kid
# (apply the configuration change through your normal channel)

# 3. Confirm both kids are listed
curl -H "X-API-Key: <admin-key>" \
  "https://<staging-host>/api/v1/admin/license/keys" > after.json
diff before.json after.json   # expect new kid added

# 4. Validate a freshly-minted file with the new kid
curl -X POST -H "X-API-Key: <admin-key>" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @new-kid.honua-license \
  "https://<staging-host>/api/v1/admin/license/upload"

curl -H "X-API-Key: <admin-key>" \
  "https://<staging-host>/api/v1/admin/license/status"
# expect IsValid=true, kid in audit log entry matches new kid

# 5. Validate a file signed by the old kid
curl -X POST -H "X-API-Key: <admin-key>" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @old-kid.honua-license \
  "https://<staging-host>/api/v1/admin/license/upload"

curl -H "X-API-Key: <admin-key>" \
  "https://<staging-host>/api/v1/admin/license/status"
# expect IsValid=true, both kids verified by the resolver

# 6. Set NotAfter on old kid to a past instant
# (apply the configuration change)

# 7. Re-upload the old-kid file; expect IsValid=false with
# ValidationState="UnknownKeyId" or "Expired"
```

Capture the manual smoke output in the incident channel before declaring
rotation complete.

---

## Telemetry to Watch

| Signal | Healthy state | Action threshold |
|--------|---------------|-----------------|
| `licenses_issued_total{source="byol-portal"}` | Steady-state increment matches portal cadence. | Flat line during rotation = mint not switched; check `License:Signing:Enabled` and `KeyId`. |
| `licenses_validated_total{result="unknown_key_id"}` | Zero. | Non-zero after adding a new `kid` = new key not loaded on a server in the fleet; re-check configuration roll. |
| `licenses_validated_total{result="signature_invalid"}` | Zero. | Non-zero after rotation = mint signed with one private key, fleet trusts a different public key for that `kid`; halt rotation, verify `kid` collision. |
| `marketplace_reconciler_runs_total{cloud="aws",result="succeeded"}` and `..."azure"...` | Steady. | Drop after rotation = adapters cannot reach the mint host with new credentials. |
| Validator log emitter `6020` (`license_validation_kid_resolved`). | One emission per `kid` per warm cache window. | Multiple `kid` resolutions for the same `license_id` within a short window = unstable resolver / configuration churn. |

---

## Rollback

If a rotation triggers any of the action thresholds above:

1. **Do not** retire the old `kid`.
2. **Switch signing back** to the old `kid` on the mint host:
   ```
   License:Signing:KeyId = honua-2026-q2
   License:Signing:PrivateKeyRef = secret://kv/honua/sign/2026-q2
   ```
3. Confirm `licenses_issued_total{source="byol-portal"}` resumes
   incrementing.
4. Page the licensing on-call. Investigate why the new `kid` failed to
   propagate before retrying.

The new `kid` configuration **stays in place** during rollback — it does
no harm because no files are signed by it. Remove the new `kid` only
after root cause is understood.

---

## Exit Criteria

A rotation is complete when:

- The mint host signs every new license with the new `kid`.
- The fleet's public-key resolver lists both `kid`s with correct
  windows.
- BYOL re-issue cadence has reached 100% of known customers.
- The retired `kid`'s `NotAfter` has passed and
  `licenses_validated_total{result="unknown_key_id"}` is zero for 14
  consecutive days.
- The smoke test was run on the non-production environment within the
  last 30 days.
- The retired `kid` configuration has been removed in a follow-up change.
- The rotation is recorded in the security log with the new `kid`,
  `NotBefore`, retirement date for the old `kid`, and the operator
  responsible.
