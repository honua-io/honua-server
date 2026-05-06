# ADR-0033: Unified License Format and Entitlement Architecture

## Status

Accepted

## Context

ADR-0024 established the open-core edition model and committed to offline-capable license enforcement. The licensing slice in `Honua.Core/Features/Licensing/` (`ILicenseManager`, `ILicenseStatusProvider`, `LicenseInfo`, `Entitlement`, `FeatureCatalog`, `HonuaEdition`) reserved the runtime contract.

> Implementation note, ticket #338: `honua-server` now ships the runtime baseline for offline license loading. The active runtime file is a JSON envelope `{ version, keyId, payload, signature }`, with `payload` and `signature` Base64URL encoded and Ed25519 verification over the exact payload bytes. Runtime public keys come from `Licensing:TrustedKeys:<keyId>`; no baked-in verification key, mint-host, marketplace, public-key inspection, hot reload, or Prometheus counter work ships in the #338 baseline.

Honua now needs to ship through three issuance tracks at once:

1. **BYOL** — customers download a long-lived signed file from the Honua portal and run air-gapped or low-connectivity (issue #338 baseline).
2. **AWS Marketplace** — SaaS Contract / Subscription deployments. AWS Marketplace Entitlement Service exposes `GetEntitlements`, and AWS License Manager exposes asymmetric-signed seller-issued tokens that customers can verify locally with the ISV-owned KMS public key.
3. **Azure Marketplace** — SaaS Fulfillment v2. The lifecycle is split between API-callback flows the publisher backend drives (`Resolve` and `Activate`, server-to-server from the browser landing-page surfaces) and webhook flows Microsoft drives (`ChangePlan`, `ChangeQuantity`, `Reinstate`, `Suspend`, `Unsubscribe`, `Renew`). Microsoft holds subscription state; **there is no portable token** for the customer side. Webhook handlers must respond within 10 seconds.

Without a single contract, every consumer of license state — runtime gates (ADR-0024 § "License Key Enforcement"), admin UI (`honua-server-admin#23`), telemetry, marketplace offerings (#390), and entitlement operations (#645) — bifurcates per cloud. The bifurcation is not optional given Azure's lack of portable tokens: any design that consumed AWS seller-issued tokens directly would still need a separate path for Azure.

The "two tracks, one shape" pattern is industry-standard (HashiCorp Enterprise, Elastic) and the only architecture that keeps the runtime hot path single-pathed across BYOL + AWS + Azure.

## Decision

Honua normalizes all three issuance tracks onto a single ISV-signed file format. Marketplace adapters translate cloud entitlement state into the same internally-issued file the BYOL track ships. Runtime gating, telemetry, and admin surfaces consume one shape regardless of source.

### Canonical License Envelope

- **Format**: UTF-8 JSON envelope with `version`, `keyId`, `payload`, and
  `signature`. `payload` is Base64URL-encoded UTF-8 JSON. `signature` is a
  Base64URL Ed25519 signature over the exact decoded payload bytes.
- **Why this envelope**: it keeps the verifier small, AOT-friendly, and free of
  JSON canonicalization. The signed bytes are stable because Honua verifies the
  payload bytes directly and parses JSON only after signature verification.
- **No JWT library**: `System.IdentityModel.Tokens.Jwt` is reflection-heavy and
  trim-incompatible. The runtime uses `System.Text.Json` source generation for
  the envelope and payload and does not depend on JWT/JWS parsing.

Envelope:

```json
{
  "version": 1,
  "keyId": "honua-2026-q2",
  "payload": "<base64url-encoded UTF-8 JSON payload bytes>",
  "signature": "<base64url Ed25519 signature over the payload bytes>"
}
```

Decoded payload fields use camel-case JSON:

| Field | Type | Notes |
|-------|------|------|
| `schema` | string | Required. Current value is `"honua.license/v1"`. |
| `licenseId` | string | Required stable issuance identifier. |
| `licensedTo` | string | Required operator/licensee display name. |
| `edition` | string | Required. Maps to `HonuaEdition`; `Professional` maps to `Pro`. |
| `issuedAt` | RFC 3339 timestamp | Required issue time. |
| `expiresAt` | RFC 3339 timestamp \| null | Optional. If present, must be in the future. |
| `entitlements` | string[] | Feature keys resolved against `FeatureCatalog`; unknown keys are ignored for activation. |
| `metadata` | object \| null | Optional bounded string metadata for issuance source and support context. |

The runtime bounds the total license file to 64 KiB. Community-tier catalog
entries are always active. Paid features are active only when their catalog key
appears in the signed `entitlements` array; the `edition` value is the
operator-facing bundle label and does not by itself activate every paid feature.

### Issuance Source and Expiry Policy

| Source | Expiry policy | Refresh policy |
|--------|---------------|----------------|
| `byol-portal` | up to 1 year (typical) | Customer downloads a new file from the portal. No phone-home required. |
| `aws-marketplace` | ≤ 90 days | `AwsEntitlementPollerService` re-mints when `GetEntitlements` state diverges or within `RefreshLeadTime` (default 14 days) of `expiresAt`. |
| `azure-marketplace` | ≤ 90 days | Webhook events and `AzureSubscriptionReconcilerService` trigger re-mint; same 14-day lead time. |

Adapter-issued files keep blast radius bounded if entitlement state changes; BYOL files stay long-lived to preserve air-gapped operation.

### Validator Topology (Hot Path)

- **Validation runs on bootstrap and successful admin upload** in the #338
  baseline. Per-call feature gates check an immutable in-memory
  `LicenseSnapshot`; they never re-run signature verification. File-watch hot
  reload and adapter re-mint events remain follow-on triggers that should reuse
  the same validator path.
- **Public-key resolver**: #338 resolves the envelope `keyId` from
  `Licensing:TrustedKeys:<keyId>`. Trusted keys are raw Ed25519 public keys as
  `base64url:<key>`, unprefixed Base64URL, or `base64:<key>`. Baked-in public
  keys, validity windows, and public-key inspection routes are follow-ons.
- **AOT**: Ed25519 verification is isolated behind an internal verifier and
  implemented with BouncyCastle's managed Ed25519 verifier in the #338 runtime.
  JSON uses source-generated `System.Text.Json` contexts. Logging uses
  `[LoggerMessage]`. No JWT library, `System.Reflection`, `dynamic`, or
  `Activator.CreateInstance` is required for the runtime path.
- **Air-gapped operation**: validation has no network dependency. Operators
  provide trusted verification keys through configuration.

### Mint Topology and Trust Model

The mint host lives in `Honua.Server` for v1 behind admin-scoped Minimal API endpoints (`POST /api/v1/admin/license/mint`, `POST /api/v1/admin/license/refresh`, `GET /api/v1/admin/license/signing/status`). Signing material loads only when `License:Signing:Enabled=true`; customer-side deployments leave it `false` and these endpoints return `404`. Honua's hosted mint instance turns it on. The route prefix matches the existing `/api/v1/admin/license/*` operator surface registered in `EndpointRegistry.cs`; the companion design doc § 4.1 enumerates the full route set (operator inspector `GET /api/v1/admin/license/keys`, marketplace reconciler `POST /api/v1/admin/marketplace/{cloud}/reconcile`, Azure webhook `POST /api/v1/marketplace/azure/webhook`, Azure landing page `GET /api/v1/marketplace/azure/landing` (browser GET with `?token`), and Azure activate `POST /api/v1/marketplace/azure/activate`). Future extraction to a dedicated `Honua.LicenseMint.*` deployable is a clean seam — no public-API change.

| Track | Mint flow |
|-------|-----------|
| BYOL | Honua portal → `POST /api/v1/admin/license/mint` (hosted mint) → signed file → customer download. |
| AWS (mint path, default) | Customer's `AwsEntitlementPollerService` sends `(customer_identifier, account_id, product_code, dimensions/observed entitlements)` to the hosted mint. `GetEntitlements` does not return a portable signature, so the mint host **independently re-queries `GetEntitlements` with publisher AWS credentials** as the authoritative source and issues a Honua-signed file with `expiresAt` no more than 90 days out. Adapters never hold the Honua signing key. |
| AWS (ALM path, optional) | When `Aws:Marketplace:UseSellerIssuedLicenses=true`, the adapter fetches the seller-issued ALM token and validates it locally against the ISV-owned KMS public key. Behind feature flag, default `false` in v1 to keep the first AWS landing bounded. |
| Azure | Webhook **durably** persists the event (Redis-backed `MarketplaceWebhookQueue`; on durable-write failure the handler returns 5xx so Azure retries — no ACK on volatile fallback) and enqueues a reconciliation job, ACKs in well under 10 s. The reconciler then implements the SaaS Fulfillment v2 contract — `Get Operation` to validate the payload, `Get Subscription` for the latest entitlement state when needed, POST to the hosted mint, apply through the runtime license store, and `PATCH operation status` **only for the actions Microsoft's [operations API](https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-operations-api) surfaces as pending operations requiring publisher acknowledgement**: `ChangePlan`, `ChangeQuantity`, `Reinstate`. For `ChangePlan` / `ChangeQuantity` initiated from Microsoft Marketplace, Microsoft auto-Successes the operation on a 10-second timeout (per the [lifecycle doc](https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-life-cycle)) — the reconciler-deferred PATCH records the publisher's decision in the operations API audit trail but does **not** act as an inline rejection hook, and a deferred `Failure` does **not** undo a Microsoft-side auto-Success. For `Reinstate`, Microsoft's docs do not specify a timeout-driven auto-Success path; the publisher's PATCH is the authoritative completion signal but is still issued from the reconciler to keep the inline ACK fast. v1 has no rejection use case; if one ever lands, it would require a separate inline pre-flight before the ACK. Notify-only events (`Unsubscribe`, `Suspend`, `Renew`) do not expose a pending operation requiring publisher acknowledgement — the reconciler updates internal mirror state and emits webhook / reconciler telemetry but issues **no** PATCH for them. The customer-side webhook never blocks on any of those calls. |

Adapter ↔ mint authentication is mTLS or M2M bearer against the Honua mint host. Customer Honua Server holds adapter credentials in its configured secret store (env vars, Kubernetes secrets, AWS Secrets Manager, Azure Key Vault).

### Multi-Key Rotation

Rotation is additive. The envelope `keyId` selects from
`Licensing:TrustedKeys`; configuration can add new keys without removing old
ones. Old keys remain valid while they stay configured. Key validity windows,
public-key inspection, and automated retirement remain follow-on work. Rotation
runbooks (referenced below) prove old/new `keyId` coexistence and retirement.

### Cross-Cutting Reuse

- **Logging**: `Honua.Server/Features/Infrastructure/Logging/Log.cs` extends with a license event-id band (`10000-10299` validation/mint, `10300-10499` AWS adapter, `10500-10699` Azure adapter). The 6000-band is already reserved by `Log.cs` Tracing Operations and used by `PerformanceMonitoringLog`, `LayerStyleLog`, and `NlQueryLog`; the 10000-band is unused at landing. All emitters are `[LoggerMessage]` source-generated. Email, subscription IDs, and account IDs are redacted to stable hashes at INFO; raw IDs require explicit DEBUG plus the redaction policy.
- **Telemetry**: #338 emits structured runtime logs for validation, upload, and
  entitlement-denial outcomes. Prometheus counters and licensing ActivitySources
  remain follow-on work: `licenses_issued_total{source}`,
  `licenses_validated_total{result}`, `licenses_active{edition}`,
  `marketplace_metering_records_total{cloud,result}`,
  `marketplace_webhook_events_total{cloud,kind,result}`,
  `marketplace_reconciler_runs_total{cloud,result}`, and
  `marketplace_operation_status_patches_total{cloud,action,result}`. Counter
  shapes are documented in the companion architecture doc § 6.2.
- **Resilience**: marketplace HTTP clients and the adapter-to-mint client reuse `Honua.Core/Features/Infrastructure/Resilience/HttpResiliencePolicies` and `HttpClientResilienceExtensions`.
- **Durable buffer**: metering records and webhook reconciliation reuse the durable substrate already proven in `Honua.Server/Features/Infrastructure/Events/` (`FeatureChangeRetryQueue`, `FeatureChangeEventStore`). Two new queues — `MarketplaceMeteringQueue`, `MarketplaceWebhookQueue` — use the same Redis-backed implementation and lease coordination. The shared in-memory implementation remains available for `MarketplaceMeteringQueue` (eventually consistent, accepts dead-letter loss as a counted outcome) and for dev/test of `MarketplaceWebhookQueue`, but **production deployments with `Azure:Marketplace:Enabled=true` require the Redis-backed `MarketplaceWebhookQueue`** — the webhook handler returns 5xx (so Azure retries) instead of ACKing on a volatile in-memory fallback that would lose events on process exit and break the at-least-once delivery contract Microsoft expects. No new substrate.
- **Caching**: #338 keeps the license snapshot in-process and does not store it
  in Redis or `ICacheService`. Follow-on file-watch, adapter re-mint, and
  public-key inspection work must invalidate or republish any cache entry whose
  output varies by license state.
- **Configuration**: `IOptions<T>` per existing `CacheOptions`/`ResiliencePolicyOptions` precedent. New options validated by `IValidateOptions<T>`.

### Project / Namespace Layout

Following the dependency rule `Honua.Core ← Honua.Postgres / Honua.DuckDB ← Honua.Server`:

- **`Honua.Core/Features/Licensing/`** (no infrastructure deps, AOT-safe, public abstractions documented):
  - `Abstractions/` — #338 adds `ILicenseEntitlementService` and keeps
    `ILicenseManager` / `ILicenseStatusProvider` as the admin/status
    compatibility APIs. Validator, signer, public-key resolver, store, and
    marketplace adapter abstractions remain follow-ons.
  - `Domain/` — #338 defines `LicenseValidationState`, `LicenseSnapshot`,
    `LicenseEntitlementDecision`, `LicenseStatus`, `LicenseInfo`,
    `Entitlement`, `HonuaEdition`, and `FeatureCatalog`.
- **`Honua.Server/Features/Infrastructure/Licensing/`** (consumes Core abstractions, owns runtime infrastructure):
  - `FileBackedLicenseService.cs` — startup load, admin upload, validation, and
    in-memory snapshot publication.
  - `LicenseFileModels.cs` — source-generated JSON context for the runtime
    JSON envelope, decoded payload, and health summary.
  - `BouncyCastleEd25519Verifier.cs` — internal Ed25519 verification adapter.
  - `LicenseGate.cs` — shared HTTP 402 and gRPC `FailedPrecondition`
    entitlement helpers.
  - `LicenseOptions.cs` — `Licensing` configuration (`LicensePath`,
    `TrustedKeys`, `AllowAdminUpload`, `ExpiryWarningDays`).
- **Follow-on server namespaces**:
  - `Honua.Server/Features/Licensing/Mint/` for publisher-only mint endpoints.
  - `Honua.Server/Features/Licensing/Marketplace/{Aws,Azure}/` for marketplace
    adapters and reconciler workers.
- **Tests** under `tests/dotnet/`:
  - `Honua.Core.Tests/Features/Licensing/` — `FeatureCatalog` and domain tests.
  - `Honua.Server.Tests/Features/Licensing/` — #338 loader/gate tests for no
    path, missing file, valid file, malformed/oversized file, unknown key,
    invalid signature, expired file, upload size guard, HTTP 402, and gRPC
    `FailedPrecondition`.
  - Follow-on mint, marketplace, file-watch, key-inspection, and rotation smoke
    tests land with their bounded child tickets.

Implementation classes in `Honua.Server` are `internal sealed`; only abstractions in `Honua.Core` are `public` (with XML docs), per AGENTS.md. Endpoints stay ≤ 5 dependencies, handlers ≤ 4. No controllers — Minimal APIs only (ADR-0013).

### Migration

The ticket #338 runtime baseline replaces the earlier placeholder manager/provider with a file-backed Ed25519 validator and in-memory entitlement snapshot. "Migration" here means: keep any legacy private-preview file path bounded while the BYOL portal issues files matching the runtime envelope.

If the BYOL portal in a separate repo has shipped a private-preview file format, the dual-format verifier path documented in the migration runbook accepts both formats during a 6-month grace window (configurable). The verifier picks the canonical format if both parse; otherwise emits a deprecation warning and accepts legacy. After the grace window the legacy branch is removed in a single PR.

### Bounded Child Tickets

This contract is a coordination ticket whose code deliverables exceed a single PR's budget. The decomposition below keeps each child reviewable in isolation:

1. License format ADR + design doc.
2. Runtime loader + JSON context — ticket #338 lands the signed JSON envelope,
   startup load, admin upload, status, health visibility, and entitlement gates.
3. File watch/hot reload and license-cache invalidation.
4. Mint library — `Honua.Core/Features/Licensing/Mint/` + `Configuration/LicenseSigningOptions`. AOT-safe. Disabled by default.
5. Mint host endpoints — `Honua.Server/Features/Licensing/Mint/`. Admin-scoped, M2M-authenticated.
6. AWS marketplace adapter (entitlements + mint path) — `Honua.Server/Features/Licensing/Marketplace/Aws/`. ALM seller-issued path deferred.
7. AWS marketplace adapter (ALM seller-issued path) — feature-flagged. Optional.
8. Azure marketplace adapter — `Honua.Server/Features/Licensing/Marketplace/Azure/`. Webhook 10s SLA test included.
9. Migration tooling + runbook — dual-format verifier (only if the portal shipped a legacy format), grace-window flag, deprecation telemetry.
10. Key-rotation runbook + smoke test.
11. Per-marketplace operations runbook.

Cross-repo follow-ups (NOT this ticket): BYOL portal integration to call the mint API (separate repo); admin UI marketplace surfaces (`honua-server-admin#23`).

## Consequences

### Positive

- **One runtime gating path.** Feature gates check `LicenseInfo` from a single in-memory snapshot regardless of source. Telemetry counters and admin UI consume one schema.
- **Air-gapped BYOL preserved.** No network dependency on the validator path.
- **Multi-key rotation without synchronous fleet update.** Envelope `keyId`
  lookup against additive `Licensing:TrustedKeys` lets old and new keys coexist
  during rotation. Validity windows and inspection routes remain follow-ons.
- **Bounded blast radius for marketplace state changes.** Adapter-issued files cap at 90 days; refresh lead time absorbs transient mint-host outages.
- **AOT and trim friendly.** Source-gen JSON, source-gen logging, no reflection, no JWT library dependency.
- **Reuses existing runtime substrate.** The #338 path is local-file and
  in-memory only; follow-on marketplace and mint work should reuse the durable
  queue, resilience, caching, and logging substrate rather than inventing new
  cross-cutting infrastructure.

### Negative

- **Mint host availability is a soft dependency for adapters.** Mitigated by long-lived adapter files (≤ 90 days) and a wide refresh lead time (default 14 days), but adapters cannot re-mint while the hosted mint is unreachable. Mint-host SLO is monitored independently.
- **Long-lived BYOL files (1 year) magnify compromised-key blast radius.** Rotation runbook is in scope; the optional revocation / kill-switch channel is **out of scope** for this ticket and tracked as a known follow-up.
- **AWS ALM seller-issued track adds a second token format if enabled.** Two parsing paths, two test matrices. Deferred to a follow-up; v1 ships mint-path only so all flows share one parser.
- **Marketplace SKU → edition mapping owned by sales/legal.** Out of scope here, blocking for #390. Adapters consume a config-driven mapping table (`Marketplace:SkuMap`) so operations can update without code change.
- **Per-customer multi-tenancy is not in v1.** `tenant_id` is in the claim schema from day one and ignored when absent — adding tenant gating later does not require a re-issue.

### Supersedes

- Refines ADR-0024 § "License Key Enforcement". The "self-contained signed JWT
  or similar" placeholder is now the signed JSON / Ed25519 envelope defined
  here.

## References

- ADR-0011: Testing Strategy and API Surface Coverage
- ADR-0013: Minimal APIs vs Controllers
- ADR-0014: Dependency Injection Limits
- ADR-0015: Vertical Slice Architecture
- ADR-0017: Redis Caching with Fallback
- ADR-0018: Source-Generated JSON Serialization for AOT Compatibility
- ADR-0021: Redis Usage and HybridCache Deferral
- ADR-0024: Open-Core Edition Model
- ADR-0031: Durable Job Orchestration Substrate
- RFC 8037 / 8032: CFRG Curves / EdDSA / Ed25519
- AWS License Manager: https://docs.aws.amazon.com/license-manager/latest/userguide/license-manager.html
- AWS License Manager seller-issued licenses: https://docs.aws.amazon.com/license-manager/latest/userguide/seller-issued-licenses.html
- AWS Marketplace Entitlement Service (`GetEntitlements`)
- AWS Marketplace Metering Service (`MeterUsage` / `BatchMeterUsage` / `RegisterUsage`)
- Azure Marketplace SaaS Fulfillment API v2: https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-life-cycle
- Azure Marketplace Metered Billing API
- HashiCorp Enterprise / Elastic license model (industry reference for the two-tracks-one-shape pattern)
- Companion design doc: `docs/contributor/architecture/unified-license-and-entitlement.md`
- Migration runbook: `docs/operator/runbooks/LICENSE_MIGRATION.md`
- Key rotation runbook: `docs/operator/runbooks/LICENSE_KEY_ROTATION.md`
- Marketplace operations runbook: `docs/operator/runbooks/MARKETPLACE_OPERATIONS.md`
- Issue #338: existing Ed25519 license infrastructure baseline
- Issue #390: Honua Cloud — AWS & Azure Marketplace SaaS offerings (consumer)
- Issue #645: Commercial entitlement operations — issuance, activation, marketplace sync, metering (consumer)
- Issue #804: this ADR
- `honua-io/honua-server-admin#23`: license + edition workspace (consumer; resumes scoped to BYOL-only after this ADR lands)
