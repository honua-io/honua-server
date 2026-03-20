# Enterprise Security Questionnaire Starter

This document provides reusable responses for common security questionnaire topics during enterprise evaluations. Each answer reflects the current published posture; items not yet available are called out explicitly.

Use this alongside the [Enterprise Procurement Readiness](ENTERPRISE_PROCUREMENT_READINESS.md) document when assembling a security packet.

---

## 1. Hosting and Deployment Model

**Q: Where is the application hosted?**

Honua Server is a customer-managed deployment. The customer provisions and operates the infrastructure in their own cloud account or on-premises environment. Honua provides container images, a Helm chart, deployment guidance, and support commitments. See [Infrastructure & Deployment](../devops/infrastructure.md) and [Deployment Scenarios](../devops/DEPLOYMENT_SCENARIOS.md).

**Q: What deployment targets are supported?**

- Docker Compose (evaluation and non-critical use)
- Kubernetes via Helm chart (recommended production deployment)
- AWS ECS/EKS with ALB, RDS for PostgreSQL + PostGIS, and ElastiCache
- Azure ACA/AKS with Application Gateway, Azure Database for PostgreSQL, and Azure Cache for Redis
- Serverless targets (AWS Lambda, Azure Functions) for specific workloads

Container images are published for `linux/amd64` and `linux/arm64`. AOT-compiled images are recommended for production. See [Infrastructure](../devops/infrastructure.md) for the full image catalog.

**Q: Is a SaaS or managed hosting option available?**

Not at this time. Honua is deployed and operated by the customer. A managed offering may be introduced in the future; contact Honua for the current roadmap.

---

## 2. Authentication and Authorization

**Q: How is administrative access authenticated?**

Admin API endpoints (`/api/v1/admin/*`) require authentication via one of:
- **API key**: `X-API-Key` header, configured with `HONUA_ADMIN_PASSWORD`
- **OIDC**: optional bearer-token flow for browser-based Admin UI access (supports Azure AD, Google, and generic OIDC providers)
- **Basic compatibility mode**: optional, for legacy client migration only

Authentication precedence: Bearer (if OIDC enabled) > API key > Basic (if enabled). See [Security Configuration](../devops/security.md).

**Q: How is access to geospatial data controlled?**

Data API access (FeatureServer, OGC API, OData, Tiles) is governed by service and layer access controls. Operators configure whether data endpoints are public or require authentication. Row-level security and RBAC are Enterprise edition features (see [ADR 0024](../contributor/adr/0024-open-core-edition-model.md)).

**Q: Is SSO/OIDC supported?**

Yes. OIDC is supported for the Admin UI and token-based API access. SSO enforcement and RBAC are Enterprise edition capabilities.

---

## 3. Network and Edge Security

**Q: How is network traffic secured?**

- TLS termination is enforced at the ingress or cloud edge (ALB, Application Gateway, or ingress controller), not inside the Honua process.
- WAF, IP allowlists, and rate limiting are operator responsibilities enforced at the edge. Recommended starting limits and cloud-specific templates are provided in [Security Configuration](../devops/security.md).
- Forwarded-header processing is configurable for trusted proxy chains.

**Q: Does the application perform rate limiting?**

No application-level rate limiting is included (MVP deferral). Rate limiting is enforced at the edge. Honua provides recommended starting limits and WAFv2/Application Gateway policy templates. App-level rate limiting is planned for the Enterprise edition.

**Q: How is the Admin UI secured in production?**

The Admin UI is served at `/admin` and requires admin authentication. Operators should restrict access at the edge using network allowlists or VPN. A Content Security Policy (CSP) guide is provided.

---

## 4. Data Protection and Encryption

**Q: Is data encrypted in transit?**

Yes. TLS is expected at the ingress or cloud edge and between managed services (e.g., application to managed database, application to managed cache). Honua documentation assumes TLS is configured as part of the customer's infrastructure.

**Q: Is data encrypted at rest?**

Encryption at rest is provided by the chosen managed database (PostGIS), cache (Redis), and object-storage platforms. This is an operator configuration responsibility. Honua does not perform application-level encryption of stored data.

**Q: Where is geospatial data stored?**

All feature data, metadata, and spatial indexes are stored in PostgreSQL with PostGIS. Redis is used for multi-node cache coherence. No data is sent to Honua-operated infrastructure.

---

## 5. Logging, Monitoring, and Incident Response

**Q: What observability is available?**

- **Health checks**: `/healthz/live` (liveness) and `/healthz/ready` (readiness)
- **Metrics**: Prometheus endpoint (`/metrics`), JSON metrics snapshots, and admin-only diagnostics
- **Tracing**: OpenTelemetry support via `OTEL_*` environment variables, with OTLP export to any compatible collector
- **Alerting**: Cloud-native alerting presets for Kubernetes, AWS, and Azure are documented

See [Monitoring & Alerting](../devops/monitoring.md).

**Q: How are security vulnerabilities reported and handled?**

Honua maintains a vulnerability disclosure policy with defined response targets:

| Severity | Acknowledge | Initial triage | Patch target |
| --- | --- | --- | --- |
| Critical (RCE, auth bypass) | 1 business day | 3 business days | 7 days |
| High (privilege escalation) | 2 business days | 5 business days | 30 days |
| Medium (contained weakness) | 3 business days | 10 business days | 90 days |
| Low (hardening) | 5 business days | as capacity allows | as capacity allows |

Reports are submitted via GitHub's private vulnerability disclosure flow. See [SECURITY.md](../../SECURITY.md).

**Q: Is there an immutable audit log?**

Immutable audit logging is an Enterprise edition feature. Community and Pro editions rely on standard application logs and infrastructure-level audit trails (CloudTrail, Azure Activity Log, etc.).

---

## 6. Support, Patching, and Release Handling

**Q: How are updates and patches delivered?**

- Container images are published for each release via the project's container registry.
- Database migrations are forward-only and additive. Destructive schema changes are staged over two releases with rollback scripts.
- The [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md) defines compatibility contracts, deprecation lifecycle, and release channels (Stable, Preview, LTS).

**Q: What support is available?**

| Area | Community | Pro | Enterprise |
| --- | --- | --- | --- |
| Channel | Docs + GitHub | Email | Email + shared Slack/Teams |
| Response target | Best effort | 2 business days | Sev 1: 4 hours; Sev 2: next day |
| Security advisories | Public release notes | Direct notice | Direct notice |

See [Enterprise Procurement Readiness](ENTERPRISE_PROCUREMENT_READINESS.md) for the full support matrix.

**Q: Is there a long-term support (LTS) channel?**

Yes. The LTS channel provides 12+ months of security fixes only. See [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md).

---

## 7. Compliance and Legal

**Q: What compliance certifications does Honua hold?**

Honua does not currently hold SOC 2, FedRAMP, or equivalent certifications. These are not published in the repository. The current security posture is documented in [Security Configuration](../devops/security.md) and [SECURITY.md](../../SECURITY.md).

**Q: Is a penetration test summary available?**

Not published at this time. Contact Honua directly for the current status.

**Q: What is the software license?**

Honua Server is licensed under the Elastic License v2 (ELv2). Client SDKs and gRPC protocol definitions are Apache 2.0. See [ADR 0024](../contributor/adr/0024-open-core-edition-model.md) for the full edition and licensing model.

**Q: Are DPA and MSA templates available?**

DPA (Data Processing Agreement) and MSA (Master Service Agreement) templates are commercial process artifacts, not published in this repository. Contact Honua directly for procurement document requests and legal review timelines.

**Q: What is the data residency posture?**

Since Honua is customer-managed, data residency is determined entirely by the customer's infrastructure choices. No data leaves the customer's environment.

---

## Artifact Availability Summary

| Artifact | Available | Where |
| --- | --- | --- |
| Deployment and architecture guidance | Yes | [Infrastructure](../devops/infrastructure.md), [Architecture Diagrams](../contributor/ARCHITECTURE_DIAGRAMS.md) |
| Security configuration baseline | Yes | [Security Configuration](../devops/security.md) |
| Vulnerability disclosure policy | Yes | [SECURITY.md](../../SECURITY.md) |
| Versioning and upgrade policy | Yes | [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md) |
| Edition and licensing model | Yes | [ADR 0024](../contributor/adr/0024-open-core-edition-model.md) |
| CI security signals (CodeQL, container scan) | Yes | [README](../../README.md) badges |
| SOC 2 report | No | Contact Honua |
| FedRAMP package | No | Contact Honua |
| Penetration test summary | No | Contact Honua |
| DPA/MSA templates | No | Contact Honua |
