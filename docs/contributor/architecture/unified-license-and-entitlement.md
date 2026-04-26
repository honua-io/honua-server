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
endpoints. All licensing routes follow the existing
`/api/v1/admin/license/...` convention used by the platform-admin license
endpoints already in `EndpointRegistry.cs`:

| Endpoint | Scope | Visible on customer instances? | Purpose |
|----------|-------|-------------------------------|---------|
| `POST /api/v1/admin/license/mint` | M2M (admin scope) | No — mint host only. | Issue a new license from supplied claims. |
| `POST /api/v1/admin/license/refresh` | M2M (admin scope) | No — mint host only. | Re-sign an existing license whose claims have not changed (refresh-only path). |
| `GET /api/v1/admin/license/signing/status` | M2M (admin scope) | No — mint host only. | Reports current `License:Signing:KeyId` and signer health. |
| `GET /api/v1/admin/license/keys` | Admin | Yes. | Inspects the resolved public-key set (`baked-in` primary + `License:Keys` additions). |
| `POST /api/v1/admin/marketplace/{cloud}/reconcile` | Admin | Yes (when adapter enabled). | Manual reconciliation trigger; bypasses the timer. `cloud` ∈ `aws`, `azure`. |
| `POST /api/v1/marketplace/azure/webhook` | Public — Azure AD JWT bearer (publisher audience). | Yes (when Azure adapter enabled). | Azure SaaS Fulfillment v2 lifecycle webhook. Not admin-scoped. |
| `GET /api/v1/marketplace/azure/landing` | Public — browser GET. Microsoft redirects the purchaser to the configured landing page URL with `?token=<marketplace-token>`. The handler exchanges that token for subscription metadata via Microsoft's Resolve API (server-to-server, `x-ms-marketplace-token` header) and renders the activation page. | Yes (when Azure adapter enabled). | Browser-facing landing page. Not admin-scoped. |
| `POST /api/v1/marketplace/azure/activate` | Public — backend POST from the landing-page form once the purchaser confirms. The handler calls Microsoft's Activate API server-to-server. | Yes (when Azure adapter enabled). | Landing-page activate. Not admin-scoped. |

Signing material loads only when `License:Signing:Enabled=true`. Customer-
side deployments leave it `false` and the mint-host-only endpoints return
`404` to keep the signing surface invisible. The `GET /api/v1/admin/license/keys`
inspector remains available on every instance for resolver auditing.

Marketplace endpoints register only when the corresponding
`{Aws,Azure}:Marketplace:Enabled=true`, so air-gapped customers see no
Azure landing page or AWS reconcile route in the registry.

The Azure landing-page URL configured in the publisher's marketplace
offer must point at `GET /api/v1/marketplace/azure/landing` on a
public-facing customer host. Microsoft drives the purchaser's browser
to that URL with the marketplace token in the `?token=` query
parameter; the handler then calls Microsoft's Resolve API
server-to-server. Activate is a backend `POST
/api/v1/marketplace/azure/activate` invoked from the landing page once
the purchaser confirms.

Future extraction to a `Honua.LicenseMint.*` deployable changes only host
wiring; the public abstractions in `Honua.Core/Features/Licensing/Abstractions/`
do not move. After extraction, the mint-host-only routes
(`/api/v1/admin/license/{mint,refresh,signing/status}`) bind on the
extracted deployable's address — the operator-facing routes
(`/api/v1/admin/license/keys`, `/api/v1/admin/marketplace/{cloud}/reconcile`,
and the Azure landing-page surfaces `GET /landing` and `POST /activate`)
remain on `Honua.Server`.

### 4.2 BYOL flow

```
[Honua portal (separate repo)]
        │
        │ POST /api/v1/admin/license/mint  (M2M bearer)
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
        │   (response carries Entitlements + NextToken; no portable signature)
        │
        ▼
   AwsMarketplaceLicenseAdapter
        │
        │ POST /api/v1/admin/license/mint
        │   with (customer_identifier, account_id,
        │         product_code, dimensions/observed entitlements)
        ▼
[Honua hosted mint]
        │ re-queries GetEntitlements with publisher AWS credentials
        │ (authoritative — adapter-supplied state is treated as a hint, not evidence)
        │ Ed25519 sign  (exp ≤ 90d)
        ▼
[Signed file]
        │
        ▼
[FileLicenseStore]  → invalidates cached snapshot → re-runs validator
```

The AWS Marketplace Entitlement Service does not return a portable signed
token — that's what the optional ALM seller-issued path below is for.
The default mint path therefore treats the adapter's payload as a
trigger and re-verifies entitlement state against AWS using publisher
credentials before signing.

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

The Azure webhook handler implements the SaaS Fulfillment v2 contract.
Per Microsoft, the publisher must (a) ACK the webhook within the
10-second window so Microsoft does not retry-then-suspend
(<https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-webhook>),
and (b) `PATCH` the operation status against the operations API
(<https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-operations-api>)
**for the actions Microsoft surfaces as a pending operation
requiring publisher acknowledgement** — in v1, the two-phase actions
`ChangePlan` and `ChangeQuantity`. The Honua webhook handler is
intentionally narrow — JWT verify, payload capture, durable queue
write, ACK — so the inline ACK stays at < 1 s p99 and the 10-second
SLA is unconditional. The queue write must land in the durable
backing (Redis) before 200; the volatile in-memory implementation is
not a webhook-side fallback, because losing an event between ACK and
reconciler drain would break the at-least-once contract Microsoft
expects. If the durable substrate is unavailable, the handler
returns 5xx so Azure retries. Validation (`Get Operation`) and the
PATCH live in the reconciler.

For `ChangePlan` and `ChangeQuantity`, Microsoft drives the change to a
terminal state on its side regardless of whether the publisher PATCHes
inside the webhook window: an unpatched two-phase operation is
auto-completed (Microsoft commits the customer's requested change in
its billing). Honua's reconciler-deferred PATCH therefore does **not**
function as a "reject inside 10 s" hook — by the time the reconciler
runs, Microsoft has typically already auto-completed the change. The
reconciler PATCHes after `FileLicenseStore` accepts the new file so
the publisher's audit trail and Microsoft's operation record agree on
`Success` (or `Failure` if mint or apply fails); a deferred `Failure`
does **not** undo Microsoft's auto-success on its side, it only
updates the operations-API audit trail. Honua does **not** support
publisher-side rejection of `ChangePlan` / `ChangeQuantity` in v1; if
a future requirement adds that path, it requires an inline pre-flight
that runs before the ACK.

```
[Azure Marketplace]
        │  Azure AD JWT bearer
        ▼
POST /api/v1/marketplace/azure/webhook
        │
        │ AzureWebhookEndpoint
        │   1. verify JWT (issuer + audience + signature)
        │   2. capture (operationId, subscriptionId, action, status,
        │      planId?, quantity?); reject bodies above
        │      Azure:Marketplace:Webhook:MaxBodyKiB before parsing
        │   3. durably persist event to MarketplaceWebhookQueue
        │      (Redis-backed); on durable-write failure return 5xx
        │      so Azure retries — never ACK on volatile fallback
        │   4. ACK 200  (target < 1 s p99, hard SLA 10 s)
        ▼
[Background] AzureSubscriptionReconcilerService
        │
        │ 5. drain queue, dedupe by (subscriptionId, operationId)
        │ 6. Get Operation (publisher credentials) — payload
        │    validation. 404 is `operation_rejected` (drop). Conflict
        │    / already-Succeeded splits two ways: if our local audit
        │    log already records a terminal PATCH for the same
        │    operationId, dedupe and exit (a peer replica handled it);
        │    otherwise Microsoft auto-completed a ChangePlan /
        │    ChangeQuantity — continue with mint + apply but skip
        │    step 10 and record `result="auto_completed"`.
        │ 7. Get Subscription (publisher credentials) — only when
        │    the latest entitlement state is needed
        │ 8. POST /api/v1/admin/license/mint  → Ed25519 sign (exp ≤ 90d)
        │ 9. apply via FileLicenseStore → validator cache invalidated
        │ 10. For two-phase actions only (ChangePlan,
        │     ChangeQuantity): PATCH operation status (Success after
        │     step 9, Failure on mint/apply fault). Microsoft's
        │     auto-completion of those actions is independent of this
        │     PATCH; a `409 Conflict` means Microsoft already
        │     finalized the operation — record
        │     `result="auto_completed"` and continue. Single-phase
        │     notifications (Unsubscribe, Suspend, Reinstate, Renew,
        │     Transfer) skip step 10 entirely; Microsoft does not
        │     expose a pending operation requiring publisher
        │     acknowledgement for those.
        ▼
[FileLicenseStore]  → invalidates cached snapshot → re-runs validator
```

`PATCH operation status` is scoped to the actions Microsoft
surfaces as a pending operation requiring publisher
acknowledgement. In v1 that is the **two-phase** actions only:
`ChangePlan` and `ChangeQuantity`. For those, the reconciler
PATCHes `Success` after `FileLicenseStore` confirms the new file is
valid, or `Failure` after a mint / apply fault we cannot recover
from. Because Microsoft drives both two-phase operations to a
terminal state on its side regardless of the PATCH window, the
reconciler PATCH typically lands after Microsoft has already
auto-completed the change — the PATCH still records the publisher's
decision in the operations API audit trail and a `409 Conflict` is
the healthy auto-completion signal. A deferred `Failure` PATCH does
**not** undo Microsoft's auto-success; it only updates the audit
trail.

The single-phase notifications (`Unsubscribe`, `Suspend`,
`Reinstate`, `Renew`, `Transfer`) do not expose a pending operation
in the operations API — see the
[lifecycle reference](https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-life-cycle)
and
[operations API reference](https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-operations-api).
The reconciler still drains those events to update the local mirror
(re-mint with refreshed entitlements on `Reinstate`, mark the local
subscription as unsubscribed on `Unsubscribe`, etc.) and emits the
relevant reconciler / webhook telemetry, but it does **not** PATCH
the operations API for them.

Resolve / Activate landing-page flow:

```
[Purchaser browser, redirected by Microsoft to the configured landing URL]
   GET /api/v1/marketplace/azure/landing?token=<marketplace-token>
        │ AzureLandingPageEndpoints.LandingAsync
        │   1. validate the token query parameter is present and well-formed
        │   2. POST <fulfillment-api>/saas/subscriptions/resolve
        │      (server-to-server; `x-ms-marketplace-token: <marketplace-token>`)
        │   3. render activation page with subscription metadata
        ▼
   HTML response (or redirect to the admin UI's activation step)

[Browser POSTs the form on the rendered landing page]
   POST /api/v1/marketplace/azure/activate
        │ AzureLandingPageEndpoints.ActivateAsync
        │   4. POST <fulfillment-api>/saas/subscriptions/{id}/activate
        │      (server-to-server; publisher access token)
        │   5. POST /api/v1/admin/license/mint  → Ed25519 sign  (exp ≤ 90d)
        ▼
   Customer download / auto-deploy / "subscription is now active" view
```

Per Microsoft's SaaS Fulfillment v2 lifecycle
(<https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-life-cycle>),
`Resolve` is a server-to-server API call the publisher backend makes
with the marketplace token Microsoft handed off in the browser query
parameter. Our public-facing surface is therefore a browser `GET` for
the landing page; the actual `Resolve` call to Microsoft never appears
in the route table.

Metering uses the symmetric `MarketplaceMeteringQueue` with
`AzureMeteringWorker` calling the Azure Marketplace Metered Billing API.

### 4.5 Adapter ↔ mint authentication

mTLS or M2M bearer against the Honua mint host. Customer-side credentials
live in the configured secret store: env vars for local; Kubernetes Secrets;
AWS Secrets Manager; Azure Key Vault. The mint host enforces:

- Audience claim (`mint:client:<customer-id>`).
- Independent re-verification of marketplace evidence (AWS publisher
  credentials, Azure publisher credentials).

Per [ADR-0004](../adr/0004-proxy-rate-limiting.md), rate limiting is
enforced at the edge (nginx / ALB / API gateway) — the mint host does not
implement an in-app rate limiter. Per-customer abuse controls live in the
edge configuration alongside the mint host's public ingress.

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
| `Honua.Licensing.Azure` | `webhook_receive`, `webhook_ack`, `resolve_subscription`, `activate_subscription`, `reconcile_subscription`, `get_operation`, `patch_operation_status`, `meter_usage`, `submit_mint_request` |

### 6.2 Meter counters

| Counter | Labels | Notes |
|---------|--------|-------|
| `licenses_issued_total` | `source` | Increments on every successful mint. |
| `licenses_validated_total` | `result` | One of the `LicenseValidationResult` values. |
| `licenses_active` | `edition` | Gauge; updated on hot reload. |
| `marketplace_metering_records_total` | `cloud`, `result` | `cloud` ∈ `aws`, `azure`; `result` ∈ `enqueued`, `succeeded`, `failed`, `dead_lettered`. |
| `marketplace_webhook_events_total` | `cloud`, `kind`, `result` | `kind` covers Resolve, Activate, Suspended, Reinstated, Unsubscribed, plan-change, quantity-change. |
| `marketplace_reconciler_runs_total` | `cloud`, `result` | Background worker outcomes; `result` ∈ `succeeded`, `failed`, `unsubscribed`, `operation_rejected` (Microsoft Get Operation said the event was not real or already processed). |
| `marketplace_operation_status_patches_total` | `cloud`, `action`, `result` | Azure-only in v1. Emitted only for the two-phase actions Microsoft surfaces as pending operations: `action` ∈ `change_plan`, `change_quantity`. `result` ∈ `success_patched`, `failure_patched`, `patch_failed`, `auto_completed`. `auto_completed` covers the case where Microsoft already auto-completed the two-phase operation before the reconciler PATCH landed (`409 Conflict` from the operations API). It is a healthy outcome, not a failure; an upward trend is a reconciler-lag signal, not a customer-billing alert. Single-phase notifications (`Unsubscribe`, `Suspend`, `Reinstate`, `Renew`, `Transfer`) do not emit on this counter because Microsoft does not expose a pending operation requiring publisher PATCH for those — observe them on `marketplace_webhook_events_total` and `marketplace_reconciler_runs_total` instead. |

### 6.3 Logging

`Honua.Server/Features/Infrastructure/Logging/Log.cs` extends with a license
event-id band. The 6000-band is already reserved (Log.cs Tracing Operations
6000-6999) and populated by `PerformanceMonitoringLog`, `LayerStyleLog`, and
`NlQueryLog`; licensing claims the unused 10000-band:

| Range | Domain |
|-------|--------|
| `10000-10099` | Validator (parse, verify, expiry, resolution). |
| `10100-10199` | License store / file watcher / hot reload. |
| `10200-10299` | Mint endpoints and signing pipeline. |
| `10300-10499` | AWS adapter (poller, RegisterUsage, metering, mint submit). |
| `10500-10699` | Azure adapter (webhook, reconciler, landing page, metering, mint submit). |

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
  (`10010` event-id, in the licensing band reserved in § 6.3).
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
