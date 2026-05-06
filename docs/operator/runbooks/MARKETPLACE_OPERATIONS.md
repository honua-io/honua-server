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

## Status / Prerequisites

This runbook documents the canonical AWS Marketplace and Azure
Marketplace adapter contract defined in ADR-0033. **None of the
marketplace adapter routes, services, or telemetry counters described
below are present on `feature/804`.** They land with the AWS and Azure
adapter child tickets per ADR-0033 § "Bounded Child Tickets":

| Surface | Status on `feature/804` | Lands with |
|---------|-------------------------|------------|
| `POST /api/v1/admin/marketplace/aws/reconcile` | Route is **not yet registered** in `EndpointRegistry`. The CONTROL_PLANE_API contract lists it under "land with the AWS marketplace adapter child ticket". A current build returns HTTP 404. | AWS marketplace adapter child ticket. |
| `POST /api/v1/admin/marketplace/azure/reconcile` | Route is **not yet registered**. A current build returns HTTP 404. | Azure marketplace adapter child ticket. |
| `POST /api/v1/marketplace/azure/webhook` | Public Azure-AD-bearer route is **not yet registered**; the webhook handler (`AzureWebhookEndpoint`) and the durable `MarketplaceWebhookQueue` ship together. | Azure marketplace adapter child ticket. |
| `GET /api/v1/marketplace/azure/landing` | Public landing-page route is **not yet registered**; `AzureLandingPageEndpoints.LandingAsync` ships with the adapter. | Azure marketplace adapter child ticket. |
| `POST /api/v1/marketplace/azure/activate` | Public activation route is **not yet registered**; `AzureLandingPageEndpoints.ActivateAsync` ships with the adapter. | Azure marketplace adapter child ticket. |
| AWS adapter services (`AwsEntitlementPollerService`, `AwsRegisterUsageOnStart`, `AwsMeteringWorker`) | **Not yet implemented.** `Aws:Marketplace:*` configuration keys are reserved by ADR-0033 but no `BackgroundService` registrations exist on this branch. | AWS marketplace adapter child ticket. |
| Azure adapter services (`AzureSubscriptionReconcilerService`, `AzureMeteringWorker`, `AzureFulfillmentClient`) | **Not yet implemented.** `Azure:Marketplace:*` configuration keys are reserved by ADR-0033 but no service registrations exist on this branch. | Azure marketplace adapter child ticket. |
| `marketplace_webhook_events_total{cloud,kind,result}`, `marketplace_reconciler_runs_total{cloud,result}`, `marketplace_operation_status_patches_total{cloud,action,result}`, `marketplace_metering_records_total{cloud,result}` Prometheus counters | Counter shapes are reserved by ADR-0033 § Cross-Cutting Reuse and the architecture doc § 6.2, but emit only after the owning adapter child tickets land. `curl https://<host>/metrics \| grep marketplace_` returns no rows on a current build. | AWS / Azure marketplace adapter child tickets (per cloud). |
| Licensing event-id bands `10300-10499` (AWS adapter) and `10500-10699` (Azure adapter), including specific emitters such as `10310` (`RegisterUsage` failure) and `10320` (metering dead-letter) | Band reservations are documented in ADR-0033, but emitters appear with their owning adapters. | AWS / Azure marketplace adapter child tickets. |
| Integration tests (`AzureWebhook_AcksWithinTenSeconds`, `AzureReconciler_PatchesOperationStatusOrRecordsAutoCompleted`, `AzureWebhook_Returns5xxWhenDurableQueueUnavailable`, `AzureReconciler_RecordsNoopConflictWhenGetOperationReturnsConflict`) | Test fixtures **not yet authored** on this branch. | Azure marketplace adapter child ticket. |
| `GET /api/v1/admin/license/status` | Operational. Used by the daily on-call check and reconciliation flow as a cross-check on adapter-issued license freshness. | Already in `LicenseAdminEndpoints`. |

The runbook is published ahead of those child tickets so the adapter
contract is reviewable in isolation. Treat every surface marked above
as **prerequisite-bound** and confirm the corresponding child ticket
has landed before running the command on a customer environment. On a
current build, `curl` calls against the marketplace routes return
HTTP 404 and `grep marketplace_` against `/metrics` returns nothing
because the emitters are not yet wired.

---

## Quick Reference

| Concern | AWS | Azure |
|---------|-----|-------|
| Entitlement source of truth | Marketplace Entitlement Service (`GetEntitlements`) and optionally License Manager seller-issued tokens. | Microsoft (no portable token); query via `Get Subscription`. |
| Activation surface | Container `RegisterUsage` on start (EKS / ECS) plus polling. | Browser landing page (`GET /api/v1/marketplace/azure/landing?token=...`) → server-to-server `Resolve` → backend `POST /activate` → server-to-server `Activate`. |
| Lifecycle delivery | Polling + entitlement-change events. | Webhook (publisher endpoint) for `ChangePlan` / `ChangeQuantity` / `Reinstate` (ack-required via PATCH operation status) and `Suspend` / `Unsubscribe` / `Renew` (notify-only). |
| Webhook ack budget | n/a (no webhook in v1; ALM seller-issued path is opt-in). | < 1 s p99 on test; **hard 10 s SLA** per Azure SaaS Fulfillment v2. |
| Metering API | `MeterUsage` / `BatchMeterUsage`. | Marketplace Metered Billing API. |
| Adapter background services | `AwsEntitlementPollerService`, `AwsMeteringWorker`, `AwsRegisterUsageOnStart`. | `AzureSubscriptionReconcilerService`, `AzureMeteringWorker`. |
| Mint flow | Adapter forwards entitlement evidence to the Honua hosted mint host; mint re-verifies and issues a Honua-signed file with `exp ≤ 90 days`. | Reconciler forwards subscription state to the same hosted mint host. |
| Customer-side signing | None. Adapters never hold the Honua signing key. | None. |

Both adapters are off by default. Enable them only on customer
deployments that purchased through the corresponding marketplace.

---

## Daily / On-Call Telemetry

Verify on every shift (the `marketplace_*_total` counters and
adapter-driven `licenses_*` series populate only after the AWS /
Azure marketplace adapter child tickets land — see § "Status /
Prerequisites" above):

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
| `licenses_validated_total{result="signature_invalid"}` after deploy. | Adapter credentials point at the wrong mint host; mint host signed with a key the customer fleet does not trust. | Confirm `Aws:Marketplace:Mint:BaseUrl` and the runtime public-key set (`Licensing:TrustedKeys`). |

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
     2. capture (operationId, subscriptionId, action, status,
        planId?, quantity?) from the body; reject bodies above
        Azure:Marketplace:Webhook:MaxBodyKiB before parsing
     3. persist event to MarketplaceWebhookQueue (durable)
     4. ACK 200   (target < 1 s p99)
       │
       ▼
[AzureSubscriptionReconcilerService] (background)
     5. drain queue, dedupe by (subscriptionId, operationId)
     6. Get Operation (publisher credentials) — branch on status:
          • 404                 → operation_rejected (drop; replay
                                  or aged out)
          • status=Conflict     → noop_conflict (Microsoft says the
                                  requested plan/quantity already
                                  matches existing — skip mint AND
                                  PATCH; record on
                                  marketplace_reconciler_runs_total)
          • status=Succeeded    → if local audit log already has a
                                  terminal PATCH for this
                                  operationId, dedupe; otherwise
                                  Microsoft auto-Successed (10 s
                                  timeout for ChangePlan/ChangeQty),
                                  continue with mint + PATCH (PATCH
                                  HTTP 409 expected → auto_completed)
          • NotStarted/InProgress/Failed → continue
     7. Get Subscription (publisher credentials) — fetches the
        latest entitlement state when the operation needs it
     8. POST /api/v1/admin/license/mint  → Honua-signed file (exp ≤ 90d)
     9. apply via FileLicenseStore → validator cache invalidated
    10. For ack-required actions (ChangePlan, ChangeQuantity,
        Reinstate): PATCH operation status (Success after step 9,
        Failure on mint/apply fault). PATCH HTTP 409 means a newer
        update is already fulfilled (covers Microsoft's 10-second
        auto-Success on ChangePlan/ChangeQuantity per the lifecycle
        doc, or any later operation that has superseded this one) —
        record `result="auto_completed"` and continue. Notify-only
        events (Unsubscribe, Suspend, Renew) skip step 10 entirely;
        Microsoft does not expose a pending operation requiring
        publisher acknowledgement for those.
```

The webhook handler **must** ACK before the 10 s SLA elapses or Azure
will retry — and on persistent failure, suspend the subscription. The
ack-and-enqueue pattern keeps the inline path predictable; Get
Operation, Get Subscription, mint, and the PATCH operation status call
all live in the reconciler.

#### Why Get Operation runs in the reconciler, not on ACK

Microsoft SaaS Fulfillment v2 expects the publisher to validate every
webhook payload against the operations API before acting on it
(<https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-webhook>).
Webhook payloads alone are not authoritative — only `Get Operation`
returning a status that matches the webhook (`NotStarted` /
`InProgress` / `Succeeded`) for the same
`operationId` / `subscriptionId` confirms the event is real. We push
that call into the reconciler so the inline ACK stays at < 1 s p99
and the 10 s webhook SLA is unconditional. The trade-off is explicit
below: Microsoft does not wait for our reconciler to validate before
acting on its own side, so a deferred `Get Operation` is purely a
defense against replay / spoofed payloads, not a gate on Microsoft's
state machine.

#### What PATCH operation status does — and what it does not do

Microsoft's
[operations API](https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-operations-api)
exposes `PATCH /operations/{operationId}` only for the actions that
Microsoft surfaces as a pending operation requiring publisher
acknowledgement. Per Microsoft, "this only applies to webhook events
such as `ChangePlan`, `ChangeQuantity`, and `Reinstate` that need an
ACK." The notify-only events (`Unsubscribe`, `Suspend`, `Renew`)
expose no pending operation and require no ACK.

For the three ack-required actions (`ChangePlan`, `ChangeQuantity`,
`Reinstate`) the reconciler PATCHes `Success` after
`FileLicenseStore` confirms the new file is valid, or `Failure`
after a mint / apply fault we cannot recover from.

For `ChangePlan` and `ChangeQuantity` initiated from Microsoft
Marketplace, the
[lifecycle doc](https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-life-cycle)
states explicitly: "If PATCH of operation status isn't received
within the 10 seconds, the change plan is automatically patched as
Success." The reconciler-deferred PATCH therefore typically lands
after Microsoft has already auto-Successed the change on its own
side. The reconciler PATCH is the publisher's authoritative entry
in the operations API audit trail; it is **not** a control over
Microsoft's state machine.

A deferred `Failure` PATCH on `ChangePlan` / `ChangeQuantity` does
**not** undo Microsoft's auto-Success: by the time the reconciler
PATCHes, Microsoft's billing has already moved to the new plan /
quantity on its side. The Failure record only updates the
operations-API audit trail. Honua v1 has no inline rejection path;
if a future requirement adds publisher-side rejection, it requires a
separate inline pre-flight that PATCHes `Failure` before the webhook
ACK and satisfies the 10-second SLA on its own. That path is out of
scope here.

For `Reinstate`, Microsoft's docs do not specify a timeout-driven
auto-Success path. The reconciler still issues the PATCH from the
deferred path (so the inline ACK stays at < 1 s), but the publisher's
PATCH is the authoritative completion signal for that action.

##### Two distinct conflict outcomes — do not conflate

Microsoft's operations API surfaces two different conflict
conditions that the reconciler must handle separately:

- **`GET operation` returning `status=Conflict`** is documented as
  "new quantity / plan is the same as existing." It is a no-op
  terminal status — Microsoft is telling the publisher that nothing
  needs to change because the requested value already matches. The
  reconciler skips both mint and PATCH and records the run on
  `marketplace_reconciler_runs_total{cloud="azure",result="noop_conflict"}`.
- **`PATCH operation` returning HTTP `409 Conflict`** is documented
  as "a newer update is already fulfilled." This covers Microsoft's
  10-second auto-Success on `ChangePlan` / `ChangeQuantity` and any
  later operation that has superseded this one. The reconciler
  records
  `marketplace_operation_status_patches_total{cloud="azure",result="auto_completed"}`
  and continues.

The notify-only events (`Unsubscribe`, `Suspend`, `Renew`) do not
expose a pending operation in Microsoft's operations API. The
webhook is a notification of a state change Microsoft has already
committed on its own side. The reconciler still drains those events
to update the local mirror (mark the local subscription as
unsubscribed on `Unsubscribe`, no-op on `Renew`, etc.) and emits the
relevant
`marketplace_webhook_events_total{cloud="azure",kind=...}` and
`marketplace_reconciler_runs_total{cloud="azure",result=...}`
telemetry, but it does **not** PATCH the operations API for them.

### Resolve / Activate landing-page flow

```
[Purchaser browser — Microsoft redirects to the configured landing page]
GET /api/v1/marketplace/azure/landing?token=<marketplace-token>
   AzureLandingPageEndpoints.LandingAsync
     → validate query parameter is present and well-formed
     → POST <fulfillment-api>/saas/subscriptions/resolve
       (server-to-server; `x-ms-marketplace-token: <marketplace-token>`)
     → render activation HTML with subscription metadata for the purchaser

[Browser POSTs the form on the rendered landing page]
POST /api/v1/marketplace/azure/activate
   AzureLandingPageEndpoints.ActivateAsync
     → POST <fulfillment-api>/saas/subscriptions/{id}/activate
       (server-to-server; publisher access token)
     → POST /api/v1/admin/license/mint → Honua-signed file (exp ≤ 90d)
     → customer download / auto-deploy / activation confirmation view
```

`/api/v1/marketplace/azure/landing` is the public, browser-facing
landing page that Microsoft redirects the purchaser's browser to with
`?token=<marketplace-token>`; the handler then exchanges that token
with Microsoft's Resolve API server-to-server (per
<https://learn.microsoft.com/en-us/partner-center/marketplace-offers/pc-saas-fulfillment-life-cycle>).
The activate route is a backend POST from the landing page once the
purchaser confirms. Both routes are public (no admin scope), but the
mint round-trip (`POST /api/v1/admin/license/mint`) targets the hosted
Honua mint host under M2M auth and is not invoked from the purchaser's
browser.

The publisher's marketplace offer registration must point its "Landing
page URL" at the public hostname for the customer instance plus
`/api/v1/marketplace/azure/landing`. A registration that points at a
`POST` route or a non-public hostname will surface as a Microsoft
purchase-flow failure rather than as a Honua telemetry signal.

### Webhook health

| Signal | Healthy | Action threshold |
|--------|---------|-----------------|
| `marketplace_webhook_events_total{cloud="azure",result="ack"}` | Equals delivered events. | Drop = JWT verification failing or queue write blocked. |
| `marketplace_webhook_events_total{cloud="azure",result="rejected"}` | Zero or near-zero. | Spike = publisher credentials rotated, JWT issuer / audience mismatch, or replay attack. |
| Webhook handler latency (p99) | < 1 s. | > 5 s = DB / Redis backpressure on the queue write. > 10 s = SLA breach; Azure will retry then suspend. |
| Reconciler success rate | Equals webhook ack rate. | Backlog growth = reconciler stalled, mint host unreachable, `Get Operation` rejecting events, or `Get Subscription` failing. |
| `marketplace_operation_status_patches_total{cloud="azure",action ∈ "change_plan"\|"change_quantity"\|"reinstate",result="success_patched"\|"auto_completed"}` | For each `action`, the sum of `success_patched` + `auto_completed` equals the count of webhooks ingested for that action **that proceed to the PATCH step** over the same window. Webhooks that GET operation returns `status=Conflict` for never reach PATCH and are accounted for separately on the aggregate `marketplace_reconciler_runs_total{result="noop_conflict"}` signal (next row) — the reconciler counter carries only `cloud` and `result` labels, so per-action attribution of `noop_conflict` is not available from telemetry; per Microsoft's operations API, GET `status=Conflict` only applies to `ChangePlan` / `ChangeQuantity`, so any shortfall in the per-action `change_plan` / `change_quantity` sum is expected to track the aggregate `noop_conflict` rate. | Audit gap: drop in the sum (after accounting for the aggregate `noop_conflict` rate) means the reconciler is not reaching step 10 — investigate the audit trail. On `change_plan` / `change_quantity`, a rising `auto_completed` rate is **not** a customer-billing problem (Microsoft auto-Successes after 10 s either way) but indicates the reconciler is consistently lagging Microsoft's auto-completion window; tighten the reconciler tick or scale workers. On `reinstate`, an `auto_completed` spike is unusual (Microsoft's Reinstate flow does not document a timeout-driven auto-Success) and warrants investigation of operation supersession or duplicate webhook delivery. |
| `marketplace_reconciler_runs_total{cloud="azure",result="noop_conflict"}` | Low; tracks `Get Operation` returning `status=Conflict` at the aggregate level — the counter has only `cloud` and `result` labels, so per-action attribution is not available from this metric. Per Microsoft's operations API the conflict only applies to `ChangePlan` / `ChangeQuantity` ("new quantity / plan is the same as existing"), so any non-zero rate is implicitly bounded to those two actions by spec. | Sustained non-zero rate = upstream is producing redundant `ChangePlan` / `ChangeQuantity` submissions; coordinate with the customer-side admin UI to suppress no-op submissions. Reconciler is correct to skip mint + PATCH on this status. |
| `marketplace_operation_status_patches_total{cloud="azure",result="patch_failed"}` | Zero. | Non-zero = publisher credentials lost write scope on the operations API, or a transient outage that the retry-with-backoff loop is not absorbing. |

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
| Webhook returns 5xx within the 10 s window. | Durable write to `MarketplaceWebhookQueue` failed. | A 5xx is the **correct** behavior here — the handler must not ACK on volatile fallback alone, because losing the event between ACK and reconciler drain would break the at-least-once delivery contract with Microsoft. Restore the durable substrate (Redis health at `/api/v1/metrics/cache`, queue lease coordination, network reachability) so writes succeed; Azure retries on 5xx by design. The in-memory implementation of `MarketplaceWebhookQueue` is reserved for dev/test or single-process deployments where durability is provided externally; production deployments with `Azure:Marketplace:Enabled=true` require Redis-backed persistence. |
| Webhook does not ack within 10 s; Azure retries flood. | Inline mint, Get Operation, Get Subscription, or PATCH on the webhook path (regression). | Verify the handler matches the documented ack-and-enqueue pattern — only the JWT verify, body capture, and queue write run inline; every operations / fulfillment API call is the reconciler's responsibility. |
| `Get Operation` returns 404 in the reconciler. | Webhook payload references an operation Microsoft cannot find — replay, spoofed payload, or operation already aged out. | Drop the event; do **not** mint or PATCH. Surface as `marketplace_reconciler_runs_total{cloud="azure",result="operation_rejected"}` and inspect the audit log for replay patterns. |
| `Get Operation` returns `status=Conflict` (operation status, not HTTP 409). | Microsoft documents this status as "new quantity / plan is the same as existing" — a no-op terminal where the requested change matches the current value. | Healthy outcome. The reconciler **skips both mint and PATCH** and records `marketplace_reconciler_runs_total{cloud="azure",result="noop_conflict"}`. Persistent spikes indicate the upstream change request flow is producing redundant submissions; investigate the customer-side admin UI or the Microsoft Marketplace flow that originated the change. |
| `Get Operation` returns `status=Succeeded` for the same `operationId`. | Either (a) another reconciler replica already processed the event behind the lease, or (b) Microsoft auto-Successed a `ChangePlan` / `ChangeQuantity` operation via its 10-second timeout before our reconciler ran. | Healthy outcomes: for (a) dedupe and skip the event entirely; for (b) the reconciler still mints + applies the new file (so internal state matches Microsoft) and PATCHes — the PATCH is expected to land as HTTP `409 Conflict`, recorded as `marketplace_operation_status_patches_total{result="auto_completed"}`. The reconciler distinguishes (a) from (b) by checking whether our local audit log already has a Success / Failure record for the same `operationId`. |
| `PATCH operation` for `ChangePlan` / `ChangeQuantity` / `Reinstate` never sent. | Reconciler stalled before step 10, or the operations API was unreachable through the retry-with-backoff window. | For `change_plan` / `change_quantity`, Microsoft auto-Successes via the 10 s timeout regardless, so the customer's billing has already moved on Microsoft's side; the audit-trail gap is still real — manually trigger reconciliation (`POST /api/v1/admin/marketplace/azure/reconcile`) so `Get Operation` returns the auto-Successed state and the reconciler records `result="auto_completed"`. For `reinstate`, no documented auto-Success applies — the customer's subscription state on Microsoft's side may diverge from local until reconciliation completes; treat as P2 and page the licensing on-call if the manual reconcile fails. Investigate why the reconciler stalled. |
| `PATCH operation` returns HTTP `409 Conflict` (PATCH-side, not GET status). | Per the operations API: "a newer update is already fulfilled." For `ChangePlan` / `ChangeQuantity` this typically means Microsoft auto-Successed via the 10 s timeout; for any action it can also mean a later operation has superseded this one. | Healthy outcome, not a failure. Reconciler records `marketplace_operation_status_patches_total{cloud="azure",result="auto_completed"}`. No action unless the rate trends upward on `change_plan` / `change_quantity` (reconciler-lag signal — tighten the tick) or appears on `reinstate` (investigate operation supersession or duplicate webhook delivery). |
| `PATCH operation` returns 401 or 403. | Publisher credentials lost write scope on the operations API. | Rotate `Azure:Marketplace:Publisher:ClientSecretRef`; ensure the publisher app registration retains `SaaSAPI.FullAccess`. |
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
- **No `Get Operation` inline.** The replay / spoof guard runs in the
  reconciler. The validation is for the publisher's audit trail — it
  does not gate Microsoft's state machine, which moves on its own
  schedule for `ChangePlan` / `ChangeQuantity` (auto-Success on a
  10 s timeout per the lifecycle doc).
- **No `Get Subscription` inline.** Same reason.
- **No `PATCH operation status` inline.** The PATCH ships from the
  reconciler after `FileLicenseStore` confirms the new file. PATCHing
  inline would either (a) extend the inline path past the 10 s SLA or
  (b) ACK `Success` before the change actually applied, which leaves
  the customer in a divergent state if the mint then fails. For
  `ChangePlan` / `ChangeQuantity`, accept that Microsoft auto-Successes
  the operation on the 10 s timeout — the PATCH is for audit only and
  a PATCH HTTP `409 Conflict` (`auto_completed`) is a healthy outcome,
  not a failure. For `Reinstate`, no documented auto-Success applies;
  the deferred PATCH is still the publisher's authoritative completion
  signal.
- **No synchronous external calls during ACK.** JWT verification uses
  cached JWKS; do not re-fetch the JWKS on the request path more
  frequently than the configured TTL.
- **Bounded body parse.** The handler enforces
  `Azure:Marketplace:Webhook:MaxBodyKiB`; oversize payloads are
  rejected before any work happens.
- **No ACK without durable persistence.** The handler enqueues to
  `MarketplaceWebhookQueue` and only returns 200 after the durable
  write succeeds. If the durable substrate (Redis) is unavailable,
  return 5xx so Azure retries — never ACK on the volatile in-memory
  implementation for the webhook queue, since the event would be
  lost on process exit while preventing Azure's retry. The in-memory
  fallback substrate is appropriate for the metering queue (which is
  eventually consistent and accepts dead-letter loss as a counted
  outcome), **not** for the webhook queue. Production deployments
  with `Azure:Marketplace:Enabled=true` require Redis.
- **Run the SLA assertion test before every release.** The integration
  test (`AzureWebhook_AcksWithinTenSeconds`) must pass — it asserts
  p99 < 1 s under simulated load with `Get Operation`,
  `Get Subscription`, mint, and PATCH operation status all stubbed off
  the inline path.

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

2. Trigger a manual reconciliation (the reconcile route is registered
   only after the corresponding marketplace adapter child ticket lands;
   see § "Status / Prerequisites" above — a current build returns
   HTTP 404):

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
