# License Migration Runbook

Use this runbook to move existing Honua deployments from any pre-existing
license file format to the ticket #338 runtime license envelope: a JSON
envelope with Ed25519 verification over exact payload bytes. ADR-0033 tracks
the broader marketplace/mint architecture; customer runtimes on this branch
load the signed JSON envelope described here.

The #338 runtime accepts only this signed JSON envelope. If any customer is
still on a private-preview license format, treat compatibility as a separate
bounded follow-on before declaring that deployment migrated.

---

## Scope

This runbook applies to:

- BYOL customers running an older Honua release that consumed a private-
  preview license file (if any portal-issued files predate ADR-0033).
- New BYOL customers receiving ticket #338 signed JSON license files from day one.
- AWS Marketplace and Azure Marketplace customers once the marketplace adapter
  follow-on tickets mint the same runtime envelope. Those adapters are not part
  of ticket #338.

This runbook does **not** cover:

- Key rotation. See `LICENSE_KEY_ROTATION.md`.
- Marketplace lifecycle operations. See `MARKETPLACE_OPERATIONS.md`.

---

## Status / Prerequisites

This branch includes the runtime load, validation, upload, status, and
entitlement-check path from ticket #338. Mint-host, marketplace, public-key
inspection, and Prometheus licensing counters remain separate child work:

| Command surface | Current status | Lands with |
|-----------------|-------------------------|------------|
| `POST /api/v1/admin/license/upload` | Operational through the same validator as startup load. Default `Licensing:AllowAdminUpload=false` returns HTTP `400`; enable it only when uploads are part of the operational workflow. | Landed with ticket #338. |
| `GET /api/v1/admin/license/status` | Operational. Returns edition, validation state, expiry, license id, licensee, and entitlements. | Landed with ticket #338. |
| `GET /api/v1/admin/observability/errors` | The endpoint is operational. Runtime licensing emits structured events `10000` through `10009` for no path, missing file, malformed file, unknown key, invalid signature, expired file, successful load, upload rejection/save failure, and entitlement denial. The endpoint returns the recent-error buffer; filter the response client-side for the licensing event-id range. | Landed with ticket #338 for the listed event ids. |
| `licenses_validated_total{result=...}` and `licenses_active{edition=...}` Prometheus counters | Counter shapes are documented in ADR-0033 but are not emitted by the #338 runtime baseline. | Telemetry counters child ticket. |

Confirm the target release includes ticket #338 before running the upload or
startup-file migration flow.

---

## Runtime Envelope

The runtime license file is UTF-8 JSON:

```json
{
  "version": 1,
  "keyId": "honua-2026-q2",
  "payload": "<base64url-encoded UTF-8 JSON payload bytes>",
  "signature": "<base64url Ed25519 signature over the payload bytes>"
}
```

The decoded payload is also JSON:

```json
{
  "schema": "honua.license/v1",
  "licenseId": "lic_123",
  "licensedTo": "Example Operator",
  "edition": "Pro",
  "issuedAt": "2026-05-06T00:00:00Z",
  "expiresAt": "2027-05-06T00:00:00Z",
  "entitlements": ["analytics.clustering"],
  "metadata": {
    "source": "byol"
  }
}
```

`schema`, `licenseId`, `licensedTo`, `edition`, and `issuedAt` are required.
`expiresAt` is optional; when present it must be in the future. The file is
rejected as `Malformed` when the envelope is missing required fields, exceeds
the 64 KiB runtime size limit, contains invalid Base64URL, or decodes to
invalid payload JSON. A `keyId` that is absent from `Licensing:TrustedKeys` is
`UnknownKey`; a signature mismatch is `InvalidSignature`; an expired file is
`Expired`.

Community-tier catalog entries are always active. Paid features are active only
when their catalog key is present in the signed `entitlements` array; the
operator-facing `edition` value does not automatically activate every paid
feature in that edition.

## Core Policy

1. **Do not force legacy customers without compatibility.** If a
   private-preview format exists in production, file a compatibility verifier
   or customer re-issue plan before upgrading those installs to a #338-only
   runtime.
2. **Roll forward for new issuances.** New BYOL files use the signed JSON
   envelope above.
3. **No indefinite legacy support.** Any temporary legacy branch must have a
   removal ticket, deadline, and source-backed inventory of affected customers.
4. **Evidence-driven cutover.** Use admin status, runtime logs, and deployment
   inventory for #338. Prometheus license counters remain a follow-on signal.

---

## Pre-Migration Checklist

Before deploying a signed license file to production, verify on a non-
production environment:

```bash
# Optional: confirm upload validates end-to-end after temporarily setting
# Licensing:AllowAdminUpload=true and configuring Licensing:LicensePath.
curl -X POST -H "X-API-Key: <admin-key>" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @license.honua-license.json \
  "https://<host>/api/v1/admin/license/upload"

# Confirm status surfaces edition, validation state, and entitlements.
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/license/status"
```

Verify:
- `validationState=Valid`, `isValid=true`, `edition` matches expected, and
  `expiresAt` is either absent/perpetual or in the future.
- `licenseId`, `licensedTo`, and active entitlements match the issued file.
- `/healthz/metrics` and `/monitoring/health/production` include the same
  license state under `license`. `/api/v1/metrics/health` does not include
  license state in the #338 baseline.

---

## Configuration

Runtime license loading is controlled by:

```
Licensing:LicensePath                  = /etc/honua/license.honua-license.json
Licensing:TrustedKeys:<key-id>         = base64url:<32-byte raw Ed25519 public key>
Licensing:AllowAdminUpload             = false
Licensing:ExpiryWarningDays            = 30
```

| Setting | Type | Notes |
|---------|------|-------|
| `Licensing:LicensePath` | string | Optional path to the signed license file. Empty/unset runs Community mode. |
| `Licensing:TrustedKeys:<key-id>` | string | Trusted raw Ed25519 public key as `base64url:<key>`, unprefixed Base64URL, or `base64:<key>`. The license envelope `keyId` must match this entry. |
| `Licensing:AllowAdminUpload` | bool | Enables admin upload to `LicensePath`. Default `false`. |
| `Licensing:ExpiryWarningDays` | int | Warning threshold surfaced in admin status. Default `30`. |

Apply the change through the standard configuration channel (env var,
`appsettings.Production.json`, Helm values, or the configured secret store).
A restart is required for startup load. Admin upload, when enabled, validates
and atomically replaces the configured file path at runtime.

---

## Migration Phases

### Phase 1 — Land signed JSON envelope

1. Deploy the release that includes ticket #338.
2. Configure `Licensing:TrustedKeys:<key-id>` on every instance.
3. Re-issue all existing in-house and staging files as signed JSON envelopes.
4. Configure `Licensing:LicensePath` and restart, or enable
   `Licensing:AllowAdminUpload=true` for the migration window and upload the
   file through `/api/v1/admin/license/upload`.
5. Verify `/api/v1/admin/license/status` shows `validationState=Valid`.

### Phase 2 — Enable dual-format verifier (only if a legacy format exists)

This phase only runs if the BYOL portal in a separate repo shipped a
private-preview format before ADR-0033 landed. If no legacy format exists,
skip directly to Phase 4.

No dual-format verifier ships in ticket #338. If a legacy private-preview
format exists, open a bounded follow-on ticket for the compatibility verifier
before attempting this phase. Until that lands, only the signed JSON envelope
loads.

### Phase 3 — Re-issue cadence and tracking

1. Track un-migrated installs through deployment inventory and the admin
   license status response. Runtime licensing emits structured validation
   state logs in the `10000`-`10009` event-id range.
2. Coordinate with the portal team to re-issue legacy files in canonical
   format on the customer's next renewal touchpoint. **Do not** force an
   out-of-cycle download.
3. Communicate the deadline at least 60 days before the cutover via the
   normal customer channel (release notes, support email, in-product
   banner if available).

### Phase 4 — Cutover

When every deployment has loaded a valid signed JSON license:

1. Disable `Licensing:AllowAdminUpload` unless ongoing uploads are part of
   the operating model.
2. Remove legacy files from configuration management and secret stores.
3. Update this runbook to record the cutover date.

---

## Verification

Per phase, verify with:

```bash
# License status
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/license/status"

# Recent license-related logs (admin observability)
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/observability/errors"
```

Expected after Phase 1: status shows `validationState=Valid`,
`isValid=true`, expected `edition`, and the signed entitlement set.

Expected during Phase 2/3: no `Malformed`, `UnknownKey`,
`InvalidSignature`, or `Expired` validation states for in-flight customer
files.

---

## Rollback

If the validator regresses on canonical files after a release:

1. Roll back the application to the previous release that loaded canonical
   files successfully (see `UPGRADE_AND_ROLLBACK.md`).
2. Confirm the previous release still trusts the signing key used for the
   currently deployed license files.
3. Page the licensing on-call.

If a follow-on dual-format verifier is later introduced, document its rollback
knobs in that ticket. The #338 baseline has no legacy parser.

---

## Communication Templates

Only use the dual-format templates below if a follow-on compatibility verifier
has shipped. The #338-only runtime cannot promise that legacy files continue to
load.

### Customer notification — dual format enabled

> Honua release N introduces a unified license file format consistent
> across BYOL and cloud-marketplace deployments. Your existing license file
> continues to work without action; we will reissue it in the new format
> at your next renewal. Files in the new format are interoperable across
> all editions; no changes to runtime configuration are required.

### Customer notification — deadline approaching (60-day notice)

> Your Honua license file uses the legacy format which is being retired
> on `<DEADLINE>`. We will reissue your file in the unified format at your
> next renewal (`<EXPECTED DATE>`). If your renewal falls after the
> deadline, please contact support so we can issue an interim canonical
> file.

### Internal — cutover PR

> Removes the dual-format verifier branch per ADR-0033 § Migration. The
> `licenses_validated_total{result="legacy_format_accepted"}` counter has
> been zero for `<N>` days; the deadline `<DEADLINE>` has passed. After
> this PR, files in the legacy format return `Malformed`. The
> migration runbook has been updated to record the cutover.

---

## Exit Criteria

The migration is complete when:

- All in-flight customer license files validate as canonical.
- Deployment inventory and admin status show no remaining legacy files. If a
  follow-on dual-format verifier adds `licenses_validated_total`, require
  `licenses_validated_total{result="legacy_format_accepted"}` to stay zero for
  14 consecutive days before removal.
- The configured deadline has passed.
- The cutover PR is merged.
- This runbook is updated with the cutover date and the time-bound section
  is removed.
