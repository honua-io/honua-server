# Unified License and Entitlement Architecture

This document is the implementation companion to ADR-0033. The ADR captures the
decision and rationale; this document captures the wire-level format, validator
internals, marketplace adapter flows, telemetry/log semantics, and the cache
invalidation matrix that downstream tickets will depend on.

The design covers three issuance tracks behind a single runtime contract:

- **BYOL** — Honua portal-issued long-lived signed file (issue #338).
- **AWS Marketplace** — adapter that mints internal license files from AWS
  Marketplace Entitlement Service state (and optionally consumes AWS License
  Manager seller-issued tokens).
- **Azure Marketplace** — adapter that mints internal license files from Azure
  SaaS Fulfillment v2 Resolve / Activate state and lifecycle webhooks.

All three tracks emit the same JWS envelope and are validated by one
`Ed25519LicenseValidator` instance on the runtime hot path.

---

## 1. Canonical License Envelope

### 1.1 Wire format

The license file is a compact JWS (RFC 7515): three Base64URL segments separated
by `.` characters.

```
<protected_header>.<payload>.<signature>
```

The file is UTF-8 text (no surrounding whitespace), persisted verbatim to disk
or to the configured secret store. File extension `.honua-license` is the
convention; consumers must not depend on the extension.

### 1.2 Protected header

```
{
  "alg": "EdDSA",
  "typ": "JWT",
  "kid": "honua-2026-q2"
}
```

| Field | Value |
|-------|-------|
| `alg` | `EdDSA` (RFC 8037). The validator rejects any other value. |
| `typ` | `JWT`. Required. |
| `kid` | Key identifier resolved against the public-key set (see § 4.2). |
| `cty` | Optional. Reserved for future variants. Ignored when absent. |

The `alg=none` JWS attack class is closed by hard-coding the accepted
algorithm in the validator and rejecting unknown values before any decoding.

### 1.3 Payload claims

Payload claims use snake_case names so the source-generated JSON context can
emit canonical wire format with `JsonKnownNamingPolicy.SnakeCaseLower`.

```
{
  "iss": "honua.io",
  "sub": "license:01964e84-2c3a-7c5e-9b22-2f7d8e1c4a52",
  "iat": 1748563200,
  "nbf": 1748563200,
  "exp": 1780099200,
  "license_id": "01964e84-2c3a-7c5e-9b22-2f7d8e1c4a52",
  "edition": "enterprise",
  "entitlements": [
    "alerts.advanced",
    "channels.slack",
    "raster.cog",
    "analytics.spatial"
  ],
  "issued_to": {
    "name": "Acme Public Works",
    "email": "ops@acme.example",
    "org": "Acme Corp"
  },
  "issuance_source": "aws-marketplace",
  "marketplace": {
    "subscription_id": "arn:aws:license-manager::123456789012:license-configuration:lic-...",
    "account_id": "123456789012",
    "product_code": "ABCDEFGHIJKL01234567",
    "plan_id": "honua-enterprise-monthly"
  },
  "tenant_id": null
}
```

| Claim | Type | Required | Notes |
|-------|------|---------|-------|
| `iss` | string | yes | Always `"honua.io"`. |
| `sub` | string | yes | `license:<license-id>`. |
| `iat`, `nbf`, `exp` | int (Unix seconds, UTC) | yes | `nbf <= iat <= exp`. |
| `license_id` | string (UUID v7) | yes | Stable per issuance. |
| `edition` | string | yes | `community` \| `pro` \| `enterprise`. |
| `entitlements` | string[] | yes | Resolved against `FeatureCatalog`. Unknown keys log a warning at INFO and are dropped. |
| `issued_to` | object | yes | `{ name, email, org }`. `email` and `org` are optional inside the object. |
| `issuance_source` | string | yes | `byol-portal` \| `aws-marketplace` \| `azure-marketplace`. |
| `marketplace` | object \| null | conditional | Required when `issuance_source != byol-portal`. Fields are issuance-source dependent (see § 1.5). |
| `tenant_id` | string (UUID) \| null | no | Reserved for future multi-tenancy. |

The validator rejects payloads missing required claims, payloads exceeding 8 KiB
after Base64URL decode, and payloads where `nbf > now + ClockSkew` or
`exp <= now - ClockSkew`. Default `ClockSkew = 60s`.

### 1.4 Expiry policy by source

| `issuance_source` | Maximum `exp - iat` | Refresh trigger |
|-------------------|---------------------|-----------------|
| `byol-portal` | 366 days | Customer downloads a new file from the portal. |
| `aws-marketplace` | 90 days | `AwsEntitlementPollerService` re-mints when `GetEntitlements` state diverges or within `RefreshLeadTime` (default 14 days) of `exp`. |
| `azure-marketplace` | 90 days | Webhook event or `AzureSubscriptionReconcilerService` triggers re-mint; same lead time. |

The mint service rejects requests that violate the per-source expiry cap.

### 1.5 Marketplace claim shape

For `aws-marketplace`:

```
"marketplace": {
  "subscription_id": "<AWS Marketplace customer identifier>",
  "account_id": "<12-digit AWS account ID>",
  "product_code": "<20-char Marketplace product code>",
  "plan_id": "<dimension or contract identifier>"
}
```

For `azure-marketplace`:

```
"marketplace": {
  "subscription_id": "<Azure SaaS subscription ID, GUID>",
  "account_id": "<purchaser tenant ID, GUID>",
  "product_code": "<offer ID>",
  "plan_id": "<plan ID>"
}
```

Validators do not interpret `marketplace` for gating decisions; the field
exists for reconciliation, telemetry, and admin visibility.

### 1.6 Size and serialization rules

- Decoded payload size is bounded at 8 KiB. The validator computes this before
  parsing JSON, returning `LicenseValidationResult.PayloadTooLarge` for
  oversize files. The bound prevents deserialization-amplified attacks and
  caps webhook body sizes when the file is delivered through reconciliation.
- Source-generated JSON via `LicenseDomainJsonContext`
  (`System.Text.Json.Serialization.JsonSerializerContext`) with
  `JsonKnownNamingPolicy.SnakeCaseLower` and
  `JsonIgnoreCondition.WhenWritingNull` for optional fields.
- The signing input is the exact bytes of `<header>.<payload>` (Base64URL
  segments). The validator never re-serializes the header or payload before
  verification.

---

## 2. Validator Topology

### 2.1 Single hot-path entry

```
ILicenseManager.GetLicenseInfoAsync()
  └── ILicenseStore.GetCurrentSnapshotAsync()
        └── (cached, keyed by license_id + iat + kid)
              └── Ed25519LicenseValidator.Validate(licenseBytes)
                    ├── JwsParser (split + base64url decode)
                    ├── ILicensePublicKeyResolver.Resolve(kid)
                    ├── NSec.Cryptography.SignatureAlgorithm.Ed25519
                    └── ClockSkewPolicy.IsAcceptable(iat, nbf, exp)
```

`ILicenseManager` is the one runtime gating contract. `Ed25519LicenseValidator`
is `sealed` and stateless. `Honua.Architecture.Tests` enforces both.

### 2.2 Per-request gates

Per-request feature gates check the in-memory `LicenseInfo` snapshot through
`ILicenseManager.GetLicenseInfoAsync`. They never re-run signature verification.
Validation runs once on bootstrap and on hot reload (file change, admin upload,
adapter re-mint event). Hot-reload wiring uses the existing
`Honua.Server/Features/Infrastructure/Events/` substrate so cache invalidation
piggybacks on a tested durable channel.

### 2.3 Validation result codes

`LicenseValidationResult` is a discriminated record with:

| Result | Meaning | Operator action |
|--------|---------|-----------------|
| `Valid(LicenseInfo)` | Signature, claims, and expiry are all OK. | None. |
| `MalformedEnvelope` | Not a 3-segment JWS or the segments fail Base64URL decode. | Inspect the file; reissue. |
| `UnsupportedAlgorithm` | `alg` is not `EdDSA` or `typ` is not `JWT`. | Reissue with the canonical header. |
| `UnknownKeyId(kid)` | `kid` does not resolve in the configured key set. | Add the rotated key via configuration; restart not required. |
| `PayloadTooLarge` | Decoded payload exceeds 8 KiB. | Review portal/mint output; file an incident. |
| `MalformedClaims(detail)` | JSON does not deserialize against `LicenseClaims`. | Reissue. |
| `ClaimViolation(detail)` | Required claim missing or invariant broken (e.g., `nbf > exp`). | Reissue. |
| `Expired(at)` | `exp` is in the past beyond clock skew. | Reissue. Adapter triggers re-mint automatically. |
| `NotYetValid(at)` | `nbf` is in the future beyond clock skew. | Wait or reissue; investigate clock drift. |
| `SignatureInvalid` | Ed25519 verification failed. | Treat as tampering; alert. |
| `InternalError(detail)` | Crypto provider failure. | Capture diagnostics; check NSec / BouncyCastle smoke. |

The validator returns the result rather than throwing; outer layers surface
RFC 7807 problem JSON via the shared problem helpers (no raw exception bodies).

### 2.4 AOT / trimming

- **Crypto**: `NSec.Cryptography` (libsodium native binding, AOT-validated on
  `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`). If NSec fails AOT smoke
  on any RID, the build references the BouncyCastle managed Ed25519 fallback
  for that RID. Both implementations share the validator's golden-vector
  test set.
- **JSON**: `LicenseDomainJsonContext` source-generated. The validator does
  not call `JsonSerializer.Deserialize<T>()` against runtime metadata.
- **Logging**: `[LoggerMessage]`-generated emitters; no `string.Format` in
  hot paths.
- **No reflection**: `JsonSerializerIsReflectionEnabledByDefault=false` and
  `PublishAot=true` are already enforced in `Honua.Server.csproj`.

### 2.5 Performance budget

- Validator self-time per call: ≤ 100 µs on commodity x64. Validation runs
  only on bootstrap / hot reload, so per-request overhead is dominated by
  the `LicenseInfo` field read.
- File parse + deserialize: ≤ 1 ms even at the 8 KiB ceiling.
- Webhook ack budget: < 1 s p99 on test environments (well inside the Azure
  SaaS Fulfillment v2 10 s SLA).

---

## 3. Multi-Key Rotation

### 3.1 Resolver contract

```csharp
public interface ILicensePublicKeyResolver
{
    LicensePublicKey? Resolve(string kid);
    IReadOnlyList<LicensePublicKey> CurrentKeys { get; }
}

public sealed record LicensePublicKey(
    string Kid,
    ReadOnlyMemory<byte> PublicKeyBytes,
    DateTimeOffset NotBefore,
    DateTimeOffset? NotAfter,
    LicenseKeySource Source);

public enum LicenseKeySource
{
    BakedIn,
    Configuration,
}
```

### 3.2 Key composition rules

- The **baked-in primary** is compiled into `Honua.Core` as an embedded
  resource at release time. Configuration cannot delete it; deletion requires
  a release.
- **Configuration-additive** keys load from `License:Keys` (env-var-first
  `IOptions<T>`). The list is monitored by `IOptionsMonitor<T>`; a change
  publishes a `LicenseKeysChanged` event that invalidates the public-key
  cache (§ 5).
- A key is "active" when `now ∈ [NotBefore, NotAfter ?? +∞)`. Expired keys
  remain in the resolver only long enough for the next reload to drop them;
  the resolver `Resolve(kid)` returns `null` for keys outside their window
  even if they are still listed.
- `kid` collisions are forbidden. The resolver fails fast at startup if
  configuration redefines a baked-in `kid`.

### 3.3 Rotation flow (runbook reference)

The `LICENSE_KEY_ROTATION` runbook walks rotations end-to-end:

1. Generate a new keypair on the offline signing host.
2. Add the public key to `License:Keys` configuration; it begins serving
   verification immediately.
3. Switch the mint host to sign with the new private key.
4. Re-issue BYOL files on the next portal cadence; adapter-issued files
   re-mint automatically within `RefreshLeadTime`.
5. Set `NotAfter` on the retired key once the longest-lived in-flight file
   has expired plus a margin.
6. Remove the retired key from configuration in a follow-up change.

The runbook smoke test exercises a key rotation cycle and verifies that
licenses signed by the old `kid` remain valid through retirement.

---

## 4. Mint Topology

### 4.1 Mint host placement

The mint host lives in `Honua.Server` for v1 behind admin-scoped Minimal API
endpoints:

| Endpoint | Scope | Purpose |
|----------|-------|---------|
| `POST /admin/license/mint` | M2M (admin scope) | Issue a new license from supplied claims. |
| `POST /admin/license/refresh` | M2M (admin scope) | Re-sign an existing license whose claims have not changed (refresh-only path). |

Signing material loads only when `License:Signing:Enabled=true`. Customer-side
deployments leave it `false` and these endpoints return `404` to keep the
signing surface invisible.

Future extraction to a `Honua.LicenseMint.*` deployable changes only host
wiring; the public abstractions in `Honua.Core/Features/Licensing/Abstractions/`
do not move.

### 4.2 BYOL flow

```
[Honua portal (separate repo)]
        │
        │ POST /admin/license/mint  (M2M bearer)
        ▼
[Hosted mint host]
        │
        │ Ed25519LicenseSigner
        ▼
[Signed file] ─────► [Customer downloads from portal]
```

BYOL files default to `exp - iat = 365 days`. Customer-side servers load the
file from `License:File:Path` (or upload via the admin endpoint) and validate
against the local public-key set. No network call.

### 4.3 AWS Marketplace adapter

#### Mint path (default)

```
[Customer Honua Server]
   AwsEntitlementPollerService (BackgroundService, PeriodicTimer)
        │
        │ AWS Marketplace Entitlement Service: GetEntitlements
        │
        ▼
   AwsMarketplaceLicenseAdapter
        │
        │ POST /mint  with (entitlement payload, AWS signature, identity)
        ▼
[Honua hosted mint]
        │ verifies via publisher AWS credentials
        │ Ed25519 sign  (exp ≤ 90d)
        ▼
[Signed file]
        │
        ▼
[FileLicenseStore]  → invalidates cached snapshot → re-runs validator
```

The `RegisterUsage` call runs once on container start
(`AwsRegisterUsageOnStart` `IHostedService`) for EKS / ECS deployments; failures
are logged but do not block startup unless `Aws:RegisterUsage:RequiredOnStart`
is `true`.

#### ALM seller-issued path (optional, deferred to a follow-up)

When `Aws:UseSellerIssuedLicenses=true`, the adapter fetches the seller-issued
token via AWS License Manager and validates it locally against the ISV-owned
KMS public key. This skips the mint round-trip but introduces a second token
format. The path is feature-flagged off in v1; the validator's hot path
remains single-pathed.

#### Metering

```
[Per-request usage producer]
        │
        ▼
MarketplaceMeteringQueue (durable buffer, in-memory + Redis)
        │
        ▼
AwsMeteringWorker (BackgroundService, PeriodicTimer)
        │
        │ MeterUsage / BatchMeterUsage with retry-with-backoff
        ▼
[AWS Marketplace Metering Service]
```

The metering write path **never** runs inline on the request path. The durable
buffer reuses `FeatureChangeRetryQueue` semantics so transient AWS API
failures do not lose records. Reconciliation telemetry (§ 6) reports buffer
depth, retry count, and dead-letter count.

### 4.4 Azure Marketplace adapter

```
[Azure Marketplace]
        │  Azure AD JWT bearer
        ▼
POST /marketplace/azure/webhook
        │
        │ AzureWebhookEndpoint
        │   1. verify JWT (issuer + audience + signature)
        │   2. persist event to MarketplaceWebhookQueue
        │   3. ACK 200  (target < 1 s p99, hard SLA 10 s)
        ▼
[Background] AzureSubscriptionReconcilerService
        │
        │ Get Subscription (publisher credentials)
        │ POST /mint  → Ed25519 sign  (exp ≤ 90d)
        ▼
[FileLicenseStore]  → invalidates cached snapshot → re-runs validator
```

Resolve / Activate landing-page flow:

```
POST /marketplace/azure/resolve   (Azure-supplied marketplace token)
   AzureLandingPageEndpoints.ResolveAsync
        │ Resolve API (server-to-server)
        ▼
   Render landing page with subscription metadata

POST /marketplace/azure/activate
   AzureLandingPageEndpoints.ActivateAsync
        │ Activate API (server-to-server)
        │ POST /mint  → Ed25519 sign  (exp ≤ 90d)
        ▼
   Customer download / auto-deploy
```

Metering uses the symmetric `MarketplaceMeteringQueue` with
`AzureMeteringWorker` calling the Azure Marketplace Metered Billing API.

### 4.5 Adapter ↔ mint authentication

mTLS or M2M bearer against the Honua mint host. Customer-side credentials
live in the configured secret store: env vars for local; Kubernetes Secrets;
AWS Secrets Manager; Azure Key Vault. The mint host enforces:

- Audience claim (`mint:client:<customer-id>`).
- Per-customer rate limit (config-driven; defaults conservative).
- Independent re-verification of marketplace evidence (AWS publisher
  credentials, Azure publisher credentials).

---

## 5. Caching and Invalidation Matrix

`ICacheService.RemoveByPatternAsync` drives invalidation. Cache keys:

| Cache | Key | TTL | Invalidation triggers |
|-------|-----|-----|-----------------------|
| Validated license snapshot | `license:snapshot:{license_id}:{iat}:{kid}` | 1 hour | File-watcher change, admin upload, adapter re-mint event, `License:Keys` change. |
| Marketplace subscription state | `marketplace:{cloud}:subscription:{subscription_id}` | 1 hour | Webhook event, manual reconciler trigger. |
| Public-key set | `license:keys:current` | 5 minutes | `IOptionsMonitor<LicenseKeysOptions>` change. |
| AWS entitlements last seen | `marketplace:aws:entitlements:{customer_id}` | 24 hours | Poll cadence, manual refresh. |

The validator does not cache invalid results; only `Valid(LicenseInfo)` is
cached, and only after signature verification succeeds.

---

## 6. Telemetry

### 6.1 ActivitySources

| ActivitySource | Spans |
|----------------|-------|
| `Honua.Licensing.Validator` | `validate_license`, `parse_jws`, `verify_signature`, `resolve_kid` |
| `Honua.Licensing.Mint` | `mint_license`, `refresh_license`, `verify_marketplace_evidence` |
| `Honua.Licensing.Aws` | `poll_entitlements`, `register_usage`, `meter_usage`, `submit_mint_request` |
| `Honua.Licensing.Azure` | `webhook_receive`, `webhook_ack`, `resolve_subscription`, `activate_subscription`, `reconcile_subscription`, `meter_usage`, `submit_mint_request` |

### 6.2 Meter counters

| Counter | Labels | Notes |
|---------|--------|-------|
| `licenses_issued_total` | `source` | Increments on every successful mint. |
| `licenses_validated_total` | `result` | One of the `LicenseValidationResult` values. |
| `licenses_active` | `edition` | Gauge; updated on hot reload. |
| `marketplace_metering_records_total` | `cloud`, `result` | `cloud` ∈ `aws`, `azure`; `result` ∈ `enqueued`, `succeeded`, `failed`, `dead_lettered`. |
| `marketplace_webhook_events_total` | `cloud`, `kind`, `result` | `kind` covers Resolve, Activate, Suspended, Reinstated, Unsubscribed, plan-change, quantity-change. |
| `marketplace_reconciler_runs_total` | `cloud`, `result` | Background worker outcomes. |

### 6.3 Logging

`Honua.Server/Features/Infrastructure/Logging/Log.cs` extends with a license
event-id band:

| Range | Domain |
|-------|--------|
| `6000-6099` | Validator (parse, verify, expiry, resolution). |
| `6100-6199` | License store / file watcher / hot reload. |
| `6200-6299` | Mint endpoints and signing pipeline. |
| `6300-6499` | AWS adapter (poller, RegisterUsage, metering, mint submit). |
| `6500-6699` | Azure adapter (webhook, reconciler, landing page, metering, mint submit). |

All emitters are `[LoggerMessage]` source-generated. INFO logs redact
emails, subscription IDs, and account IDs to stable hashes (truncated SHA-256
hex). Raw IDs require explicit DEBUG and the redaction policy.

---

## 7. Configuration

### 7.1 New options

```
License:File:Path                      = /var/lib/honua/license.honua-license
License:File:HotReload                 = true
License:Signing:Enabled                = false             # publisher-only
License:Signing:KeyId                  = honua-2026-q2
License:Signing:PrivateKeyRef          = secret://...
License:Keys:0:Kid                     = honua-2026-q2
License:Keys:0:PublicKey               = base64url:...
License:Keys:0:NotBefore               = 2026-04-01T00:00:00Z
License:Keys:0:NotAfter                = 2027-04-01T00:00:00Z
License:Validation:ClockSkewSeconds    = 60

License:Migration:DualFormatEnabled    = false
License:Migration:DualFormatDeadline   = 2026-10-26T00:00:00Z

Marketplace:SkuMap:0:ProductCode       = ABCDEFGHIJKL01234567
Marketplace:SkuMap:0:PlanId            = honua-enterprise-monthly
Marketplace:SkuMap:0:Edition           = enterprise
Marketplace:SkuMap:0:Entitlements:0    = alerts.advanced
Marketplace:SkuMap:0:Entitlements:1    = channels.slack

Aws:Marketplace:Enabled                = false
Aws:Marketplace:CustomerIdentifier     = ...
Aws:Marketplace:PollIntervalSeconds    = 3600
Aws:Marketplace:RegisterUsage:RequiredOnStart = false
Aws:Marketplace:UseSellerIssuedLicenses = false
Aws:Marketplace:Mint:BaseUrl           = https://mint.honua.io
Aws:Marketplace:Mint:CredentialRef     = secret://...

Azure:Marketplace:Enabled              = false
Azure:Marketplace:Publisher:TenantId   = ...
Azure:Marketplace:Publisher:ClientId   = ...
Azure:Marketplace:Publisher:ClientSecretRef = secret://...
Azure:Marketplace:Webhook:AllowedAudiences:0 = api://honua-marketplace
Azure:Marketplace:Webhook:MaxBodyKiB   = 8
Azure:Marketplace:Mint:BaseUrl         = https://mint.honua.io
Azure:Marketplace:Mint:CredentialRef   = secret://...

Mint:RefreshLeadTimeDays               = 14
```

All options are validated by `IValidateOptions<T>`. Boolean toggles default
to `false` so a stock customer build does not load any marketplace dependency
or signing surface.

### 7.2 Air-gapped deployments

An air-gapped install ships with:

- A baked-in primary public key in `Honua.Core` for offline verification.
- `License:File:Path` pointing at the customer-supplied BYOL file.
- All `Aws:Marketplace:*` and `Azure:Marketplace:*` settings unset (or
  `Enabled=false`) so no marketplace SDK code path runs.

The validator never resolves DNS, opens a socket, or reads from a public-key
URL. This is asserted by an architecture test.

---

## 8. Testing Strategy

| Layer | Tests | Substrate |
|-------|-------|-----------|
| Unit (`Honua.Core.Tests/Features/Licensing/`) | Golden-vector validator suite (≥ 30 vectors covering each `LicenseValidationResult`); JWS parser fuzz cases (truncated, malformed Base64URL, oversize); claim deserialization; clock-skew edges; multi-`kid` resolver including expired keys; signer round-trip. | Pure compute, no fixtures. |
| Integration (`Honua.Server.Tests/Features/Licensing/`) | Admin mint endpoints (M2M auth, options-disabled `404`); file watcher hot reload; AWS poller against stub `IAmazonMarketplaceEntitlementService`; Azure webhook with stub fulfillment client (10 s SLA assertion); durable buffer fault-injection (Redis down → in-memory fallback). | Testcontainers + Postgres + Redis + admin scope. |
| Architecture (`Honua.Architecture.Tests`) | No `Honua.Server` symbols leak into `Honua.Core/Features/Licensing/`; validator class is `sealed`; public types in `Honua.Core/Features/Licensing/Abstractions/` and `Honua.Core/Features/Licensing/Domain/` carry XML docs; mint endpoints registered when (and only when) `License:Signing:Enabled=true`; no `System.IdentityModel.Tokens.Jwt` reference graph reaches `Honua.Core` or `Honua.Server`. | Roslyn analyzers + assembly scan. |
| Smoke | AOT publish on `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`; key-rotation runbook smoke (old + new `kid` coexistence, retirement). | CI matrix. |

Per ADR-0011 every endpoint has at least one integration test and the
`EndpointRegistry` and `OperationRegistry` architecture tests fail closed if
new endpoints land without coverage.

---

## 9. Migration

The existing licensing slice exposes only abstractions; no license file
format has shipped from this repo. The first BYOL file the portal issues
already conforms to the format above.

If the portal in a separate repo has shipped a private-preview file format,
the dual-format verifier path runs for a configurable grace window
(`License:Migration:DualFormatEnabled`, `License:Migration:DualFormatDeadline`,
default 6 months from ADR landing). Behavior:

- Both formats parse → canonical wins; legacy parse is logged as
  `licenses_validated_total{result="legacy_format_accepted"}`.
- Only legacy parses → accepted with a deprecation warning at INFO
  (`6010` event-id).
- Neither parses → `MalformedEnvelope`.

Operators can monitor un-migrated installs via the deprecation counter and
re-issue at the next BYOL portal cadence. After the deadline the legacy
branch is removed in a single PR and the option is deleted.

The `LICENSE_MIGRATION` runbook walks operators through the cutover
including telemetry checkpoints and the legacy-branch removal milestone.

---

## 10. Open follow-ups recorded outside this design

- Optional revocation / kill-switch channel (out of scope here; tracked
  separately). Long-lived BYOL files magnify compromised-key blast radius;
  the rotation runbook provides the in-scope mitigation.
- Marketplace SKU → edition mapping owned by sales / legal (out of scope;
  blocking for #390). Adapters consume `Marketplace:SkuMap` so updates do
  not require a code change.
- Mint host extraction to `Honua.LicenseMint.*` after v1 soak.
- Per-customer multi-tenancy beyond `tenant_id` claim presence.

---

## 11. References

- ADR-0033: Unified License Format and Entitlement Architecture (decision).
- ADR-0011, ADR-0013, ADR-0014, ADR-0015, ADR-0017, ADR-0018, ADR-0021,
  ADR-0024, ADR-0031: cross-cutting precedents this design reuses.
- RFC 7515, RFC 7517, RFC 7519, RFC 8032, RFC 8037.
- AWS Marketplace Entitlement Service, AWS Marketplace Metering Service,
  AWS License Manager, Azure Marketplace SaaS Fulfillment v2, Azure
  Marketplace Metered Billing.
- HashiCorp Enterprise / Elastic license file format (industry reference).
- Issues #338, #390, #645, #804, `honua-io/honua-server-admin#23`.
