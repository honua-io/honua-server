# Audit Coverage Matrix

This document is the authoritative definition of **which operations Honua Server
records as audit events**, how each maps to the audit event model, and where the
emission happens. It pairs with:

- The audit event model and storage (`Honua.Core.Features.AuditLog`, the
  `honua.audit_log` Postgres sink) — see [`compliance-framework.md`](compliance-framework.md).
- The middleware-driven instrumentation that emits the events (#507).

Audit emission is **middleware-driven and pipeline-driven**, never added by hand
in individual endpoint handlers. Two shared components carry every row below:

| Component | Location | Covers |
|---|---|---|
| `AuditLogMiddleware` + `IAuditActionResolver` | `Honua.Hosting/Features/Middleware` | HTTP-surface operations resolved from the matched **route + method + status**: admin mutations, authentication/login, authorization failures. |
| `AuditingFeatureWriter` (shared `IFeatureWriter` decorator) | `Honua.Hosting/Features/Middleware` | Destructive feature writes (delete, bulk/transactional edit) at the shared edit-pipeline boundary, so **every** protocol adapter (GeoServices, OGC API Features, WFS-T, OData, gRPC) is covered once. |

The route → action mapping lives in `DefaultAuditActionResolver`; it is the code
form of this matrix. The destructive-write rows are emitted by the writer
decorator rather than the resolver because protocols such as WFS-T and gRPC
tunnel the operation through a single endpoint where the route alone cannot
reveal whether a delete occurred.

## Event model recap

Each emitted `AuditEvent` carries:

- `Timestamp` (UTC, set at emit time)
- `EventType` — one of `Authentication`, `Authorization`, `AdminAction`,
  `ConfigChange`, `DataExport`, `DataDelete`
- `Actor` / `ActorType` — the authenticated principal (user id, hashed API-key
  id, `anonymous`, or `system`); the raw API key is never recorded
- `ResourceType` / `ResourceId` — the targeted resource family and identifier
- `Action` — a stable dotted-lowercase verb (e.g. `admin.delete`, `feature.delete`)
- `Outcome` — `Success`, `Failure`, or `Denied`
- `CorrelationId`, `RemoteIp`, `UserAgent` (IP/UA may be `null` per privacy policy)
- `Details` — a pre-sanitized JSON object; never contains SQL, connection
  strings, secrets, stack traces, or raw payloads

## Coverage matrix

### Administrative actions

| Operation | Route family | Method(s) | EventType | Action | Outcome source |
|---|---|---|---|---|---|
| Any admin control-plane mutation (service/connection/user/role create, update, delete; configuration changes; OIDC, client-cert, alert, import, license, deploy admin, etc.) | `/api/v{version}/admin/**` | `POST`, `PUT`, `PATCH`, `DELETE` | `AdminAction` | `admin.{method}` | 2xx → `Success`; 401 → `Failure`; 403 → `Denied`; other → `Failure` |

The admin surface is covered by a **route-prefix rule** rather than per-endpoint
wiring, so newly added admin mutation endpoints are audited automatically. Read
(`GET`/`HEAD`) admin requests are not audited on success; an admin read that is
rejected `401`/`403` is still captured by the authorization row below.

### Authentication and authorization

| Operation | Route | Method | EventType | Action | Outcome source |
|---|---|---|---|---|---|
| Admin OIDC backend-assisted login (code → token exchange) | `/api/v{version}/admin/auth/providers/{providerKey}/token` | `POST` | `Authentication` | `auth.login` | 2xx → `Success`; failure → `Failure`/`Denied` |
| First-party OAuth token issuance | `/oauth/token`, `/sharing/rest/oauth2/token` | `POST` | `Authentication` | `auth.token.issue` | 2xx → `Success`; failure → `Failure`/`Denied` |
| Failed authentication (any route) | `**` | any | `Authentication` | `auth.failure` | `401` → `Failure` |
| Permission denied (any route) | `**` | any | `Authorization` | `auth.denied` | `403` → `Denied` |

Login success and failed login therefore both produce an `auth.login`
(success/failure) event on the login routes, while a bare `401`/`403` on any
other route is captured as `auth.failure` / `auth.denied`. This satisfies
"login, failed login, permission denied".

### Destructive data writes

Emitted by `AuditingFeatureWriter` at the shared `IFeatureWriter` boundary, so
the protocol that initiated the edit does not matter.

| Operation | Triggered by (examples) | EventType | Action | Outcome source |
|---|---|---|---|---|
| Single feature delete | FeatureServer `deleteFeatures`, OGC API Features `DELETE .../items/{id}`, OData entity `DELETE`, gRPC delete | `DataDelete` | `feature.delete` | deleted → `Success`; not found → `Failure`; exception → `Failure` |
| Bulk / transactional edit containing deletes | FeatureServer `applyEdits` (with deletes), OGC API Features transaction, WFS-T `Transaction`, OData `$batch` change set, gRPC apply-edits | `DataDelete` | `feature.bulk-edit.delete` | `result.IsSuccess` → `Success`/`Failure` |
| Bulk edit (multi-operation, no delete) | `applyEdits` / transaction with >1 create/update | `DataDelete` | `feature.bulk-edit` | `result.IsSuccess` → `Success`/`Failure` |

`ResourceType` is `feature`; `ResourceId` is the storage layer id. `Details`
carries operation counts (`created`/`updated`/`deleted`/`rolledBack`) — never the
feature payloads. Single-feature creates and single-feature updates are **not**
treated as destructive-write audit events; they remain covered by mutation
metrics/logging.

## Out of scope (this slice)

Per #507 and its parent #350, the following are intentionally not part of this
matrix:

- Audit event schema and storage (delivered in #504).
- Export, retention, and SIEM integration.
- Secure-connection-specific connection auditing (#354).
- Generic structured-logging improvements.

## Edition gating

Audit emission is **not** edition-gated. The `FeatureCatalog` has no audit or
compliance entitlement, and the audit sink already degrades safely to
`NullAuditLog` when no durable backend is configured (tests, non-database hosts).
Gating destructive-write or admin-action auditing behind an edition would create
forensic blind spots in lower editions, so emission runs unconditionally and the
sink decides durability. If an audit entitlement is later introduced, gate it in
the shared components above (resolver + writer decorator) so the gate stays
consistent across every protocol.
