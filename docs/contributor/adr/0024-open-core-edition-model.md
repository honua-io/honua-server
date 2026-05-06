# ADR-0024: Open-Core Edition Model

## Status
Accepted

## Context

The manifesto commits to an open-core model with ELv2 licensing and enterprise
features funded by paid tiers. We need a clear, defensible boundary between what
ships free and what requires a license key — one that maximizes community adoption
while building a sustainable revenue stream.

Key design constraints:

1. **Never gate protocols.** Protocol pluralism (GeoServices REST, OGC, OData,
   MVT, gRPC) is the moat. Gating protocols would undermine the core value
   proposition and slow adoption.
2. **Never gate deployment targets.** Docker, Helm, Terraform, serverless
   templates — all infrastructure that helps people run Honua must be open.
   Gating _where_ you deploy gates adoption. Gate _what runs_ instead.
3. **Never gate SDKs.** The JS, Python, and .NET SDKs are the developer
   adoption funnel. Gating client libraries creates friction at the exact
   moment a developer decides to evaluate Honua.
4. **The Community tier must replace ArcGIS Server for a single team.** If
   Community can't do that, the adoption story collapses.
5. **Align paid tiers to the EnterpriseReady framework** (enterpriseready.io):
   SSO, RBAC, Audit Logging, Change Management, Product Assurance, Deployment
   Options, Integrations, Support, and Reporting.
6. **Operator-grade AI DevOps tooling can be proprietary.** The open-core promise
   applies to the runtime, protocols, SDKs, deployment targets, and base MCP
   data-access surface. Higher-level operator/copilot tooling may remain private
   without undermining platform adoption.

## Decision

Three editions: **Community**, **Pro**, and **Enterprise**. The gate is runtime
capabilities, not infrastructure or protocols.

One additional boundary matters: Honua's operator-grade AI DevOps/copilot
tooling is not part of the open-core runtime. It may live in private enterprise
tooling on top of the public admin/control-plane APIs.

### Edition Boundaries

#### Community (Free, Self-Host, ELv2)

A complete, production-capable feature server for a single-process deployment.

| Area | Included |
|------|----------|
| **Protocols** | All — GeoServices REST (FeatureServer, MapServer, ImageServer, GeometryService), OGC API (Features, Tiles, Maps), WMS 1.3, WMTS 1.0, OData v4, MVT, gRPC (unary) |
| **Data** | PostGIS backend; GeoJSON, Shapefile, GeoPackage, GPX, KML, WKT, FlatGeobuf, File Geodatabase, GeoParquet import; Esri REST service import |
| **Query** | Full Filter AST (CQL2, OData `$filter`, WHERE), spatial queries, statistics, pagination |
| **Edits** | Full CRUD across GeoServices, OGC, and OData; attachments; related records |
| **SDKs** | JS SDK, Python SDK, .NET SDK — full query + edit capabilities |
| **MCP Server** | AI-assisted discovery, query, statistics (REST transport) |
| **Admin UI** | Connections, layer publishing, style editor (Maputnik), map preview, import wizard, spatial SQL playground (query editor with autocomplete, inline map preview, EXPLAIN visualization) |
| **Rendering** | MVT + TileJSON + auto MapLibre styles, MapServer export/identify, WMS GetMap |
| **Caching** | In-memory cache (single process) |
| **Deployment** | Docker, Docker Compose, Helm, Terraform (all cloud modules), serverless (Lambda, Azure Functions), .NET Aspire |
| **Migration** | `honua-migrate` scan, codemod, reconcile CLI |
| **Auth** | API key authentication, CORS |
| **Observability** | Health endpoints, structured logging, Prometheus metrics, OpenTelemetry traces |

Community on serverless works — each invocation is independent with its own
in-memory cache. It is functional at low-to-moderate concurrency. The natural
ceiling is cache coherence and connection pooling at scale, not an artificial gate.

Community explicitly includes the base MCP data-access surface. It does **not**
include private operator/copilot tooling for rollout automation or delegated ops.

#### Pro (Per-Node License)

For teams running production workloads that need scale, streaming, and advanced
analytics. The gate is **distributed coordination and advanced runtime capabilities**.

Everything in Community, plus:

| Area | Included | EnterpriseReady Pillar |
|------|----------|----------------------|
| **Distributed Cache** | Redis L2 cache with fallback, output caching, tile cache seeding — makes multi-node and serverless coherent | Deployment Options |
| **gRPC Streaming** | `QueryFeaturesStream` RPC for high-throughput binary streaming with server-side paging | Product Assurance |
| **MCP gRPC Transport** | MCP server over gRPC-Web (lower latency than REST) | Product Assurance |
| **CDC + Event Bus** | Feature change events via webhooks and Redis Streams — real-time notifications on edits | Integrations |
| **Real-Time Streaming** | WebSocket/SSE feature subscriptions with spatial and attribute filters | Integrations |
| **Spatial Analytics API** | Server-side clustering (DBSCAN), density heatmaps, spatial joins, buffer analytics — PostGIS power as clean endpoints | Product Assurance |
| **AI Spatial Agent** | Natural language spatial query translation; anomaly detection (statistical outliers on geometry/attributes); auto-documentation (ISO 19115/FGDC metadata generation from layer schemas); schema suggestion (field type, index, and spatial reference recommendations for imported data) — all via enhanced MCP | Product Assurance |
| **Offline Sync** | GeoPackage-based delta sync for field collection workflows | Product Assurance |
| **Style Engine** | Programmatic style generation, theme engine (dark, accessible, print), style versioning and diffing | Product Assurance |
| **Priority Support** | Email support, 48hr response SLA | Support |

**Why this is the gate**: distributed cache coordination is what makes
multi-node and high-concurrency serverless actually viable. Without it, each
process is an island. Teams that hit this ceiling have production traffic and
budget. The streaming and analytics features are high-value capabilities that
Esri charges $10K+/year for as separate products.

#### Enterprise (Per-Node License, Annual Contract)

For organizations with compliance, governance, and multi-team requirements.
Maps directly to the full EnterpriseReady framework.

Everything in Pro, plus:

| Area | Included | EnterpriseReady Pillar |
|------|----------|----------------------|
| **SSO / OIDC** | Okta, Entra ID, Ping, Auth0; SAML bridge; SCIM user/group provisioning | Single Sign-On |
| **RBAC** | Per-service, per-layer, per-operation role-based access; row-level security | Role-Based Access Control |
| **Audit Logging** | Immutable audit trail (who queried/edited what, when, from where); SIEM export (Splunk, Datadog, Elastic) | Audit Logging |
| **Change Management** | GitOps manifest API (apply, dryRun, prune); drift detection; approval workflows; rollback | Change Management |
| **Private Operator Copilot** | AI DevOps/operator tooling, rollout planning, delegated operations, and implementation workflows delivered through private enterprise tooling on top of the public control-plane API | Change Management / Support |
| **Compliance** | SOC 2 / FedRAMP evidence collection; data residency controls; encryption-at-rest key rotation | Product Assurance |
| **Federated Queries** | Cross-instance queries (Honua-to-Honua); external source proxy (Esri REST, OGC WFS) | Integrations |
| **Multi-Tenancy** | Schema-per-tenant isolation; tenant-scoped API keys; per-tenant usage metering | Deployment Options |
| **Usage Analytics** | Dashboard — queries/sec, popular layers, slow queries, storage growth, user activity | Reporting |
| **Plugin SDK** | Custom endpoints, pre/post-edit hooks, validators, computed fields (.NET source-gen, AOT-safe) | Integrations |
| **Event Bus (Advanced)** | Kafka and NATS sink support; exactly-once delivery; dead letter queues | Integrations |
| **Secure Connections** | Connection host allowlist; encrypted credential vault; connection audit trail | Product Assurance |
| **App-Level Rate Limiting** | Per-tenant, per-user, per-API-key rate limits (beyond edge enforcement) | Product Assurance |
| **HA + DR** | Active-passive failover playbooks; backup/restore automation; RTO/RPO runbooks | Deployment Options |
| **Premium Support** | Dedicated Slack channel; 4hr response SLA; architecture reviews; migration assistance | Support |

### License Key Enforcement

Runtime license checking at startup:

- Community: no key required. Full functionality within the Community boundary.
- Pro/Enterprise: environment variable or file-based license key validated at
  startup. Gated features return a clear error (HTTP 402 or gRPC `PERMISSION_DENIED`
  with upgrade guidance) when accessed without a valid key.

License checks must be:
- **Offline-capable**: no phone-home requirement. Keys are self-contained
  (signed envelope) with edition, expiry, and any per-edition entitlements
  encoded; see ADR-0033 for the canonical claim set.
- **Transparent**: gated endpoints return actionable error messages, not
  silent failures.
- **Auditable**: license status is visible through the admin license status
  API and runtime health/monitoring payloads.

> The canonical envelope (compact JWS / EdDSA / Ed25519), the BYOL and
> marketplace issuance flows, and the multi-key rotation contract are
> defined in [ADR-0033](0033-unified-license-format.md). The companion design
> doc lives at
> [`docs/contributor/architecture/unified-license-and-entitlement.md`](../architecture/unified-license-and-entitlement.md);
> operational procedures live in the
> [licensing runbooks](../../operator/runbooks/README.md#licensing-runbooks).
>
> Per-node enforcement (a node-count claim plus runtime gating) is **not**
> in the v1 claim set defined by ADR-0033 — Pro and Enterprise are gated
> by `edition` and `entitlements` only. Per-node accounting is deferred
> to a follow-up ticket and would be additive to the JWS payload (a
> `node_count` claim, an `IPerNodeLicenseEnforcer`, and either a heartbeat
> aggregator or a reconciler that meters distinct hosts). It is **not**
> required to ship Pro / Enterprise in v1.

### The Mental Model

```
┌──────────────────────────────────────────────────────┐
│  Enterprise: WHO can do WHAT (governance)             │
│  ┌────────────────────────────────────────────────┐  │
│  │  Pro: HOW FAST and HOW MUCH (scale + power)    │  │
│  │  ┌──────────────────────────────────────────┐  │  │
│  │  │  Community: FULL FEATURE SERVER           │  │  │
│  │  │  All protocols, all SDKs, deploy anywhere │  │  │
│  │  │  Single-process, in-memory cache          │  │  │
│  │  └──────────────────────────────────────────┘  │  │
│  └────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────┘
```

- **Community** = complete feature server, single process, deploy anywhere
- **Pro** = distributed coordination, streaming, analytics
- **Enterprise** = governance, compliance, multi-tenancy, extensibility, and private operator tooling

### EnterpriseReady Pillar Mapping

| Pillar | Community | Pro | Enterprise |
|--------|-----------|-----|-----------|
| Single Sign-On | API keys | API keys | OIDC, SAML, SCIM |
| Audit Logging | Structured logs | Structured logs | Immutable trail + SIEM |
| RBAC | Admin key (all-or-nothing) | Admin key | Per-resource roles + RLS |
| Change Management | Manual config | Manual config | GitOps + drift detection + private operator copilot |
| Product Assurance | AOT, TLS, SQL playground | + Streaming, analytics, sync, AI spatial agent | + Compliance, secure connections |
| Deployment Options | All targets, single-process | + Distributed cache | + Multi-tenant, HA/DR |
| Integrations | SDKs, MCP (REST) | + CDC, real-time, MCP (gRPC) | + Federation, plugins, Kafka/NATS |
| Support | Community (GitHub) | Email, 48hr SLA | Dedicated Slack, 4hr SLA |
| Reporting | Health + Prometheus | Grafana dashboards | + Usage analytics |

## Consequences

### Positive

- **Generous Community tier drives adoption.** A free Honua replaces a $20K+/year
  ArcGIS Server license for most single-team use cases. This fills the top of the
  funnel.
- **Clean gate boundaries.** "Distributed coordination" and "governance" are
  natural inflection points that correlate with organizational budget, not
  artificial feature crippling.
- **Serverless works at every tier.** Community users can deploy to Lambda; it
  just runs without shared cache. Pro makes it coherent at scale. No artificial
  block on deployment targets.
- **EnterpriseReady alignment.** The framework is well-understood by enterprise
  buyers. Mapping features to pillars makes the value proposition legible to
  procurement teams.
- **ELv2 protects the lane.** Competitors cannot offer Honua as a managed
  service, preserving the option for a future Honua Cloud offering.

### Negative

- **License key infrastructure is new work.** Requires signed-key generation,
  startup validation, per-endpoint gating middleware, and Admin UI integration.
- **Gray areas will emerge.** Some features will straddle tier boundaries (e.g.,
  "is a webhook a Pro CDC feature or a Community notification?"). These need
  case-by-case adjudication.
- **Community ceiling must feel natural, not punitive.** If Community users hit
  too many "upgrade to Pro" errors in normal workflows, it damages trust. The
  boundary must align with genuine scaling inflection points.

### Supersedes

- ADR-0020 (MVP Operational Deferrals): the deferrals described there (rate
  limiting, audit logs, compliance) are now scoped to the Enterprise tier rather
  than being indefinitely deferred.
