# Editions and licensing

Honua's source is available under the [Elastic License 2.0](https://github.com/honua-io/honua-server/blob/trunk/LICENSE) — free to use, deploy, and modify. At runtime, the server operates in one of three editions:

| Edition | How it activates | Scope |
|---|---|---|
| Community | Default — no license file configured | Baseline platform: all protocols, publishing, one-shot file import, portal token issuance |
| Pro | Signed license file with Pro entitlements | Adds features such as GeoServices FeatureServer editing, spatial analytics, real-time streams, Redis caching, geocoding, single-provider OIDC SSO |
| Enterprise | Signed license file with Enterprise entitlements | Adds features such as multi-provider OIDC governance, claim-to-role mapping, branch versioning, service imports, the plugin SDK |

Community requires no license and is unaffected by license expiry. Pro and Enterprise use the strict failure-mode contract: a missing, invalid or expired license refuses startup with a non-zero exit; no Community fallback is permitted. Declare `Licensing__Edition=Pro` or `Enterprise` so a missing source is identified as a paid deployment. Explicit `Community` ignores license sources; with no declared edition and no source the default remains Community.

## License files

A license is a small UTF-8 JSON envelope signed with Ed25519:

```json
{
  "version": 1,
  "keyId": "honua-2026-q2",
  "payload": "<base64url payload bytes>",
  "signature": "<base64url Ed25519 signature over the payload bytes>"
}
```

The decoded payload declares `licenseId`, `licensedTo`, `edition`, `issuedAt`, an optional `expiresAt`, and an `entitlements` array of feature keys. Validation is fully offline — the server verifies the signature against locally configured trusted public keys; no license server or phone-home is involved.

Feature activation is entitlement-based: Community-tier features are active on Community and on valid paid deployments, and a paid feature is active only when its key (for example `editing.featureserver-edits` or `identity.oidc`) appears in the signed `entitlements` array. GeoServices FeatureServer writes are Pro-gated; open-protocol edits through OGC API Features, WFS-T, OData, and gRPC remain Community while using the shared edit pipeline. Basic single-provider OIDC is a Pro entitlement; multi-provider OIDC configuration and claim-to-role mapping remain Enterprise entitlements. The `edition` label is operator-facing and does not by itself activate every feature in that edition. Inspect the full feature inventory at `GET /api/v1/admin/license/entitlements`.

## Configuration

```bash
Licensing__Edition=Pro
Licensing__LicensePath=/etc/honua/license.honua-license.json
Licensing__TrustedKeys__honua-2026-q2=base64url:<32-byte-raw-ed25519-public-key>
Licensing__AllowAdminUpload=false   # default
Licensing__ExpiryWarningDays=30     # default
```

- `Licensing__Edition` — `Community`, `Pro`, or `Enterprise`. Set it on every paid server and worker. If omitted, a configured source selects paid-license validation and a valid signed license determines the tier.
- `Licensing__LicensePath` — path to the signed license file and the location prefix for the persisted `.uploaded` override. With no paid edition declared and no file, inline content, or secret reference configured, the server runs Community mode.
- `Licensing__LicenseContent` — inline signed JSON envelope.
- `Licensing__LicenseContentSecretRef` — secret-store reference to the signed envelope.
- `Licensing__TrustedKeys__<keyId>` — trusted raw Ed25519 public key per key id (`base64url:<key>`, unprefixed Base64URL, or `base64:<key>`). The envelope's `keyId` must match a configured entry.
- `Licensing__AllowAdminUpload` — enables future runtime uploads through the admin API; disabling it does not undo a previously uploaded license.
- `Licensing__ExpiryWarningDays` — admin warning threshold, clamped to at least 30 days. It cannot disable the required 30/14/7/1-day warning schedule.

Startup reads `<LicensePath>.uploaded` first when present, then a resolved secret reference, inline content, and finally the ordinary `LicensePath` file. Admin upload takes effect immediately and survives restart. Persist and back up the containing directory, including both files. The authoritative file or secret is re-read every minute and on restart. Environment/configuration changes require a restart; replacing the ordinary file alone does not replace an existing uploaded override. Follow the renewal procedure below.

## How gating responds

When a request needs an entitlement that is not active:

- **HTTP protocols** return `402 Payment Required` with a problem response naming the missing entitlement and required edition.
- **gRPC** returns `FAILED_PRECONDITION` with the same upgrade message.

Denials are also logged as structured events (licensing event ids `10000`–`10009`, retrievable via `GET /api/v1/admin/observability/errors`).

## License status and upload via the admin API

In the authorized [API explorer](../reference/openapi-and-explorer.md), run `GET /api/v1/admin/license/status` to inspect the current edition, validation state, expiry, and entitlements. To replace the license when `Licensing__AllowAdminUpload=true`, run `POST /api/v1/admin/license/upload` and attach `license.honua-license.json` as the binary request body.

Upload runs the same validator as startup load and atomically commits the signed envelope at `<LicensePath>.uploaded`, then updates `LicensePath` as a compatibility mirror. A mirror-only failure is reported in the successful upload result; startup still uses the committed override. Upload returns `400` when `AllowAdminUpload` is `false`. The status response reports a `validationState` of `Valid`, `NoLicenseConfigured`, `MissingFile`, `Malformed`, `UnknownKey`, `InvalidSignature`, or `Expired` — an invalid paid license blocks operations and retains the paid tier rather than reverting to Community.

## Renew or replace a license

After any successful admin upload, renew through the upload endpoint so the persisted override is replaced as well. Upload the new signed envelope, then verify the expected `licenseId`, expiry, edition, and entitlements with `GET /api/v1/admin/license/status` and `GET /api/v1/admin/license/entitlements`. Further uploads may be disabled afterward without reverting the license.

To switch back to a file or configuration-managed source:

1. Stop the server and back up both license files and the licensing configuration.
2. Remove `<LicensePath>.uploaded`. Replace the ordinary `LicensePath` file, or update the intended inline/secret source. Remove or update any higher-precedence configured source so it cannot continue to select an older license.
3. Restart and verify the expected license identity, expiry, edition, and entitlements through the admin endpoints.

Replacing `LicensePath` and restarting while leaving `.uploaded` present continues to load the override. An invalid, unreadable, or expired override refuses paid startup and blocks paid runtime operations; it does not restore an older configured license. Coordinate renewal across replicas with separate filesystems. Uploads made by older server versions must be re-uploaded once after upgrade to establish the persisted override.

## Expiry and recovery

At expiry, Pro and Enterprise stop all data operations, including reads and exports
of existing data. In-flight jobs receive cancellation and are recorded as failed
with the exact reason `license expired`. Cancelled jobs do not present partial
outputs as complete: artifact references/download links are removed or an explicit
failed/incomplete import marker is persisted. Renewal does not resume those jobs;
inspect the failure and submit a new job after renewing.

Expiry warnings are surfaced in admin status and logs at **30, 14, 7 and 1 days**.
The admin banner remains visible between thresholds. Each log threshold is emitted
once per license expiry per process, including crossed thresholds at startup.
Renew before expiry, or complete the documented [backup/export procedure](../guides/deploy/backup-and-restore.md)
before expiry. Exporting through the expired paid instance is not a recovery path.

Renewal takes effect on restart and during the running instance's **one-minute
re-validation interval**. Admin upload also applies immediately. The authenticated
license status/upload recovery routes and health probes remain available while
paid data operations are blocked. Configure the same edition and authoritative
license source on each API replica and native worker.

For the exact startup errors and remedies, see
[operator troubleshooting](../guides/deploy/troubleshooting.md#license-failure-mode).

## Issuing Pro / Enterprise licenses (publisher only)

> This section is for the Honua publisher (whoever owns the signing key), not for
> operators consuming a license. Customers never run the mint tool.

License envelopes are minted offline with the `honua-license-mint` tool
(`src/Honua.LicenseMint`). It generates the Ed25519 signing key pair and signs the
canonical payload bytes the runtime verifier checks, so a minted file is accepted by
`GET /api/v1/admin/license/status` with `validationState=Valid`.

### 1. Generate a signing key pair (once per key id)

```bash
dotnet run --project src/Honua.LicenseMint -- \
  keygen --key-id honua-2026-q3 --private-out signing.key
```

This prints the public key and the exact `Licensing__TrustedKeys__<keyId>` setting to
configure on every server instance, and writes the private seed (Base64URL, `chmod 600`)
to `signing.key`. The public key is safe to publish; configure it as a trusted key on
the runtime. The private seed is the trust root for **every** license — see custody
rules below.

### 2. Mint a license

```bash
dotnet run --project src/Honua.LicenseMint -- \
  mint --key-id honua-2026-q3 \
       --license-id lic-acme-001 \
       --licensed-to "Acme Corp" \
       --edition Pro \
       --expires 365d \
       --capacity-units 4 \
       --annual-surge-days 14 \
       --surge-allowance standard \
       --private-key-file signing.key \
       --out acme.honua-license.json
```

- `--edition` is `Community`, `Pro`, or `Enterprise`. By default every `FeatureCatalog`
  feature at or below the edition is granted; pass `--entitlements key1,key2` to scope a
  license to specific feature keys (each must be a known catalog key).
- `--expires` accepts an RFC 3339 timestamp or a duration like `365d`. Omit it for a
  perpetual license. BYOL files are typically ≤ 1 year; marketplace-issued files ≤ 90 days
  (ADR-0033).
- `--capacity-units` signs the maximum sustained serving-unit band into the license.
  When present, `--annual-surge-days` defaults to `14` (or accepts `unlimited`) and
  `--surge-allowance` defaults to `standard` (`high` and `unlimited` are also valid).
  Omit all three options only when the commercial license is intentionally unbanded.
- The signing key can also be supplied inline with `--private-key <base64url>` or via the
  `HONUA_LICENSE_SIGNING_KEY` environment variable.

Hand `acme.honua-license.json` to the customer; they load it via `Licensing__LicensePath`
(or the admin upload endpoint) as described above.

### Key custody

The Ed25519 **private seed is the trust root** for the entire licensing system — anyone
holding it can mint a license for any edition.

- **Never commit it.** Keep `signing.key` (and any inline key value) out of version
  control; the mint tool restricts the written file to owner-only permissions on
  POSIX hosts as a best-effort safeguard.
- **Store it in a secret manager** (AWS Secrets Manager, Azure Key Vault, a sealed
  secret, or an offline air-gapped store), not in CI logs or shared drives.
- **Rotate by adding, not replacing.** `Licensing__TrustedKeys` is additive: configure a
  new `keyId` public key alongside the old one, mint new files with the new key, and retire
  the old key once the longest-lived file signed with it has expired (ADR-0033, key-rotation
  runbook).
- **The public key is not secret.** Only the public key goes into `Licensing__TrustedKeys`;
  distributing it does not weaken signing.

The hosted-mint admin API (`POST /api/v1/admin/license/mint`) and marketplace adapters
described in [ADR-0033](../internal/contributor/adr/0033-unified-license-format.md) are
follow-on work; this tool is the offline BYOL minting path and the signing primitive those
hosted flows reuse.

## Migrating existing licenses

The runtime accepts only the signed JSON envelope above; there is no legacy license parser. To move existing deployments onto it:

1. Deploy a server release that includes runtime licensing and configure `Licensing__TrustedKeys__<keyId>` on every instance.
2. Obtain re-issued license files in the signed envelope format for every deployment.
3. Load each file through the upload endpoint with `Licensing__AllowAdminUpload=true`, or provision the configured source using [Renew or replace a license](#renew-or-replace-a-license). Remove any existing upload override before switching to file/configuration management; replacing the ordinary file alone is insufficient.
4. Verify `GET /api/v1/admin/license/status` shows `validationState=Valid`, the expected `edition` and `licenseId`, and the expected active entitlements.
5. Set `Licensing__AllowAdminUpload` back to `false` unless uploads are part of your operating model, and remove superseded license files from configuration management.

Validate on a non-production environment first. If a release regresses validation of known-good files, roll back to the previous release (confirm it still trusts the signing key in use) — see [Upgrade and rollback](../guides/deploy/upgrade-and-rollback.md).
