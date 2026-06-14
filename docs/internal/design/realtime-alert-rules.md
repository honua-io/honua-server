# Realtime / Geofence / Threshold / Dwell Alert Rules — Backend Scoping & Handoff

**Status:** Proposed · scoping for implementation handoff
**Issue:** honua-server#1169
**Owner (UI side):** honua-console `/operate/alerts/rules` editor (rule authoring + per-rule delivery-state)
**Audience:** the engineer/agent implementing the honua-server side
**Goal:** confirm and finalize the alert **rule authoring + delivery-state** contract the
`/operate/alerts/rules` editor binds to (geofence enter/exit/dwell + threshold rules, channel selection,
validation, enable/disable, and per-rule delivery health), so the editor renders live instead of the honest
missing-binding state.

---

## 1. TL;DR — what this is and is not

This is **almost entirely already built.** honua-server ships the full alert rule + geofence-zone authoring
surface, validation, delivery-channel evaluation, and per-rule operational health:

- A mapped admin endpoint group, `AlertAdminEndpoints`
  (`src/Honua.Server/Features/Admin/AlertAdminEndpoints.cs`), at `/api/v{version}/admin/alerts`,
  `RequireAdminAuthorization()`, returning `ApiResponse<T>`:
  - **Zones:** list / get / create / update / delete (`/zones[...]`).
  - **Rules:** list / get / create / update / delete (`/rules[...]`), **enable toggle**
    (`PUT /rules/{ruleId}/enabled`), **draft validation** (`POST /rules/test`), and **operational health**
    (`GET /rules/{ruleId}/health`).
- The admin store, `IAlertAdminStore`
  (`src/Honua.Core/Features/Alerts/Abstractions/IAlertAdminStore.cs`) with `PostgresAlertAdminStore`
  (`src/Honua.Postgres/Features/Alerts/PostgresAlertAdminStore.cs`), including `GetRuleHealthAsync`.
- The domain (`src/Honua.Core/Features/Alerts/Domain/AlertModels.cs`): `AlertRuleDefinition`,
  `AlertZoneDefinition`, `AlertTriggerType` (`Enter`/`Exit`/`Dwell`/`Threshold`), `AlertSeverity`
  (`Info`/`Warning`/`Critical`), `AlertChannelType` (webhook/websocket/email/digest/aws_sns/azure_eventgrid/
  slack/microsoft_teams/aws_sqs/azure_eventhub), `AlertRuleHealthSnapshot`, `AlertRuleDeliveryHealth`.
- The wire DTOs (`src/Honua.Server/Features/Admin/Models/AlertAdminModels.cs`): `AlertRuleRequest`,
  `AlertRuleResponse`, `AlertRuleTestRequest`/`AlertRuleTestResponse`, `AlertChannelValidationResponse`,
  `AlertRuleHealthResponse`, `AlertRuleDeliveryHealthResponse`, `AlertRuleRecentTriggerResponse`, all
  source-genned (`AlertAdminJsonContext`) and wrapped in `ApiResponse<T>`.
- Server-side validation that gates persistence: trigger-specific condition checks (dwell requires positive
  `dwellSeconds`; threshold requires `field`/`operator`/numeric `value`), zone-reference rules
  (enter/exit/dwell require a zone; threshold must not), edition gating, and **per-channel delivery
  validation** (configured/unconfigured/disabled/unauthorized) — see `ValidateRuleDraftAsync`.

So #1169's server contract effectively **exists.** **The work is narrow:**

1. **Confirm the console binds `/api/v{version}/admin/alerts/...`** as-is. If `/operate/alerts/rules` still
   shows missing-binding, the gap is most likely console-side wiring to the existing routes, not a missing
   server contract. This doc freezes the contract so the console can bind it with certainty.
2. **Two optional refinements** the editor benefits from (§5): a stable camelCase enum confirmation, and an
   events-by-rule listing for the rule's "recent triggers" drill-down beyond the bounded set already in
   `health`.

---

## 2. Existing pieces to reuse (do not reinvent)

| Concern | Existing type / file | Reuse as |
| --- | --- | --- |
| Rule/zone CRUD + test + health endpoints | `AlertAdminEndpoints` · `Honua.Server/Features/Admin` | **The contract** — bind as-is |
| Admin store | `IAlertAdminStore` / `PostgresAlertAdminStore` | No change |
| Domain | `AlertRuleDefinition`, `AlertZoneDefinition`, `AlertTriggerType`, `AlertSeverity`, `AlertChannelType` · `AlertModels.cs` | The authoring vocabulary |
| Wire DTOs + JSON | `AlertAdminModels.cs` / `AlertAdminJsonContext` | Frozen request/response shapes |
| Validation | `ValidateRuleDraftAsync` + `TryValidateConditions` in `AlertAdminEndpoints` | The pre-persist gate (also exposed via `/rules/test`) |
| Delivery-state | `AlertRuleDeliveryHealth` + `BuildChannelValidation` + `ResolveDeliveryHealthStatus` | The per-channel state the editor renders |
| Event query (recent triggers) | `IAlertEventQuery` · `Honua.Core/Features/Alerts/Abstractions/IAlertEventQuery.cs` | Backs `health.recentTriggers`; reuse for an events-by-rule list if added |
| Edition policy | `IAlertEditionPolicy` (`AlertEditionPolicy.cs`) | Channel/trigger gating — already enforced |
| Audit | `IAuditLog` (`alert_rule.*` events) | Already wired in every mutation |
| Envelope | `ApiResponse<T>` · `src/Honua.Hosting/Features/Models/ApiResponse.cs` | This group uses it (`success`/`data`/`message`/`timestamp`) |

---

## 3. What exists vs the gap (precise)

| Capability | Route today | State |
| --- | --- | --- |
| List rules (filter by `serviceId`, `layerId`) | `GET /api/v{version}/admin/alerts/rules` | **Exists** |
| Get rule | `GET …/rules/{ruleId}` | **Exists** |
| Create rule | `POST …/rules` | **Exists** (validated) |
| Update rule | `PUT …/rules/{ruleId}` | **Exists** (validated) |
| Enable/disable rule | `PUT …/rules/{ruleId}/enabled` | **Exists** (validated when enabling) |
| Delete rule | `DELETE …/rules/{ruleId}` | **Exists** |
| Validate a draft rule (+ draft zone) | `POST …/rules/test` | **Exists** |
| Per-rule delivery-state + health | `GET …/rules/{ruleId}/health` | **Exists** |
| Zone CRUD | `…/zones[...]` | **Exists** |
| Console binding to the above | — | **GAP (confirm; likely console-side wiring)** |
| Events-by-rule listing (full drill-down) | — | **GAP (optional refinement)** |

---

## 4. The console wire contract (FROZEN — already built server-side)

All routes: `/api/v{version}/admin/alerts`, api-version 1.0, `RequireAdminAuthorization()`, JSON camelCase,
`ApiResponse<T>` envelope. Errors: `400` (`ApiResponse<object>.Failure`) for validation, `404` for missing.

### 4.1 Author a rule — `POST /api/v{version}/admin/alerts/rules`

Request (`AlertRuleRequest`):

```jsonc
{
  "serviceId": "vehicles",
  "layerId": 0,
  "zoneId": 12,                       // required for enter/exit/dwell; must be null for threshold
  "ruleName": "Trucks dwelling in depot",
  "triggerType": "dwell",             // "enter" | "exit" | "dwell" | "threshold"
  "conditionsJson": "{ \"dwellSeconds\": 300 }",
  "cooldownSeconds": 600,
  "severity": "warning",              // "info" | "warning" | "critical"
  "editionRequired": "pro",           // "pro" | "enterprise"
  "channels": ["slack", "email"],     // see channel vocabulary below
  "isActive": true
}
```

`conditionsJson` per trigger (validated server-side, `TryValidateConditions`):
- **dwell:** `{ "dwellSeconds": <positive int> }`
- **threshold:** `{ "field": "<name>", "operator": ">"|">="|"<"|"<="|"=="|"!=", "value": <number> }`
- **enter / exit:** no required conditions (zone transition is the trigger).

Channel vocabulary (`AlertChannelType.ToExternalName()`): `webhook`, `websocket`, `email`, `digest`,
`aws_sns`, `azure_eventgrid`, `slack`, `microsoft_teams`, `aws_sqs`, `azure_eventhub`.

Response: `ApiResponse<AlertRuleResponse>` (the persisted rule, `ruleId` assigned). `400` when validation
fails (the first error message is returned; the editor should pre-validate via `/rules/test` for the full
list + per-channel breakdown).

### 4.2 Validate a draft (the editor's live validation) — `POST …/rules/test`

Request (`AlertRuleTestRequest`): `{ "rule": <AlertRuleRequest>, "zone": <AlertZoneRequest|null> }`. The
optional `zone` lets the editor validate a brand-new geofence draft *with* the rule before either is
persisted.

Response: `ApiResponse<AlertRuleTestResponse>`:

```jsonc
{ "success": true, "data": {
  "isValid": false,
  "errors": ["ZoneId is required for enter, exit, and dwell alert rules."],
  "warnings": ["The referenced zone is inactive; the rule will not evaluate spatial transitions until the zone is active."],
  "deliveryChannels": [
    { "channel": "slack", "status": "configured",   "isAllowed": true,  "isConfigured": true,  "message": "The 'slack' channel is available." },
    { "channel": "email", "status": "unconfigured", "isAllowed": true,  "isConfigured": false, "message": "The server is not configured to deliver the 'email' channel." }
  ],
  "evaluatedAt": "2026-06-03T12:00:00Z"
}}
```

Channel `status` literals: `configured` | `unconfigured` | `disabled` | `unauthorized` | `rate_limited` |
`failing`. This is exactly what the editor's per-channel chips bind to.

### 4.3 Per-rule delivery-state + health — `GET …/rules/{ruleId}/health`

Response: `ApiResponse<AlertRuleHealthResponse>`:

```jsonc
{ "success": true, "data": {
  "ruleId": 42,
  "lastEvaluatedAt": "2026-06-03T11:58:00Z",
  "lastTriggeredAt": "2026-06-03T11:40:00Z",
  "activeIncidentCount": 2,
  "recentTriggerCount": 7,
  "coolingDownFeatureCount": 3,
  "nextCooldownExpiresAt": "2026-06-03T12:05:00Z",
  "deliveryFailureCount": 1,
  "deadLetterCount": 0,
  "linkedEventIds": [9001, 8999, 8990],
  "deliveryChannels": [
    { "channel": "slack", "status": "configured", "pendingCount": 0, "processingCount": 0,
      "deliveredCount": 7, "failedCount": 0, "deadLetterCount": 0,
      "lastAttemptAt": "…", "lastDeliveredAt": "…", "lastError": null },
    { "channel": "email", "status": "failing", "pendingCount": 1, "processingCount": 0,
      "deliveredCount": 4, "failedCount": 1, "deadLetterCount": 0,
      "lastAttemptAt": "…", "lastDeliveredAt": "…", "lastError": "SMTP 421 (rate limited)" }
  ],
  "recentTriggers": [
    { "eventId": 9001, "triggerType": "dwell", "severity": "warning", "occurredAt": "…",
      "incidentStatus": "ongoing", "lifecycleStatus": "open", "resourceRef": "alert/9001" }
  ]
}}
```

- `deliveryChannels[].status` reuses the same literal set as §4.2, with operational states derived from the
  dispatch outbox (`ResolveDeliveryHealthStatus`): `failing` / `rate_limited` when there are failed/
  dead-letter rows, `disabled` when the rule is inactive, etc. `lastError` is **sanitized**
  (`AlertDeliveryErrorSummaries.ToSanitizedSummary`) — safe to render.
- `recentTriggers` is bounded (10, `RecentTriggerLimit`). For a full drill-down, see §5.2.

### 4.4 Enable/disable, list, get, delete (already built)

| Method | Route | Body / Notes | Returns |
| --- | --- | --- | --- |
| `GET` | `…/rules?serviceId=&layerId=` | optional filters | `ApiResponse<AlertRuleResponse[]>` |
| `GET` | `…/rules/{ruleId}` | | `ApiResponse<AlertRuleResponse>` |
| `PUT` | `…/rules/{ruleId}` | `AlertRuleRequest` | `ApiResponse<AlertRuleResponse>` |
| `PUT` | `…/rules/{ruleId}/enabled` | `{ "enabled": true }` (re-validated when enabling) | `ApiResponse<AlertRuleResponse>` |
| `DELETE` | `…/rules/{ruleId}` | | `ApiResponse<object>` |
| `GET/POST/PUT/DELETE` | `…/zones[...]` | `AlertZoneRequest` (WKT + SRID geometry) | `ApiResponse<AlertZoneResponse[...]>` |

Zones carry geometry as **WKT** (`wkt` + `srid`); the server parses to WKB via `IGeometryService`. The
editor's geofence draw step posts WKT.

---

## 5. Optional refinements (raise editor fidelity)

These are not required to unblock the editor (the §4 contract is complete), but improve the drill-down:

### 5.1 Confirm camelCase enum wire values

Today the handler emits trigger/severity as `ToString().ToLowerInvariant()` (`"dwell"`, `"warning"`) and
parses them case-insensitively (`TryParseTriggerType` rejects numeric input). These already match the
console's expected lowercase literals — **no change needed**, but freeze them here so they are not "tidied"
into PascalCase later. Channels use the explicit `ToExternalName()` mapping (snake_case for the cloud
channels) — also frozen.

### 5.2 `GET …/rules/{ruleId}/events?status=&limit=&before=` — NEW (optional)

A paged events-by-rule listing for the rule's full trigger history (the `health.recentTriggers` set is
bounded to 10). Back it with the existing `IAlertEventQuery.ListAsync(new AlertEventFilter { RuleId, … })`
already used inside `HandleGetRuleHealth` — this is a thin extraction, not new infrastructure. Response
mirrors `AlertRuleRecentTriggerResponse[]` in an `ApiResponse<T>` envelope with paging metadata. Add the
route to `AlertAdminEndpoints` + `EndpointRegistry.cs` + the JSON context.

---

## 6. Auth, config, secrets

- **Auth:** `RequireAdminAuthorization()` on the whole `/admin/alerts` group. The `/operate/alerts/rules`
  editor is admin; the console sends `X-API-Key`. Every mutation already writes an `IAuditLog`
  config-change event (`alert_rule.create/update/enable/disable/delete`, `alert_zone.*`).
- **Config / secrets:** rule authoring itself needs none. **Delivery-channel configuration** (SMTP, Slack/
  Teams webhook URLs, AWS SNS/SQS, Azure Event Grid/Hub) lives in the existing alert delivery config
  (`AlertOptions` · `src/Honua.Core/Features/Alerts/Domain/AlertOptions.cs`; sinks under
  `src/Honua.Server/Features/Alerts/*DeliverySink.cs`). The editor does NOT author secrets — it only selects
  channels, and the server reports each channel's configured/unconfigured/unauthorized state via §4.2/§4.3
  so the operator sees which channels will actually deliver. Channel secrets follow the standard secret-
  reference/`ISecretProvider` pattern used elsewhere; do not surface them through the rule API.
- **Edition gating:** `IAlertEditionPolicy` enforces which triggers/channels the configured edition allows;
  disallowed selections fail validation (`unauthorized`). This is already wired — honour it.
- **Provider:** alert evaluation depends on the durable change tracker (`IChangeTracker`) + Postgres alert
  stores; read-only/analytics providers cannot host the evaluator. Rule authoring is only meaningful where
  the alert pipeline runs (Postgres).

---

## 7. Build order (suggested)

1. **Verify the console binding.** Point `/operate/alerts/rules` at `/api/v{version}/admin/alerts/rules`
   (+ `/rules/test`, `/rules/{ruleId}/health`, `/zones`). If the editor renders, #1169 is **server-complete**
   — the remaining work is console-side only.
2. **Freeze the enum/channel wire values** (§5.1) with a contract test so they cannot regress to PascalCase.
3. **(Optional) Events-by-rule listing** (§5.2) for the full trigger drill-down.
4. **Tests:** the rule CRUD + test + health endpoints already have integration coverage; add the
   events-by-rule test if §5.2 is implemented. Confirm `EndpointRegistry.cs` covers any new route.

Step 1 is the actual unblock — most of #1169 is already shipped.

---

## 8. Cross-repo

- **honua-console** — `/operate/alerts/rules` binds the §4 routes. The likely remaining work is wiring the
  editor's client to `/api/v{version}/admin/alerts/*` (the contract is live). No server change is required to
  bind; §5 refinements are optional.
- **honua-server** — this document. Keep the §4 request/response shapes and the channel/trigger/severity wire
  literals stable; they are the authoring contract.
