# License Migration Runbook

Use this runbook to move existing Honua deployments from any pre-existing
license file format to the unified Ed25519 / JWS format defined in ADR-0033.
The migration is **non-forced**: existing BYOL customers continue to operate
through a configurable dual-format grace window while the unified format
becomes canonical.

---

## Scope

This runbook applies to:

- BYOL customers running an older Honua release that consumed a private-
  preview license file (if any portal-issued files predate ADR-0033).
- New BYOL customers receiving canonical-format files from day one.
- AWS Marketplace and Azure Marketplace customers — adapters mint
  canonical-format files automatically, so for them this runbook is
  informational only.

This runbook does **not** cover:

- Key rotation. See `LICENSE_KEY_ROTATION.md`.
- Marketplace lifecycle operations. See `MARKETPLACE_OPERATIONS.md`.

---

## Core Policy

1. **No forced re-issuance.** Existing BYOL customers must not be required to
   download a new file before the legacy format reaches its deprecation
   deadline.
2. **Roll forward.** New issuances always use the canonical format. Legacy
   files run through the dual-format verifier until the configured deadline.
3. **Time-bound the dual path.** The legacy parsing branch is removed in a
   single PR after the deadline. No indefinite legacy support.
4. **Telemetry-driven cutover.** Customers are tracked through deprecation
   counters; the deadline only advances when the in-flight legacy population
   is small enough that a re-issue sweep is feasible.

---

## Pre-Migration Checklist

Before enabling the dual-format verifier in production, verify on a non-
production environment:

```bash
# Confirm canonical format validates end-to-end
curl -X POST -H "X-API-Key: <admin-key>" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @canonical.honua-license \
  "https://<host>/api/v1/admin/license/upload"

# Confirm health surfaces edition + expiry
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/license/status"
```

Verify:
- The status response shows `issuance_source` matching the file's payload
  claim.
- `IsValid=true`, `Edition` matches expected, and `ExpiresAt` is in the
  future.
- The validator counter `licenses_validated_total{result="valid"}` ticks
  upward.

---

## Configuration

The dual-format verifier is gated by configuration so the legacy parsing
branch only runs when explicitly enabled:

```
License:Migration:DualFormatEnabled    = true
License:Migration:DualFormatDeadline   = 2026-10-26T00:00:00Z
```

| Setting | Type | Notes |
|---------|------|-------|
| `License:Migration:DualFormatEnabled` | bool | When `false`, only the canonical format parses. Default `false`. |
| `License:Migration:DualFormatDeadline` | RFC 3339 timestamp | After this instant the legacy branch is bypassed. The deadline is informational once the code path is removed; until then it controls runtime behavior. |

Apply the change through the standard configuration channel (env var,
`appsettings.Production.json`, Helm values, or the configured secret store).
A restart is **not** required — the validator picks up the change through
`IOptionsMonitor<LicenseMigrationOptions>`.

---

## Migration Phases

### Phase 1 — Land canonical format

1. Deploy the release that includes ADR-0033 and the validator (child
   ticket: validator + JSON context).
2. Leave `License:Migration:DualFormatEnabled=false`.
3. Re-issue all existing in-house and staging files in canonical format.
4. Verify the validator counter shows
   `licenses_validated_total{result="valid"}` matching expected install
   count.

### Phase 2 — Enable dual-format verifier (only if a legacy format exists)

This phase only runs if the BYOL portal in a separate repo shipped a
private-preview format before ADR-0033 landed. If no legacy format exists,
skip directly to Phase 4.

1. Set `License:Migration:DualFormatEnabled=true` and a future
   `License:Migration:DualFormatDeadline` (default 6 months from ADR-0033
   landing).
2. Confirm via test that:
   - Canonical files validate (`result="valid"`).
   - Legacy files validate with a deprecation warning
     (`result="legacy_format_accepted"`).
   - Garbage payloads fail with `result="malformed_envelope"`.
3. Roll out to production.

### Phase 3 — Re-issue cadence and tracking

1. Track un-migrated installs via the deprecation counter:

   ```
   licenses_validated_total{result="legacy_format_accepted"}
   ```

   Any non-zero rate indicates legacy files still in play. Per-deployment
   visibility comes from the structured log emitter (event-id `10010`) which
   records the `license_id` (full only at DEBUG, hashed at INFO).
2. Coordinate with the portal team to re-issue legacy files in canonical
   format on the customer's next renewal touchpoint. **Do not** force an
   out-of-cycle download.
3. Communicate the deadline at least 60 days before the cutover via the
   normal customer channel (release notes, support email, in-product
   banner if available).

### Phase 4 — Cutover

When the legacy counter has been zero for 14 consecutive days **and** the
deadline has passed:

1. Open the cutover PR. The PR removes:
   - The legacy parsing branch in the validator.
   - `LicenseMigrationOptions.DualFormatEnabled` and `DualFormatDeadline`
     options binding.
   - The `licenses_validated_total{result="legacy_format_accepted"}`
     metric label literal (the label is now unreachable).
   - The deprecation log emitter (`10010`).
2. Land the cutover PR. After release, attempts to parse a legacy file
   return `MalformedEnvelope` (the same code path as a corrupt canonical
   file).
3. Update this runbook to record the cutover date and remove this section.

---

## Verification

Per phase, verify with:

```bash
# License status
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/license/status"

# Validator + entitlement metrics (Prometheus scrape)
curl https://<host>/metrics | grep licenses_validated_total
curl https://<host>/metrics | grep licenses_active

# Recent license-related logs (admin observability)
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/observability/errors?eventIdMin=10000&eventIdMax=10299"
```

Expected after Phase 1: `licenses_validated_total{result="valid"}`
increments; `licenses_active{edition="..."}` reflects the running
deployment.

Expected during Phase 2/3: zero rate of `result="signature_invalid"`,
`result="expired"`, or `result="malformed_envelope"` for in-flight customer
files. A `legacy_format_accepted` rate that does not trend downward
indicates customer outreach is needed.

---

## Rollback

If the validator regresses on canonical files after a release:

1. Roll back the application to the previous release that loaded canonical
   files successfully (see `UPGRADE_AND_ROLLBACK.md`).
2. Set `License:Migration:DualFormatEnabled=true` if it is not already.
3. Page the licensing on-call.

If the validator regresses on legacy files during the dual-format window:

1. Confirm `License:Migration:DualFormatEnabled=true` is loaded — check
   `/api/v1/admin/config` (admin-scoped) for the effective value.
2. If the option is disabled in higher-precedence configuration (env var
   override), set it via the highest-precedence channel and verify reload
   through the configuration counter.
3. If the dual-format flag is correct and legacy parses still fail, capture
   the offending file's `license_id` (DEBUG log channel) and escalate.

The legacy parser is intentionally a thin compatibility shim — it does not
become the canonical path. **Never** disable the canonical format to "let
legacy keep working"; that defeats the migration.

---

## Communication Templates

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
> this PR, files in the legacy format return `MalformedEnvelope`. The
> migration runbook has been updated to record the cutover.

---

## Exit Criteria

The migration is complete when:

- All in-flight customer license files validate as canonical.
- `licenses_validated_total{result="legacy_format_accepted"}` has been
  zero for 14 consecutive days.
- The configured deadline has passed.
- The cutover PR is merged.
- This runbook is updated with the cutover date and the time-bound section
  is removed.
