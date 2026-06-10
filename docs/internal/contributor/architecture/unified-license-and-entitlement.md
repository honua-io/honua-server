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

All three tracks normalize onto the same Honua-signed runtime file shape before
customer instances validate or gate features. Ticket #338 implements the first
runtime shape as a JSON envelope `{ version, keyId, payload, signature }`; the
signature covers exact decoded payload bytes, and the runtime trusts only keys
configured under `Licensing:TrustedKeys:<keyId>`.

Mint-host, marketplace, public-key inspection, file-watch hot reload, baked-in
key, and Prometheus counter details below are follow-on architecture unless a
section explicitly says it is part of the #338 runtime baseline.

---

## 1. Canonical License Envelope

### 1.1 Wire format

The ticket #338 license file is UTF-8 JSON:

```json
{
  "version": 1,
  "keyId": "honua-2026-q2",
  "payload": "<base64url-encoded UTF-8 JSON payload bytes>",
  "signature": "<base64url Ed25519 signature over the payload bytes>"
}
```

The file is persisted verbatim to disk or to the configured secret store. The
`.honua-license.json` extension is conventional; consumers must not depend on
the extension. The runtime reads at most 64 KiB.

### 1.2 Envelope fields

| Field | Value |
|-------|-------|
| `version` | Required integer. Current value is `1`. |
| `keyId` | Required key identifier resolved from `Licensing:TrustedKeys:<keyId>`. |
| `payload` | Required Base64URL-encoded UTF-8 JSON payload bytes. |
| `signature` | Required Base64URL Ed25519 signature over the decoded payload bytes. |

Trusted public keys are raw 32-byte Ed25519 public keys configured as
`base64url:<key>`, unprefixed Base64URL, or `base64:<key>`.

### 1.3 Payload fields

The decoded payload uses camel-case JSON:

```json
{
  "schema": "honua.license/v1",
  "licenseId": "lic_123",
  "licensedTo": "Example Operator",
  "edition": "Pro",
  "issuedAt": "2026-05-06T00:00:00Z",
  "expiresAt": "2027-05-06T00:00:00Z",
  "entitlements": [
    "analytics.clustering",
    "staticmap.high-dpi"
  ],
  "metadata": {
    "source": "byol"
  }
}
```

| Field | Type | Required | Notes |
|-------|------|---------|-------|
| `schema` | string | yes | Current value is `"honua.license/v1"`. |
| `licenseId` | string | yes | Stable per issuance. |
| `licensedTo` | string | yes | Operator/licensee display name. |
| `edition` | string | yes | `Community`, `Pro`, `Enterprise`, or `Professional` (`Professional` maps to `Pro`). |
| `issuedAt` | RFC 3339 timestamp | yes | Issue timestamp. |
| `expiresAt` | RFC 3339 timestamp \| null | no | If present, must be in the future. |
| `entitlements` | string[] | no | Feature keys resolved against `FeatureCatalog`; unknown keys are ignored for activation. |
| `metadata` | object \| null | no | Optional string-valued metadata map for source/support context. |

Community-tier catalog entries are always active. Paid features are active only
when their catalog key appears in the signed `entitlements` array. The
operator-facing `edition` label does not activate every paid feature in that
edition by itself.

### 1.4 Expiry policy by source

| Source | Maximum validity | Refresh trigger |
|-------------------|---------------------|-----------------|
| `byol-portal` | 366 days | Customer downloads a new file from the portal. |
| `aws-marketplace` | 90 days | `AwsEntitlementPollerService` re-mints when `GetEntitlements` state diverges or within `RefreshLeadTime` (default 14 days) of `expiresAt`. |
| `azure-marketplace` | 90 days | Webhook event or `AzureSubscriptionReconcilerService` triggers re-mint; same lead time. |

The mint service rejects requests that violate the per-source expiry cap.

### 1.5 Marketplace metadata shape

Marketplace adapters are follow-on work. The #338 `metadata` field is a flat
string-valued map for simple source/support fields. Rich marketplace details
should land as a future typed marketplace object without changing feature-gate
semantics.

For AWS Marketplace:

```
"marketplace": {
  "subscription_id": "<AWS Marketplace customer identifier>",
  "account_id": "<12-digit AWS account ID>",
  "product_code": "<20-char Marketplace product code>",
  "plan_id": "<dimension or contract identifier>"
}
```

For Azure Marketplace:

```
"marketplace": {
  "subscription_id": "<Azure SaaS subscription ID, GUID>",
  "account_id": "<purchaser tenant ID, GUID>",
  "product_code": "<offer ID>",
  "plan_id": "<plan ID>"
}
```

Validators do not interpret marketplace metadata for gating decisions; the data
exists for reconciliation, telemetry, and admin visibility.

### 1.6 Size and serialization rules

- Total license file size is bounded at 64 KiB. Oversized files publish a
  `Malformed` validation state and do not replace the active snapshot.
- Source-generated JSON via `LicenseFileJsonContext`
  (`System.Text.Json.Serialization.JsonSerializerContext`) uses camel-case
  names and omits nulls.
- The signing input is the exact decoded payload bytes. The validator never
  re-serializes the payload before verification.

---

## 2. Validator Topology

### 2.1 Single hot-path entry

```
ILicenseManager.GetLicenseInfoAsync()
ILicenseStatusProvider.GetCurrentStatus()
ILicenseEntitlementService.GetSnapshot()
  └── FileBackedLicenseService in-memory LicenseSnapshot
        └── startup load or admin upload
              └── validate JSON envelope
                    ├── Base64URL decode payload and signature
                    ├── resolve keyId from Licensing:TrustedKeys
                    ├── BouncyCastle Ed25519 verify(payloadBytes, signature)
                    └── parse signed payload into LicenseSnapshot
```

`ILicenseEntitlementService` is the fast runtime gating contract.
`ILicenseManager` and `ILicenseStatusProvider` expose the same snapshot to the
admin/status compatibility surfaces.

### 2.2 Per-request gates

Per-request feature gates check the immutable in-memory `LicenseSnapshot`
through `ILicenseEntitlementService`. They never re-run signature verification.
Validation runs once on bootstrap and after successful admin upload. File-watch
hot reload and adapter re-mint events are follow-on triggers that should reuse
the same validation and snapshot-publication path.

### 2.3 Validation result codes

The #338 runtime publishes these `LicenseValidationState` values:

| Result | Meaning | Operator action |
|--------|---------|-----------------|
| `NoLicenseConfigured` | `Licensing:LicensePath` is empty/unset. Community mode is active and `isValid=true`. | None. |
| `Valid` | Signature, payload, expiry, and trusted key are all OK. | None. |
| `MissingFile` | A configured path does not exist. | Mount/provision the file or clear `LicensePath`. |
| `Malformed` | Envelope JSON, payload JSON, Base64URL, configured public key, required payload fields, or file size failed validation. | Inspect the file and key config; reissue if needed. |
| `UnknownKey` | Envelope `keyId` is not present in `Licensing:TrustedKeys`. | Add the trusted public key and restart, or reissue with a trusted key. |
| `InvalidSignature` | Ed25519 verification failed. | Treat as tampering or key mismatch; reissue and investigate. |
| `Expired` | `expiresAt` is in the past. | Reissue. Adapter refresh is follow-on work. |

The validator returns the result rather than throwing. Startup and admin status
surfaces publish safe validation state, admin upload returns a sanitized
`ApiResponse` rejection, and paid-feature gates use the shared protocol error
helpers (no raw exception bodies).

### 2.4 AOT / trimming

- **Crypto**: BouncyCastle managed Ed25519 verification behind internal
  `IEd25519Verifier`; the runtime does not hand-roll Ed25519.
- **JSON**: `LicenseFileJsonContext` source-generated. The validator does not
  call `JsonSerializer.Deserialize<T>()` against runtime metadata.
- **Logging**: `[LoggerMessage]`-generated emitters; no `string.Format` in
  hot paths.
- **No reflection**: `JsonSerializerIsReflectionEnabledByDefault=false` and
  `PublishAot=true` are already enforced in `Honua.Server.csproj`.

### 2.5 Performance budget

- Validator self-time is not on the request path. Validation runs only on
  bootstrap and successful admin upload, so per-request overhead is dominated
  by a snapshot read and O(1) entitlement set lookup.
- File read is bounded to 64 KiB; malformed or oversized files do not replace
  the active snapshot.
- Webhook ack budget: < 1 s p99 on test environments (well inside the Azure
  SaaS Fulfillment v2 10 s SLA).

---

## 3. Multi-Key Rotation

### 3.1 Resolver contract

Ticket #338 resolves keys directly from `Licensing:TrustedKeys:<keyId>` on
`LicenseOptions`. Each configured value must decode to a 32-byte raw Ed25519
public key. There is no baked-in verification key, key validity window,
`IOptionsMonitor` hot reload, or public-key inspection route in the baseline.

### 3.2 Key composition rules

- Adding a key is additive: configure another `Licensing:TrustedKeys:<keyId>`
  entry on every instance and restart.
- Removing a key retires every license whose envelope references that `keyId`;
  those files publish `UnknownKey` after restart or upload.
- Key id collisions are configuration errors operationally even though the
  dictionary can only hold one value per id. Rotation runbooks require unique
  ids such as `honua-2026-q3`.
- Baked-in keys, validity windows, public-key inspection, and live key reload
  are follow-on work.

### 3.3 Rotation flow (runbook reference)

The `LICENSE_KEY_ROTATION` runbook walks rotations end-to-end:

1. Generate a new keypair on the offline signing host.
2. Add the public key to `Licensing:TrustedKeys` configuration on every
   instance and restart.
3. Switch the mint host to sign with the new private key.
4. Re-issue BYOL files on the next portal cadence; adapter-issued files
   re-mint automatically within `RefreshLeadTime`.
5. Remove the retired key from `Licensing:TrustedKeys` once the longest-lived
   in-flight file has expired or been replaced.

The runbook smoke test exercises a key rotation cycle and verifies that
licenses signed by the old `keyId` remain valid through retirement.

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
| `GET /api/v1/admin/license/keys` | Admin | Yes. | Follow-on public-key inspection for the resolved `Licensing:TrustedKeys` set. |
| `POST /api/v1/admin/marketplace/{cloud}/reconcile` | Admin | Yes (when adapter enabled). | Manual reconciliation trigger; bypasses the timer. `cloud` ∈ `aws`, `azure`. |
| `POST /api/v1/marketplace/azure/webhook` | Public — Azure AD JWT bearer (publisher audience). | Yes (when Azure adapter enabled). | Azure SaaS Fulfillment v2 lifecycle webhook. Not admin-scoped. |
| `GET /api/v1/marketplace/azure/landing` | Public — browser GET. Microsoft redirects the purchaser to the configured landing page URL with `?token=<marketplace-token>`. The handler exchanges that token for subscription metadata via Microsoft's Resolve API (server-to-server, `x-ms-marketplace-token` header) and renders the activation page. | Yes (when Azure adapter enabled). | Browser-facing landing page. Not admin-scoped. |
| `POST /api/v1/marketplace/azure/activate` | Public — backend POST from the landing-page form once the purchaser confirms. The handler calls Microsoft's Activate API server-to-server. | Yes (when Azure adapter enabled). | Landing-page activate. Not admin-scoped. |

Signing material loads only when `License:Signing:Enabled=true`. Customer-
side deployments leave it `false` and the mint-host-only endpoints return
`404` to keep the signing surface invisible. The `GET /api/v1/admin/license/keys`
inspector is a follow-on route; #338 operators verify trusted keys through
effective deployment configuration.

Marketplace endpoints register only when the corresponding
`{Aws,Azure}:Marketplace:Enabled=true`, so air-gapped customers see no
Azure landing page or AWS reconcile route in the registry.

The Azure landing-page URL configured in the publisher's marketplace
offer must point at `GET /api/v1/marketplace/azure/landing` on a
public-facing customer host. Microsoft drives the purchaser's browser
to that URL with the marketplace token in the `?token=` query
parameter; the handler then calls Microsoft's Resolve API
server-to-server. Activate is a backend
`POST /api/v1/marketplace/azure/activate` invoked from the landing page
once the purchaser confirms.

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

BYOL files default to roughly 365 days between `issuedAt` and `expiresAt`.
Customer-side servers load the file from `Licensing:LicensePath` (or upload via the admin endpoint) and validate
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
        │ Ed25519 sign  (expiresAt ≤ 90d)
        ▼
[Signed file]
        │
        ▼
[FileBackedLicenseService]  → publishes new in-memory snapshot after validation
```

The AWS Marketplace Entitlement Service does not return a portable signed
token — that's what the optional ALM seller-issued path below is for.
The default mint path therefore treats the adapter's payload as a
trigger and re-verifies entitlement state against AWS using publisher
credentials before signing.

The `RegisterUsage` call runs once on container start
(`AwsRegisterUsageOnStart` `IHostedService`) for EKS / ECS deployments; failures
are logged but do not block startup unless `Aws:Marketplace:RegisterUsage:RequiredOnStart`
is `true`.

#### ALM seller-issued path (optional, deferred to a follow-up)

When `Aws:Marketplace:UseSellerIssuedLicenses=true`, the adapter fetches the seller-issued
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
requiring publisher acknowledgement** — per the operations API
reference, those are `ChangePlan`, `ChangeQuantity`, and
`Reinstate`. Notify-only events (`Unsubscribe`, `Suspend`, `Renew`)
do not surface a pending operation. The Honua webhook handler is
intentionally narrow — JWT verify, payload capture, durable queue
write, ACK — so the inline ACK stays at < 1 s p99 and the 10-second
SLA is unconditional. The queue write must land in the durable
backing (Redis) before 200; the volatile in-memory implementation is
not a webhook-side fallback, because losing an event between ACK and
reconciler drain would break the at-least-once contract Microsoft
expects. If the durable substrate is unavailable, the handler
returns 5xx so Azure retries. Validation (`Get Operation`) and the
PATCH live in the reconciler.

For `ChangePlan` and `ChangeQuantity` initiated from Microsoft
Marketplace, Microsoft auto-Successes the operation on a 10-second
publisher-PATCH timeout per the lifecycle reference: "If PATCH of
operation status isn't received within the 10 seconds, the change
plan is automatically patched as Success." Honua's reconciler-deferred
PATCH therefore does **not** function as a "reject inside 10 s"
hook for those actions — by the time the reconciler runs, Microsoft
has typically already auto-completed the change. The reconciler
PATCHes after the runtime license store accepts the new file so the
publisher's audit trail and Microsoft's operation record agree on
`Success` (or `Failure` if mint or apply fails); a deferred `Failure`
does **not** undo Microsoft's auto-Success on its side, it only
updates the operations-API audit trail. For `Reinstate`, Microsoft's
docs do not specify a timeout-driven auto-Success path; the
publisher's PATCH is the authoritative completion signal but is
still issued from the reconciler so the inline ACK remains fast.
Honua does **not** support publisher-side rejection of any
ack-required action in v1; if a future requirement adds that path,
it requires an inline pre-flight that runs before the ACK.

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
        │    validation. Branch on the operation status:
        │      • 404 → `marketplace_reconciler_runs_total{result=
        │        "operation_rejected"}` (drop; replay or aged-out).
        │      • `status=Conflict` (requested plan/quantity matches
        │        existing per the operations API) → no-op terminal.
        │        Skip mint, skip PATCH, record
        │        `marketplace_reconciler_runs_total{result=
        │        "noop_conflict"}`.
        │      • `status=Succeeded` AND local audit log already has
        │        a terminal PATCH for this `operationId` → peer
        │        replica handled it; dedupe and exit.
        │      • `status=Succeeded` AND no prior local PATCH → for
        │        `ChangePlan` / `ChangeQuantity`, Microsoft has
        │        auto-Successed via the 10 s timeout; continue with
        │        mint + apply, then PATCH (expect HTTP 409 →
        │        `result="auto_completed"`).
        │      • `status=NotStarted` / `InProgress` / `Failed` →
        │        proceed with mint + apply + PATCH path.
        │ 7. Get Subscription (publisher credentials) — only when
        │    the latest entitlement state is needed
        │ 8. POST /api/v1/admin/license/mint  → Ed25519 sign (expiresAt ≤ 90d)
        │ 9. apply via runtime license store → snapshot republished
        │ 10. For ack-required actions only (`ChangePlan`,
        │     `ChangeQuantity`, `Reinstate`): PATCH operation status
        │     (`Success` after step 9, `Failure` on mint/apply fault).
        │     PATCH HTTP 409 means a newer update is already
        │     fulfilled (covers Microsoft's 10 s auto-Success for
        │     `ChangePlan` / `ChangeQuantity`, or a later operation
        │     that has superseded this one) — record
        │     `result="auto_completed"` and continue. Notify-only
        │     events (`Unsubscribe`, `Suspend`, `Renew`) skip step 10
        │     entirely; Microsoft does not expose a pending operation
        │     requiring publisher acknowledgement for those.
        ▼
[Runtime license store]  → validates file → republishes snapshot
```

`PATCH operation status` is scoped to the actions Microsoft's
[operations API reference](https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-operations-api)
surfaces as a pending operation requiring publisher
acknowledgement. Per Microsoft, those are `ChangePlan`,
`ChangeQuantity`, and `Reinstate`. For those, the reconciler PATCHes
`Success` after the runtime license store confirms the new file is valid,
or `Failure` after a mint / apply fault we cannot recover from.

The two operations-API conflict outcomes are distinct and must not
be conflated:

- `GET operation` returning `status=Conflict` is a **no-op terminal**
  reported by Microsoft when the requested plan or quantity already
  matches the existing value. The reconciler must skip both mint
  and PATCH and record
  `marketplace_reconciler_runs_total{result="noop_conflict"}`.
- `PATCH operation` returning HTTP `409 Conflict` is documented as
  "a newer update is already fulfilled" — covering Microsoft's
  10-second auto-Success on `ChangePlan` / `ChangeQuantity` per the
  [lifecycle reference](https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-life-cycle),
  and any later operation that has superseded this one. The
  reconciler records
  `marketplace_operation_status_patches_total{result="auto_completed"}`
  and continues; a deferred `Failure` PATCH does **not** undo a
  Microsoft-side auto-Success.

For `Reinstate`, Microsoft's docs do not specify a timeout-driven
auto-Success path. The reconciler still defers the PATCH so the
inline ACK remains < 1 s, but the publisher's PATCH is the
authoritative completion signal for that action.

The notify-only events (`Unsubscribe`, `Suspend`, `Renew`) do not
expose a pending operation in the operations API. The reconciler
still drains those events to update the local mirror (mark the
local subscription as unsubscribed on `Unsubscribe`, no-op on
`Renew`, etc.) and emits the relevant reconciler / webhook
telemetry, but it does **not** PATCH the operations API for them.

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
        │   5. POST /api/v1/admin/license/mint  → Ed25519 sign  (expiresAt ≤ 90d)
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

#338 does not write the license snapshot to `ICacheService` or Redis. The active
snapshot is in process and replaced on startup load or successful admin upload.

Follow-on file-watch, marketplace, and key-inspection work should use
`ICacheService.RemoveByPatternAsync` for any distributed cache entries it adds:

| Cache | Key | TTL | Invalidation triggers |
|-------|-----|-----|-----------------------|
| Validated license snapshot | `license:snapshot:{licenseId}:{issuedAt}:{keyId}` | 1 hour | File-watcher change, admin upload, adapter re-mint event, trusted-key change. |
| Marketplace subscription state | `marketplace:{cloud}:subscription:{subscription_id}` | 1 hour | Webhook event, manual reconciler trigger. |
| Public-key set | `license:keys:current` | 5 minutes | `IOptionsMonitor<LicenseKeysOptions>` change. |
| AWS entitlements last seen | `marketplace:aws:entitlements:{customer_id}` | 24 hours | Poll cadence, manual refresh. |

The validator must not cache invalid results; only a valid snapshot can be
cached, and only after signature verification succeeds.

---

## 6. Telemetry

### 6.1 ActivitySources

| ActivitySource | Spans |
|----------------|-------|
| `Honua.Licensing.Validator` | `validate_license`, `parse_envelope`, `verify_signature`, `resolve_key_id` |
| `Honua.Licensing.Mint` | `mint_license`, `refresh_license`, `verify_marketplace_evidence` |
| `Honua.Licensing.Aws` | `poll_entitlements`, `register_usage`, `meter_usage`, `submit_mint_request` |
| `Honua.Licensing.Azure` | `webhook_receive`, `webhook_ack`, `resolve_subscription`, `activate_subscription`, `reconcile_subscription`, `get_operation`, `patch_operation_status`, `meter_usage`, `submit_mint_request` |

### 6.2 Meter counters

| Counter | Labels | Notes |
|---------|--------|-------|
| `licenses_issued_total` | `source` | Increments on every successful mint. |
| `licenses_validated_total` | `result` | One of the `LicenseValidationResult` values. |
| `licenses_active` | `edition` | Gauge; updated when the active snapshot changes. |
| `marketplace_metering_records_total` | `cloud`, `result` | `cloud` ∈ `aws`, `azure`; `result` ∈ `enqueued`, `succeeded`, `failed`, `dead_lettered`. |
| `marketplace_webhook_events_total` | `cloud`, `kind`, `result` | Records inbound webhook deliveries only. `kind` covers the SaaS Fulfillment v2 webhook lifecycle actions: `ChangePlan`, `ChangeQuantity`, `Reinstate`, `Suspend`, `Unsubscribe`, `Renew` (the action names Microsoft sends in the webhook payload). `result` ∈ `ack`, `rejected` — used by the runbook for webhook ACK/SLA health (see `MARKETPLACE_OPERATIONS.md` § Webhook health). The browser-driven `Resolve` and `Activate` landing-page calls are **not** recorded here — they are not webhook deliveries and are observed via the `Honua.Licensing.Azure` `resolve_subscription` / `activate_subscription` route spans instead, so mixing them into this counter would corrupt webhook SLA signals. |
| `marketplace_reconciler_runs_total` | `cloud`, `result` | Background worker outcomes; `result` ∈ `succeeded`, `failed`, `unsubscribed`, `operation_rejected` (Microsoft `Get Operation` returned 404 — replay or aged-out), `noop_conflict` (Microsoft `Get Operation` returned `status=Conflict` — requested plan / quantity matched the existing value, so no mint and no PATCH are issued). |
| `marketplace_operation_status_patches_total` | `cloud`, `action`, `result` | Azure-only in v1. Emitted only for the actions Microsoft's operations API surfaces as pending operations requiring publisher acknowledgement: `action` ∈ `change_plan`, `change_quantity`, `reinstate`. `result` ∈ `success_patched`, `failure_patched`, `patch_failed`, `auto_completed`. `auto_completed` is recorded when PATCH returns HTTP `409 Conflict` ("a newer update is already fulfilled" per the operations API), which covers Microsoft's 10-second auto-Success on `ChangePlan` / `ChangeQuantity` and any later operation that has superseded this one. It is a healthy outcome, not a failure; an upward trend on `change_plan` / `change_quantity` is a reconciler-lag signal, not a customer-billing alert. Notify-only events (`Unsubscribe`, `Suspend`, `Renew`) do not emit on this counter because Microsoft does not expose a pending operation requiring publisher PATCH for those — observe them on `marketplace_webhook_events_total` and `marketplace_reconciler_runs_total` instead. |

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

### 7.1 Runtime options in ticket #338

```
Licensing:LicensePath                  = /etc/honua/license.honua-license.json
Licensing:TrustedKeys:honua-2026-q2    = base64url:<32-byte raw Ed25519 public key>
Licensing:AllowAdminUpload             = false
Licensing:ExpiryWarningDays            = 30
```

`Licensing:LicensePath` is optional; empty/unset means Community mode.
`Licensing:AllowAdminUpload=false` is the default. Runtime configuration changes
require restart except that admin upload can validate and replace the configured
file path when upload is enabled.

### 7.2 Follow-on marketplace/mint options

```
License:Signing:Enabled                = false             # publisher-only
License:Signing:KeyId                  = honua-2026-q2
License:Signing:PrivateKeyRef          = secret://...
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

Follow-on options should be validated by `IValidateOptions<T>`. Boolean toggles
default to `false` so a stock customer build does not load any marketplace
dependency or signing surface.

### 7.3 Air-gapped deployments

An air-gapped install ships with:

- `Licensing:TrustedKeys:<keyId>` containing the trusted public key.
- `Licensing:LicensePath` pointing at the customer-supplied BYOL file.
- All `Aws:Marketplace:*` and `Azure:Marketplace:*` settings unset (or
  `Enabled=false`) so no marketplace SDK code path runs.

The validator never resolves DNS, opens a socket, or reads from a public-key
URL. This is asserted by an architecture test.

---

## 8. Testing Strategy

| Layer | Tests | Substrate |
|-------|-------|-----------|
| Unit (`Honua.Core.Tests/Features/Licensing/`) | Feature catalog/domain coverage for edition-gated feature metadata. | Pure compute, no fixtures. |
| Unit / integration (`Honua.Server.Tests/Features/Licensing/`) | #338 runtime loader tests for no path, missing file, valid signed file, malformed JSON/Base64URL, oversized file, unknown key, invalid signature, expired file, upload size guard, HTTP 402 gate, and gRPC `FailedPrecondition`. | Deterministic Ed25519 test keys plus server fixtures where needed. |
| Integration (`Honua.Server.Tests/Features/Admin`, `HealthEndpointsTests`) | Admin status/upload/entitlement endpoints and health/monitoring license summaries. | Testcontainers + Postgres + admin auth. |
| Follow-on integration | Admin mint endpoints, file watcher hot reload, AWS poller, Azure webhook/reconciler, durable buffer fault-injection, public-key inspection, and key-rotation smoke. | Lands with the bounded child tickets that implement those surfaces. |
| Architecture (`Honua.Architecture.Tests`) | No `Honua.Server` symbols leak into `Honua.Core/Features/Licensing/`; public types in `Honua.Core/Features/Licensing/Abstractions/` and `Honua.Core/Features/Licensing/Domain/` carry XML docs; no controllers. | Roslyn analyzers + assembly scan. |

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
- Neither parses → `Malformed`.

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
