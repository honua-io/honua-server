# ADR-0033: Unified License Format and Entitlement Architecture

## Status

Accepted

## Context

ADR-0024 established the open-core edition model and committed to offline-capable license enforcement. The licensing slice in `Honua.Core/Features/Licensing/` (`ILicenseManager`, `ILicenseStatusProvider`, `LicenseInfo`, `Entitlement`, `FeatureCatalog`, `HonuaEdition`) reserved the runtime contract but ships no validator, no signer, no signed-file format, and no JSON serialization context. The `Honua.Server/Features/Admin/Services/InMemoryLicenseManager` and `ConfigurationLicenseStatusProvider` are placeholders flagged for replacement when issue #338 lands.

Honua now needs to ship through three issuance tracks at once:

1. **BYOL** — customers download a long-lived signed file from the Honua portal and run air-gapped or low-connectivity (issue #338 baseline).
2. **AWS Marketplace** — SaaS Contract / Subscription deployments. AWS Marketplace Entitlement Service exposes `GetEntitlements`, and AWS License Manager exposes asymmetric-signed seller-issued tokens that customers can verify locally with the ISV-owned KMS public key.
3. **Azure Marketplace** — SaaS Fulfillment v2. The lifecycle is API-callback + webhook only (Resolve, Activate, Suspended, Reinstated, Unsubscribed, plan-change, quantity-change). Microsoft holds subscription state; **there is no portable token** for the customer side. Webhook handlers must respond within 10 seconds.

Without a single contract, every consumer of license state — runtime gates (ADR-0024 § "License Key Enforcement"), admin UI (`honua-server-admin#23`), telemetry, marketplace offerings (#390), and entitlement operations (#645) — bifurcates per cloud. The bifurcation is not optional given Azure's lack of portable tokens: any design that consumed AWS seller-issued tokens directly would still need a separate path for Azure.

The "two tracks, one shape" pattern is industry-standard (HashiCorp Enterprise, Elastic) and the only architecture that keeps the runtime hot path single-pathed across BYOL + AWS + Azure.

## Decision

Honua normalizes all three issuance tracks onto a single ISV-signed file format. Marketplace adapters translate cloud entitlement state into the same internally-issued file the BYOL track ships. Runtime gating, telemetry, and admin surfaces consume one shape regardless of source.

### Canonical License Envelope

- **Format**: compact JWS (RFC 7515) with `alg=EdDSA`, `typ=JWT`, `kid=<key-id>`. Ed25519 signature (RFC 8037 / 8032).
- **Why JWS over a custom envelope**: RFC-grade interop; `kid` natively supports multi-key rotation (RFC 7517); aligns with the project standard "prefer canonical client behavior that best preserves interoperability".
- **No JWT library**: `System.IdentityModel.Tokens.Jwt` is reflection-heavy and trim-incompatible. Honua implements a minimal compact-JWS parser/builder (~150 LOC) inside the validator. JSON bodies flow through a new `LicenseDomainJsonContext` (`System.Text.Json` source generator), consistent with ADR-0018.

#### Canonical claims (snake_case in the JWS payload, source-generated JSON)

| Claim | Type | Notes |
|-------|------|------|
| `iss` | string | Always `"honua.io"`. |
| `sub` | string | `license:<license-id>`. |
| `iat`, `nbf`, `exp` | int (Unix seconds) | Issued / not-before / expiry. |
| `edition` | string | Maps to `HonuaEdition` (`community`, `pro`, `enterprise`). |
| `entitlements` | string[] | Feature keys resolved against `FeatureCatalog`. |
| `issued_to` | object | `{ name, email, org }`. |
| `issuance_source` | string | `byol-portal` \| `aws-marketplace` \| `azure-marketplace`. |
| `marketplace` | object \| null | `{ subscription_id, account_id, product_code, plan_id }`. Required when `issuance_source != byol-portal`. |
| `license_id` | string (UUID v7) | Stable per issuance. |
| `tenant_id` | string (UUID) \| null | Reserved for future multi-tenancy without forcing a re-issue. |

Payload size is bounded at ≤ 8 KiB (defensive limit for storage, webhook bodies, and log redaction).

### Issuance Source and Expiry Policy

| Source | Expiry policy | Refresh policy |
|--------|---------------|----------------|
| `byol-portal` | up to 1 year (typical) | Customer downloads a new file from the portal. No phone-home required. |
| `aws-marketplace` | ≤ 90 days | `AwsEntitlementPollerService` re-mints when `GetEntitlements` state diverges or within `RefreshLeadTime` (default 14 days) of `exp`. |
| `azure-marketplace` | ≤ 90 days | Webhook events and `AzureSubscriptionReconcilerService` trigger re-mint; same 14-day lead time. |

Adapter-issued files keep blast radius bounded if entitlement state changes; BYOL files stay long-lived to preserve air-gapped operation.

### Validator Topology (Hot Path)

- **Validation runs once on bootstrap and on hot reload** (file change, admin upload, adapter re-mint event). The result is cached via `ICacheService` keyed by `license_id + iat + kid`. Per-call feature gates check the in-memory `LicenseInfo` snapshot; they never re-run signature verification. The validator is `sealed` and stateless.
- **Public-key resolver**: a baked-in primary key (assembly resource, versioned per release) plus a config-driven additive list. Lookup is by `kid`. Each key carries `not_before` / `not_after` so retired keys can be refused without removal. Configuration cannot delete the baked-in primary key; that requires a release.
- **AOT**: Ed25519 via `NSec.Cryptography` (libsodium native binding, AOT-validated, no reflection); JSON via `LicenseDomainJsonContext`. Logging via `[LoggerMessage]`. No `System.Reflection`, no `dynamic`, no `Activator.CreateInstance`. `JsonSerializerIsReflectionEnabledByDefault=false` and `PublishAot=true` already enforced in `Honua.Server.csproj`. If NSec fails AOT smoke on a target RID, Honua falls back to the BouncyCastle managed Ed25519 for that RID; both implementations are exercised by the validator's golden-vector tests.
- **Air-gapped operation**: validation has no network dependency. The baked-in primary key satisfies the offline path; rotation drops new keys via configuration only.

### Mint Topology and Trust Model

The mint host lives in `Honua.Server` for v1 behind admin-scoped Minimal API endpoints (`POST /api/v1/admin/license/mint`, `POST /api/v1/admin/license/refresh`, `GET /api/v1/admin/license/signing/status`). Signing material loads only when `License:Signing:Enabled=true`; customer-side deployments leave it `false` and these endpoints return `404`. Honua's hosted mint instance turns it on. The route prefix matches the existing `/api/v1/admin/license/*` operator surface registered in `EndpointRegistry.cs`; the companion design doc § 4.1 enumerates the full route set (operator inspector `GET /api/v1/admin/license/keys`, marketplace reconciler `POST /api/v1/admin/marketplace/{cloud}/reconcile`, Azure webhook `POST /api/v1/marketplace/azure/webhook`, Azure landing page `GET /api/v1/marketplace/azure/landing` (browser GET with `?token`), and Azure activate `POST /api/v1/marketplace/azure/activate`). Future extraction to a dedicated `Honua.LicenseMint.*` deployable is a clean seam — no public-API change.

| Track | Mint flow |
|-------|-----------|
| BYOL | Honua portal → `POST /api/v1/admin/license/mint` (hosted mint) → signed file → customer download. |
| AWS (mint path, default) | Customer's `AwsEntitlementPollerService` sends `(customer_identifier, account_id, product_code, dimensions/observed entitlements)` to the hosted mint. `GetEntitlements` does not return a portable signature, so the mint host **independently re-queries `GetEntitlements` with publisher AWS credentials** as the authoritative source and issues a Honua-signed file with `exp ≤ 90d`. Adapters never hold the Honua signing key. |
| AWS (ALM path, optional) | When `Aws:UseSellerIssuedLicenses=true`, the adapter fetches the seller-issued ALM token and validates it locally against the ISV-owned KMS public key. Behind feature flag, default `false` in v1 to keep the first AWS landing bounded. |
| Azure | Webhook **durably** persists the event (Redis-backed `MarketplaceWebhookQueue`; on durable-write failure the handler returns 5xx so Azure retries — no ACK on volatile fallback) and enqueues a reconciliation job, ACKs in well under 10 s. The reconciler then implements the SaaS Fulfillment v2 contract — `Get Operation` to validate the payload, `Get Subscription` for the latest entitlement state when needed, POST to the hosted mint, apply via `FileLicenseStore`, and `PATCH operation status` **only for the two-phase actions Microsoft surfaces as pending operations** (`ChangePlan`, `ChangeQuantity`). For those two actions, Microsoft auto-completes the customer's requested change on its side independent of the PATCH window — the reconciler-deferred PATCH records the publisher's decision in the operations API audit trail but does **not** act as an inline rejection hook, and a deferred `Failure` does **not** undo a Microsoft-side auto-success. v1 has no rejection use case; if one ever lands, it would require a separate inline pre-flight before the ACK. Single-phase notifications (`Unsubscribe`, `Suspend`, `Reinstate`, `Renew`, `Transfer`) do not expose a pending operation requiring publisher acknowledgement — the reconciler updates internal mirror state and emits webhook / reconciler telemetry but issues **no** PATCH for them. The customer-side webhook never blocks on any of those calls. |

Adapter ↔ mint authentication is mTLS or M2M bearer against the Honua mint host. Customer Honua Server holds adapter credentials in its configured secret store (env vars, Kubernetes secrets, AWS Secrets Manager, Azure Key Vault).

### Multi-Key Rotation

Rotation is additive. The `kid` claim selects from the resolved key set; configuration can add new keys without removing old ones. Old keys remain valid until their `not_after` elapses. Rotation runbooks (referenced below) prove this with a smoke test that exercises old/new `kid` coexistence and retirement.

### Cross-Cutting Reuse

- **Logging**: `Honua.Server/Features/Infrastructure/Logging/Log.cs` extends with a license event-id band (`10000-10299` validation/mint, `10300-10499` AWS adapter, `10500-10699` Azure adapter). The 6000-band is already reserved by `Log.cs` Tracing Operations and used by `PerformanceMonitoringLog`, `LayerStyleLog`, and `NlQueryLog`; the 10000-band is unused at landing. All emitters are `[LoggerMessage]` source-generated. Email, subscription IDs, and account IDs are redacted to stable hashes at INFO; raw IDs require explicit DEBUG plus the redaction policy.
- **Telemetry**: new `ActivitySource`s `Honua.Licensing.Validator`, `Honua.Licensing.Mint`, `Honua.Licensing.Aws`, `Honua.Licensing.Azure`. Counters via `Meter`: `licenses_issued_total{source}`, `licenses_validated_total{result}`, `licenses_active{edition}`, `marketplace_metering_records_total{cloud,result}`, `marketplace_webhook_events_total{cloud,kind,result}`, `marketplace_reconciler_runs_total{cloud,result}`.
- **Resilience**: marketplace HTTP clients and the adapter-to-mint client reuse `Honua.Core/Features/Infrastructure/Resilience/HttpResiliencePolicies` and `HttpClientResilienceExtensions`.
- **Durable buffer**: metering records and webhook reconciliation reuse the durable substrate already proven in `Honua.Server/Features/Infrastructure/Events/` (`FeatureChangeRetryQueue`, `FeatureChangeEventStore`). Two new queues — `MarketplaceMeteringQueue`, `MarketplaceWebhookQueue` — use the same Redis-backed implementation and lease coordination. The shared in-memory implementation remains available for `MarketplaceMeteringQueue` (eventually consistent, accepts dead-letter loss as a counted outcome) and for dev/test of `MarketplaceWebhookQueue`, but **production deployments with `Azure:Marketplace:Enabled=true` require the Redis-backed `MarketplaceWebhookQueue`** — the webhook handler returns 5xx (so Azure retries) instead of ACKing on a volatile in-memory fallback that would lose events on process exit and break the at-least-once delivery contract Microsoft expects. No new substrate.
- **Caching**: `ICacheService.RemoveByPatternAsync` invalidates: license snapshot on file change / hot upload / adapter re-mint event; marketplace subscription state on webhook; public-key set on rotation config change.
- **Configuration**: `IOptions<T>` per existing `CacheOptions`/`ResiliencePolicyOptions` precedent. New options validated by `IValidateOptions<T>`.

### Project / Namespace Layout

Following the dependency rule `Honua.Core ← Honua.Postgres / Honua.DuckDB ← Honua.Server`:

- **`Honua.Core/Features/Licensing/`** (no infrastructure deps, AOT-safe, public abstractions documented):
  - `Abstractions/` — extends with `ILicenseValidator`, `ILicenseSigner`, `ILicensePublicKeyResolver`, `ILicenseStore`, `IMarketplaceLicenseAdapter`. The existing `ILicenseManager` and `ILicenseStatusProvider` stay.
  - `Domain/` — adds `LicenseClaims` (canonical JWS payload), `LicenseEnvelope` (parsed `header / payload / signature`), `IssuanceSource`, `LicenseValidationResult`. `LicenseInfo`, `Entitlement`, `LicenseStatus`, `HonuaEdition`, and `FeatureCatalog` already exist.
  - `Validation/` — `Ed25519LicenseValidator`, `JwsParser`, `ClockSkewPolicy`. Pure compute, no I/O, no reflection.
  - `Mint/` — `Ed25519LicenseSigner`, `LicenseMintService`. Publisher-only.
  - `Serialization/LicenseDomainJsonContext.cs` — `JsonSerializerContext` with `SnakeCaseLower` naming.
  - `Configuration/` — `LicenseOptions`, `AwsMarketplaceOptions`, `AzureMarketplaceOptions`, `LicenseSigningOptions`.
- **`Honua.Server/Features/Licensing/`** (consumes Core abstractions, owns infrastructure + endpoints):
  - `Storage/FileLicenseStore.cs` — file-based load/save with hot reload; uses `ICacheService`.
  - `Mint/LicenseMintEndpoints.cs` — admin-scoped Minimal APIs.
  - `Marketplace/Aws/` — `AwsEntitlementsClient`, `AwsLicenseManagerProxy` (optional), `AwsRegisterUsageOnStart`, `AwsMeteringWorker`, `AwsEntitlementPollerService`.
  - `Marketplace/Azure/` — `AzureFulfillmentClient`, `AzureLandingPageEndpoints`, `AzureWebhookEndpoint`, `AzureMeteringWorker`, `AzureSubscriptionReconcilerService`.
  - `BackgroundServices/LicenseExpiryRefreshService.cs`.
- **Tests** under `tests/dotnet/`:
  - `Honua.Core.Tests/Features/Licensing/` — known-good and known-bad JWS test vectors, `kid`-rotation tests, clock-skew tests, claim deserialization.
  - `Honua.Server.Tests/Features/Licensing/` — endpoint integration tests (admin mint, Azure webhook with stub fulfillment, AWS poller with stub entitlements client), durable buffer fault-injection.
  - `Honua.Architecture.Tests` — assert no `Honua.Server` symbols leak into `Honua.Core/Features/Licensing/`; assert the validator class is `sealed` and stateless; assert public types have XML docs.

Implementation classes in `Honua.Server` are `internal sealed`; only abstractions in `Honua.Core` are `public` (with XML docs), per AGENTS.md. Endpoints stay ≤ 5 dependencies, handlers ≤ 4. No controllers — Minimal APIs only (ADR-0013).

### Migration

The existing `Honua.Core/Features/Licensing/` slice exposes only abstractions and domain types; `InMemoryLicenseManager` and `ConfigurationLicenseStatusProvider` are placeholders flagged for #338. **No license file format has shipped from this repo yet.** "Migration" here means: design the format such that the BYOL portal's first issued file already conforms.

If the BYOL portal in a separate repo has shipped a private-preview file format, the dual-format verifier path documented in the migration runbook accepts both formats during a 6-month grace window (configurable). The verifier picks the canonical format if both parse; otherwise emits a deprecation warning and accepts legacy. After the grace window the legacy branch is removed in a single PR.

### Bounded Child Tickets

This contract is a coordination ticket whose code deliverables exceed a single PR's budget. The decomposition below keeps each child reviewable in isolation:

1. License format ADR + design doc (this PR).
2. Validator + JSON context — `Honua.Core/Features/Licensing/{Validation,Serialization,Domain/{LicenseClaims,LicenseEnvelope,LicenseValidationResult},Abstractions/{ILicenseValidator,ILicensePublicKeyResolver}}`. Pure unit tests.
3. License store + bootstrap + hot reload — `Honua.Server/Features/Licensing/Storage/`. Replaces `InMemoryLicenseManager` against the new validator.
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
- **Multi-key rotation without synchronous fleet update.** `kid` lookup against an additive key set; old keys stay valid until `not_after`.
- **Bounded blast radius for marketplace state changes.** Adapter-issued files cap at 90 days; refresh lead time absorbs transient mint-host outages.
- **AOT and trim friendly.** Source-gen JSON, source-gen logging, no reflection, no JWT library dependency.
- **Reuses durable queue, resilience, caching, and logging substrate.** No new cross-cutting infrastructure invented.

### Negative

- **Mint host availability is a soft dependency for adapters.** Mitigated by long-lived adapter files (≤ 90 days) and a wide refresh lead time (default 14 days), but adapters cannot re-mint while the hosted mint is unreachable. Mint-host SLO is monitored independently.
- **Long-lived BYOL files (1 year) magnify compromised-key blast radius.** Rotation runbook is in scope; the optional revocation / kill-switch channel is **out of scope** for this ticket and tracked as a known follow-up.
- **AWS ALM seller-issued track adds a second token format if enabled.** Two parsing paths, two test matrices. Deferred to a follow-up; v1 ships mint-path only so all flows share one parser.
- **Marketplace SKU → edition mapping owned by sales/legal.** Out of scope here, blocking for #390. Adapters consume a config-driven mapping table (`Marketplace:SkuMap`) so operations can update without code change.
- **Per-customer multi-tenancy is not in v1.** `tenant_id` is in the claim schema from day one and ignored when absent — adding tenant gating later does not require a re-issue.

### Supersedes

- Refines ADR-0024 § "License Key Enforcement". The "self-contained signed JWT or similar" placeholder is now the JWS / EdDSA / Ed25519 envelope defined here.

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
- RFC 7515: JSON Web Signature
- RFC 7517: JSON Web Key (`kid`)
- RFC 7519: JSON Web Token (`iat`/`nbf`/`exp`)
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
