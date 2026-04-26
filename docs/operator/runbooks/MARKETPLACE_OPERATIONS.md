# Marketplace Operations Runbook

Use this runbook for AWS Marketplace and Azure Marketplace SaaS lifecycle
events: webhook health, metering reconciliation, lifecycle state changes,
and common failure modes. The unified license envelope is the same for
both clouds (ADR-0033); the runbook calls out where the cloud-specific
mechanics differ.

This runbook does **not** cover:

- License file format internals or migration. See `LICENSE_MIGRATION.md`.
- Signing key rotation. See `LICENSE_KEY_ROTATION.md`.

---

## Quick Reference

| Concern | AWS | Azure |
|---------|-----|-------|
| Entitlement source of truth | Marketplace Entitlement Service (`GetEntitlements`) and optionally License Manager seller-issued tokens. | Microsoft (no portable token); query via `Get Subscription`. |
| Activation surface | Container `RegisterUsage` on start (EKS / ECS) plus polling. | Landing-page `Resolve` → `Activate` flow. |
| Lifecycle delivery | Polling + entitlement-change events. | Webhook (publisher endpoint) for Suspended / Reinstated / Unsubscribed / plan-change / quantity-change. |
| Webhook ack budget | n/a (no webhook in v1; ALM seller-issued path is opt-in). | < 1 s p99 on test; **hard 10 s SLA** per Azure SaaS Fulfillment v2. |
| Metering API | `MeterUsage` / `BatchMeterUsage`. | Marketplace Metered Billing API. |
| Adapter background services | `AwsEntitlementPollerService`, `AwsMeteringWorker`, `AwsRegisterUsageOnStart`. | `AzureSubscriptionReconcilerService`, `AzureMeteringWorker`. |
| Mint flow | Adapter forwards entitlement evidence to the Honua hosted mint host; mint re-verifies and issues a Honua-signed file with `exp ≤ 90 days`. | Reconciler forwards subscription state to the same hosted mint host. |
| Customer-side signing | None. Adapters never hold the Honua signing key. | None. |

Both adapters are off by default. Enable them only on customer
deployments that purchased through the corresponding marketplace.

---

## Daily / On-Call Telemetry

Verify on every shift:

```bash
# Webhook intake (Azure)
curl https://<host>/metrics | grep marketplace_webhook_events_total
# Expect: events ingressing matches marketplace activity; result="ack" dominates.

# Reconciler health (both clouds)
curl https://<host>/metrics | grep marketplace_reconciler_runs_total
# Expect: result="succeeded" rate equals scheduled cadence; result="failed" near zero.

# Metering durability
curl https://<host>/metrics | grep marketplace_metering_records_total
# Expect: enqueued == succeeded over a rolling 24h window. dead_lettered must be zero.

# Validator inputs from adapter-issued files
curl https://<host>/metrics | grep 'licenses_validated_total{result="valid"'
curl https://<host>/metrics | grep licenses_active

# Refresh lead time pressure
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/license/status"
# Expect: ExpiresAt > now + RefreshLeadTime. Adapter-issued files re-mint
# automatically before the lead time window closes.
```

A flat reconciler success rate or rising metering-buffer depth is the
earliest sign of trouble. Page the licensing on-call when any of these
PromQL expressions match (the `cloud` label is preserved by the `sum
by (cloud)` aggregation so the alert fires per cloud):

- `sum by (cloud)(rate(marketplace_reconciler_runs_total{result="failed"}[30m]))`
  exceeds 5% of the cadence over a 30-minute window.
- `sum by (cloud)(marketplace_metering_records_total{result="dead_lettered"})`
  is non-zero.
- Adapter-issued license `ExpiresAt` is within 24 hours of now (the
  refresh path has been failing for at least `RefreshLeadTime - 1` days).

---

## AWS Marketplace

### Configuration

```
Aws:Marketplace:Enabled                 = true
Aws:Marketplace:CustomerIdentifier      = <AWS Marketplace customer ID>
Aws:Marketplace:PollIntervalSeconds     = 3600
Aws:Marketplace:RegisterUsage:RequiredOnStart = true   # for EKS/ECS
Aws:Marketplace:UseSellerIssuedLicenses = false        # opt-in path
Aws:Marketplace:Mint:BaseUrl            = https://mint.honua.io
Aws:Marketplace:Mint:CredentialRef      = secret://aws-secrets/honua/mint-token
```

`AWSSDK.MarketplaceEntitlementService` and `AWSSDK.MarketplaceMetering`
are loaded only when `Aws:Marketplace:Enabled=true`. Air-gapped or
non-AWS customers leave the toggle off; no marketplace SDK code path
runs.

### Entitlement polling

`AwsEntitlementPollerService` is a `BackgroundService` driven by
`PeriodicTimer` at `Aws:Marketplace:PollIntervalSeconds`. On each tick:

1. Calls `GetEntitlements` for the configured customer identifier.
   The AWS response carries `Entitlements + NextToken` only — there is
   **no portable signature** to forward.
2. Diffs against the cached entitlement state
   (`marketplace:aws:entitlements:{customer_id}`).
3. On divergence (or within `RefreshLeadTime`), POSTs the customer
   identity and observed entitlements as a hint to the mint host —
   `(customer_identifier, account_id, product_code, dimensions /
   observed entitlements)`. Adapter-supplied state is a trigger, not
   evidence.
4. Mint host independently re-queries `GetEntitlements` with publisher
   AWS credentials as the authoritative source and returns a Honua-
   signed file with `exp ≤ 90 days`. Signed-token language only
   applies to the optional ALM seller-issued path below.
5. Adapter persists the file via `FileLicenseStore`, which invalidates
   the validator snapshot cache.

### `RegisterUsage` on container start

For EKS / ECS deployments, `AwsRegisterUsageOnStart` (`IHostedService`)
calls `RegisterUsage` exactly once during startup. Behavior:

- Success → license adapter proceeds normally.
- Failure with `Aws:Marketplace:RegisterUsage:RequiredOnStart=true` →
  startup aborts with event-id `10310`. This is correct for paid SaaS
  containers because AWS denies revenue without a successful registration.
- Failure with `RequiredOnStart=false` → adapter logs and continues; the
  poller picks up entitlements on the next tick.

### Metering

```
[Per-request usage producer]
  → MarketplaceMeteringQueue (in-memory + Redis durable buffer)
  → AwsMeteringWorker (BackgroundService, PeriodicTimer)
  → MeterUsage / BatchMeterUsage (retry with exponential backoff)
```

The metering write path **never** runs inline on the request path. If
Redis is unavailable, the in-memory buffer keeps records up to its
configured ceiling; spillover triggers `result="dead_lettered"` and
emits event-id `10320`.

### Common AWS Failure Modes

| Symptom | Likely cause | Action |
|---------|--------------|--------|
| Poller succeeds but no re-mint occurs. | Entitlement state has not diverged and `exp` is beyond `RefreshLeadTime`. | This is healthy. No action. |
| `marketplace_reconciler_runs_total{cloud="aws",result="failed"}` rising. | Hosted mint unreachable, expired adapter credentials, or AWS Marketplace IAM scope mismatch. | Check `Aws:Marketplace:Mint:CredentialRef` resolves to a valid token; check AWS IAM allows `aws-marketplace:GetEntitlements` for the customer identifier. |
| Validator reports `Expired` for an adapter-issued file. | Adapter has been failing to re-mint for at least `RefreshLeadTime`. | Inspect adapter and reconciler logs (event-id band `10300-10499`). The poller cadence may need shortening. |
| `RegisterUsage` fails on container start. | Container is not a marketplace-purchased SKU, or task role lacks `aws-marketplace:RegisterUsage`. | Verify the deployment was launched from a marketplace AMI / EKS marketplace add-on. Check the task IAM policy. |
| Metering buffer depth not draining. | AWS API throttling or transient outage; or worker stalled. | Inspect `marketplace_metering_records_total` by `result`. Restart the metering worker if needed; AWS API outages resolve themselves through the retry-with-backoff loop. |
| `licenses_validated_total{result="signature_invalid"}` after deploy. | Adapter credentials point at the wrong mint host; mint host signed with a key the customer fleet does not trust. | Confirm `Aws:Marketplace:Mint:BaseUrl` and the public-key set (`License:Keys`). |

### Optional: AWS License Manager seller-issued path

When `Aws:Marketplace:UseSellerIssuedLicenses=true` (deferred to a
follow-up ticket), the adapter consumes the seller-issued ALM token
directly and validates it against the ISV-owned KMS public key locally.
This skips the mint round-trip but introduces a second token format. v1
ships mint-path only; do not enable in production until the follow-up
lands.

---

## Azure Marketplace

### Configuration

```
Azure:Marketplace:Enabled                = true
Azure:Marketplace:Publisher:TenantId     = <publisher Entra tenant>
Azure:Marketplace:Publisher:ClientId     = <publisher app registration>
Azure:Marketplace:Publisher:ClientSecretRef = secret://kv/honua/azure-mp-secret
Azure:Marketplace:Webhook:AllowedAudiences:0 = api://honua-marketplace
Azure:Marketplace:Webhook:MaxBodyKiB     = 8
Azure:Marketplace:Mint:BaseUrl           = https://mint.honua.io
Azure:Marketplace:Mint:CredentialRef     = secret://kv/honua/mint-token
```

The Azure Marketplace SDK does **not** ship a managed first-party client
for the SaaS Fulfillment v2 surface. The adapter implements a small HTTP
client (`AzureFulfillmentClient`) over the documented v2 endpoints using
`IHttpClientFactory` plus the existing
`Honua.Core/Features/Infrastructure/Resilience/HttpResiliencePolicies`.

### Webhook lifecycle

```
[Azure Marketplace]
  POST /api/v1/marketplace/azure/webhook   (Azure AD JWT bearer)
       │
       ▼
   AzureWebhookEndpoint
     1. verify JWT (issuer + audience + signature)
     2. persist event to MarketplaceWebhookQueue (durable)
     3. ACK 200   (target < 1 s p99)
       │
       ▼
[AzureSubscriptionReconcilerService] (background)
     4. drain queue, dedupe by (subscriptionId, operationId)
     5. Get Subscription (publisher credentials)
     6. POST /api/v1/admin/license/mint  → Honua-signed file (exp ≤ 90d)
     7. apply via FileLicenseStore → validator cache invalidated
```

The webhook handler **must** ACK before the 10 s SLA elapses or Azure
will retry — and on persistent failure, suspend the subscription. The
ack-and-enqueue pattern keeps the inline path predictable; mint and Get
Subscription latency lives entirely in the reconciler.

### Resolve / Activate landing-page flow

```
POST /api/v1/marketplace/azure/resolve   (Azure-supplied marketplace token)
   AzureLandingPageEndpoints.ResolveAsync
     → Resolve API call (server-to-server)
     → renders landing page with subscription metadata for the customer

POST /api/v1/marketplace/azure/activate
   AzureLandingPageEndpoints.ActivateAsync
     → Activate API call (server-to-server)
     → POST /api/v1/admin/license/mint → Honua-signed file (exp ≤ 90d)
     → customer download / auto-deploy
```

`/api/v1/marketplace/azure/resolve` and `/api/v1/marketplace/azure/activate`
are public landing-page surfaces; they require Azure-supplied
marketplace tokens but are not admin-scoped. The mint round-trip
(`POST /api/v1/admin/license/mint`) targets the hosted Honua mint host
under M2M auth and is not invoked from the customer side.

### Webhook health

| Signal | Healthy | Action threshold |
|--------|---------|-----------------|
| `marketplace_webhook_events_total{cloud="azure",result="ack"}` | Equals delivered events. | Drop = JWT verification failing or queue write blocked. |
| `marketplace_webhook_events_total{cloud="azure",result="rejected"}` | Zero or near-zero. | Spike = publisher credentials rotated, JWT issuer / audience mismatch, or replay attack. |
| Webhook handler latency (p99) | < 1 s. | > 5 s = DB / Redis backpressure on the queue write. > 10 s = SLA breach; Azure will retry then suspend. |
| Reconciler success rate | Equals webhook ack rate. | Backlog growth = reconciler stalled, mint host unreachable, or `Get Subscription` failing. |

### Metering

Symmetric with AWS:

```
[Per-request usage producer]
  → MarketplaceMeteringQueue (in-memory + Redis durable buffer)
  → AzureMeteringWorker (BackgroundService, PeriodicTimer)
  → Marketplace Metered Billing API (retry with exponential backoff)
```

Same buffer, same retry semantics, same dead-letter telemetry. The two
worker classes share the durable substrate but address different APIs.

### Common Azure Failure Modes

| Symptom | Likely cause | Action |
|---------|--------------|--------|
| Webhook returns 401. | JWT issuer/audience mismatch or stale `Azure:Marketplace:Publisher:*` credentials. | Rotate publisher client secret; verify `AllowedAudiences` matches the configured publisher app registration. |
| Webhook returns 5xx within the 10 s window. | Queue substrate (`MarketplaceWebhookQueue`) write failed. | Check Redis health (`/api/v1/metrics/cache`); the in-memory fallback should keep ACKs flowing — investigate why the fallback is also failing. |
| Webhook does not ack within 10 s; Azure retries flood. | Inline mint or Get Subscription on the webhook path (regression). | Verify the handler matches the documented ack-and-enqueue pattern — the reconciler must be the only path that calls the mint or Get Subscription. |
| `Get Subscription` returns 404 in the reconciler. | Subscription was unsubscribed before the reconciler drained the event. | Mark the local mirror as unsubscribed; do not re-mint. Surface this as `result="unsubscribed"` on the reconciler counter. |
| `Get Subscription` returns 401. | Publisher credentials expired or revoked. | Rotate `Azure:Marketplace:Publisher:ClientSecretRef`. The reconciler retries on the next tick. |
| `licenses_validated_total{result="expired"}` for an adapter-issued file. | Reconciler has been failing for at least `RefreshLeadTime` days. | Inspect Azure logs (event-id band `10500-10699`); confirm publisher credentials and mint reachability. |
| Metering API returns 429. | Throttling. | The retry-with-backoff loop absorbs throttling; only escalate if records dead-letter. |
| Webhook ack latency p99 climbs near 10 s under load. | Body size pressure or queue contention. | Confirm `Azure:Marketplace:Webhook:MaxBodyKiB=8` is enforced; investigate the queue substrate. **Never** address this by skipping the durable persistence — that re-introduces the SLA risk. |

### Operating the 10-Second SLA

The SLA is the most failure-sensitive part of the Azure path. Hold
these invariants:

- **No mint call inline.** The webhook handler persists and ACKs; the
  reconciler is the only path that calls mint.
- **No `Get Subscription` inline.** Same reason.
- **No synchronous external calls during ACK.** JWT verification uses
  cached JWKS; do not re-fetch the JWKS on the request path more
  frequently than the configured TTL.
- **Bounded body parse.** The handler enforces
  `Azure:Marketplace:Webhook:MaxBodyKiB`; oversize payloads are
  rejected before any work happens.
- **Run the SLA assertion test before every release.** The integration
  test (`AzureWebhook_AcksWithinTenSeconds`) must pass — it asserts
  p99 < 1 s under simulated load.

---

## Reconciliation and Drift Recovery

If telemetry indicates the local mirror has drifted from marketplace
truth (rare; usually a sign of credential drift or a mint outage during a
plan change):

1. Capture the current state:

   ```bash
   curl -H "X-API-Key: <admin-key>" \
     "https://<host>/api/v1/admin/license/status"
   ```

2. Trigger a manual reconciliation:

   ```bash
   curl -X POST -H "X-API-Key: <admin-key>" \
     "https://<host>/api/v1/admin/marketplace/<aws|azure>/reconcile"
   ```

   This bypasses the timer and runs the reconciler immediately. The
   endpoint is admin-scoped and idempotent; repeated calls coalesce.

3. Verify the reconciler emitted `result="succeeded"` and the validator
   sees a fresh `iat` on the in-memory `LicenseInfo`.

4. If reconciliation fails repeatedly, treat as a P2 incident and page
   the licensing on-call.

---

## Coordination With Sales / Legal

Marketplace SKU → edition mapping is owned by sales / legal and lives in
configuration:

```
Marketplace:SkuMap:0:ProductCode     = ABCDEFGHIJKL01234567
Marketplace:SkuMap:0:PlanId          = honua-enterprise-monthly
Marketplace:SkuMap:0:Edition         = enterprise
Marketplace:SkuMap:0:Entitlements:0  = alerts.advanced
```

When sales adds a new SKU:

1. Sales / legal updates the `Marketplace:SkuMap` entries in the
   marketplace configuration repository.
2. Adapters pick up the change through `IOptionsMonitor`; no code change
   or release is required.
3. Verify the mapping covers all entitlement keys before announcing the
   SKU. An unknown entitlement key in `entitlements` logs a warning at
   INFO and is dropped — invisible until a customer notices.

A removed SKU stays accepted until the corresponding marketplace
subscription expires; do not remove map entries while subscriptions are
in flight.

---

## Exit Criteria for an Incident

A marketplace incident is resolved when:

- The relevant reconciler counter has been at expected steady-state for
  30 consecutive minutes.
- The metering buffer has drained
  (`marketplace_metering_records_total{result="dead_lettered"}` zero).
- For Azure: webhook ack p99 is < 1 s and no Azure-side retries are
  observed.
- A short post-incident note is appended to the security log with the
  affected `cloud`, `subscription_id` (or hashed equivalent), root
  cause, and recovery action.
