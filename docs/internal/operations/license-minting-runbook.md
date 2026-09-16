# Minting Pro / Enterprise licenses (publisher runbook)

> **Internal.** This runbook is for the Honua publisher — whoever holds the signing key.
> It is deliberately kept out of `docs/SUMMARY.md` so it is never vendored to the public
> documentation site. Customers never run the mint tool; the customer-facing
> [Editions and licensing](../../concepts/editions-and-licensing.md) page tells them to
> contact Honua instead.
>
> Background and the full mint topology are in
> [ADR-0033](../contributor/adr/0033-unified-license-format.md) and
> [Unified license and entitlement](../contributor/architecture/unified-license-and-entitlement.md).
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

A hosted-mint admin API and marketplace adapters
described in [ADR-0033](../contributor/adr/0033-unified-license-format.md) are
follow-on work; this tool is the offline BYOL minting path and the signing primitive those
hosted flows reuse.

