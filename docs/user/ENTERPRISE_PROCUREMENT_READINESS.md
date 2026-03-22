# Enterprise Procurement Readiness

This document is the canonical procurement-facing package for Honua Server. It consolidates commercial posture, support commitments, deployment reference patterns, and the security/compliance artifacts currently published in this repository.

## Commercial and Licensing Posture

- **Open-core model**: Honua follows the edition boundary in [ADR 0024](../contributor/adr/0024-open-core-edition-model.md).
- **Community edition**: free to use, deploy, and modify under [ELv2](../../LICENSE).
- **Pro and Enterprise editions**: commercial subscriptions with runtime license-key enforcement for paid capabilities; deployment surfaces themselves are not paywalled.
- **Deployment pricing boundary**: customer cloud spend, managed database spend, and optional professional services are separate from the software subscription unless explicitly bundled in an order form.
- **Transparent pricing posture**: Honua should separate quotes into `software subscription`, `support tier`, and `professional services`. Community pricing is public (`free`). Commercial package structure is public even when final enterprise pricing remains quote-based.

## Shared Responsibility Summary

Honua Server is primarily a customer-managed deployment model today.

- Honua provides the application, container images, Helm chart, deployment guidance, and support commitments defined here.
- The customer owns the cloud account, network controls, TLS certificates, identity-provider configuration, backup operations, and infrastructure availability unless a separate services agreement says otherwise.
- Edge controls such as WAF, IP allowlists, and rate limiting are operator responsibilities and are documented in [Security Configuration](../devops/security.md).

## Availability, Performance, and Durability Objectives

These objectives assume the recommended reference architecture, indexed spatial
data, managed PostGIS, and normal operating conditions. They are planning
targets for procurement and support discussions, not an unconditional guarantee
for undersized or misconfigured customer infrastructure.

| Objective area | Community | Pro | Enterprise |
| --- | --- | --- | --- |
| Availability objective | best effort, no uptime SLA | `99.9%` monthly for the Honua application layer on the recommended single-region HA footprint | `99.95%` monthly for the Honua application layer on the recommended HA or multi-region footprint |
| Query latency objective | no SLA | indexed query `P50 <= 250 ms`, `P95 <= 1.5 s`, `P99 <= 4 s` | indexed query `P50 <= 200 ms`, `P95 <= 1.0 s`, `P99 <= 3 s` on dedicated enterprise sizing |
| Map export objective | no SLA | `P95 <= 10 s` for standard map exports on the recommended footprint | `P95 <= 8 s` on dedicated enterprise sizing |
| Tile generation objective | no SLA | `P95 <= 1.5 s` for warm tile generation and `<= 3 s` for cold generation | `P95 <= 1.0 s` warm and `<= 2.5 s` cold on dedicated enterprise sizing |
| Durability baseline | operator-managed only | managed PostGIS backups at least daily, target `RPO <= 24h` | PITR-enabled managed PostGIS, target `RPO <= 1h`, documented restore exercise expectation |
| Restore expectation | no SLA | best-effort guidance during support engagement | target `RTO <= 4h` for the recommended HA footprint, subject to customer cloud controls and runbooks |

Performance notes:

- Query latency targets assume selective or indexed spatial/attribute filters and reasonable result windows.
- Export and tile targets assume default production guidance from [Infrastructure](../devops/infrastructure.md) and [Operations](../devops/operations.md).
- Community deployments can outperform these numbers, but Honua does not offer a contractual SLA on the free tier.
- For reproducible benchmark evidence and methodology, see the [Benchmark Results](BENCHMARK_RESULTS.md) proof pack.

## Reference Architecture Summaries

| Reference pattern | Recommended use | Core components | Availability target | Primary pointers |
| --- | --- | --- | --- | --- |
| `Kubernetes HA` | default production deployment | ingress/WAF, `2+` Honua replicas, managed PostGIS, optional Redis for multi-node caching | `99.9%` monthly application availability target for the Honua service layer | [Infrastructure](../devops/infrastructure.md), [Helm chart](../../infrastructure/helm/honua/README.md) |
| `AWS/Azure managed cloud` | production when customer standardizes on Terraform-managed infrastructure | managed edge, managed PostGIS, Honua containers or serverless targets, post-apply validation hooks | `99.5%` monthly application availability target for the Honua service layer | [Infrastructure](../devops/infrastructure.md), dedicated `honua-terraform` repository |
| `Single-node / Docker Compose` | evaluation, demos, and non-critical internal use | single Honua instance, local PostGIS, optional Redis/MinIO | no production SLO or SLA | [Infrastructure](../devops/infrastructure.md), [Docker Compose sample](../devops/docker-compose.md) |

Notes:

- The availability targets above are reference objectives for customer-managed deployments built from Honua's recommended architecture. They are not a managed-SaaS uptime guarantee.
- Redis is recommended for multi-node coherence and scale; PostGIS is required in every deployment model.
- TLS termination, WAF, and network restrictions are expected at the ingress or cloud edge, not inside the Honua process.

## Reference Architecture Sizing Profiles

These profiles are starting points for procurement and capacity planning. They
should be validated with the customer dataset, query patterns, and export/tile
mix before production sign-off.

| Profile | Expected usage | App sizing baseline | PostGIS baseline | Redis baseline | Deployment notes |
| --- | --- | --- | --- | --- | --- |
| `Small` | `1-10` users, `<1M` features, pilot or departmental workload | `1 x 2 vCPU / 4 GiB` Honua node | `2 vCPU / 8 GiB` managed PostGIS or hardened single-node PostGIS | optional | Docker Compose or single-node Kubernetes only; no production HA commitment |
| `Medium` | `10-100` users, `1M-100M` features, production single-region | `2-3 x 2-4 vCPU / 8 GiB` Honua replicas behind ingress | `4-8 vCPU / 16-32 GiB` managed PostGIS with automated backups | `1-2 GiB` managed Redis or equivalent | Preferred Pro footprint; use Helm or managed container orchestration |
| `Large` | `100+` users, `100M+` features, enterprise or multi-team workload | `4+ x 4-8 vCPU / 16 GiB` Honua replicas, optionally split across regions | `8-16 vCPU / 32-64 GiB` HA managed PostGIS with replica/failover strategy | managed Redis with HA or clustered cache | Preferred Enterprise footprint; requires observability, failover runbooks, and restore drills |

Cloud-specific deployment guidance:

- `AWS ECS/EKS`: pair ALB/WAF with RDS for PostgreSQL + PostGIS and ElastiCache for Redis. Use the dedicated `honua-terraform` repository for Terraform examples and this repo's [Helm chart](../../infrastructure/helm/honua/README.md) for Kubernetes packaging.
- `Azure ACA/AKS`: pair Front Door or Application Gateway with Azure Database for PostgreSQL Flexible Server and Azure Cache for Redis. Use the dedicated `honua-terraform` repository for Terraform examples and this repo's [Helm chart](../../infrastructure/helm/honua/README.md) for Kubernetes packaging.
- Serverless targets can be part of a delivery architecture, but the default procurement posture for uptime-sensitive deployments remains containerized Honua plus managed PostGIS.

## Support, SLA, and SLO Matrix

Severity definitions:

- `Sev 1`: production outage, active security event, or no-workaround blocker
- `Sev 2`: major degradation with workaround
- `Sev 3`: non-blocking defect or implementation question

| Area | Community | Pro | Enterprise |
| --- | --- | --- | --- |
| Support channel | docs and GitHub issue flow | email support | email support plus shared Slack/Teams incident channel by agreement |
| Coverage window | best effort | business hours | business hours with coordinated incident response for `Sev 1` |
| Initial response target | no SLA | within `2` business days | `Sev 1`: within `4` business hours; `Sev 2`: next business day; `Sev 3`: within `2` business days |
| Status update cadence | no SLA | reasonable-effort during active case | at least each business day for active `Sev 1` and `Sev 2` cases |
| Architecture review | self-service docs | available as scoped services | included by agreement |
| Security advisory notice | public release notes | public release notes and direct notice for contracted contacts | public release notes and direct notice for contracted contacts |

Additional service-level notes:

- Pro support aligns to the current paid-tier commitment in ADR 0024: initial response within `48` business hours.
- Enterprise support aligns to the current paid-tier commitment in ADR 0024: critical-incident response within `4` business hours.
- Availability objectives depend on the chosen architecture, not only on edition. Use the reference architecture table above when documenting customer commitments.

## Security and Vulnerability Response

- Vulnerability disclosure and response policy: [root SECURITY.md](../../SECURITY.md)
- Operator security configuration: [Security Configuration](../devops/security.md)
- Deployment hardening and validation flow: [Infrastructure](../devops/infrastructure.md)

Current published security posture:

- admin access supports API-key authentication and optional OIDC
- TLS, WAF, forwarded-header trust, and rate limiting are enforced at the edge
- production guidance recommends managed PostGIS and Redis for multi-node deployments
- health checks, metrics, and tracing are available for runtime monitoring

## Data Flow and Authentication Architecture Summary

Current procurement-facing architecture summary:

- request flow: `client -> ingress/WAF/TLS termination -> Honua Server -> PostGIS (+ optional Redis / object storage)`
- admin authentication: `X-API-Key` by default, optional OIDC bearer-token flow for browser-based admin access
- runtime authorization: admin surfaces require admin credentials; geospatial data access remains governed by service and layer access controls
- encryption in transit: expected at the ingress or cloud edge and between managed services where the customer enables it
- encryption at rest: provided by the chosen PostGIS, Redis, and object-storage platform configuration

These controls are described in more detail in [Security Configuration](../devops/security.md) and [Infrastructure](../devops/infrastructure.md).

## Published Security and Compliance Artifact Summary

This table describes what is published in-repo today. It is intentionally explicit so procurement reviews do not over-assume maturity that is not yet documented.

| Artifact | Status today | Source |
| --- | --- | --- |
| Edition and licensing boundary | published | [ADR 0024](../contributor/adr/0024-open-core-edition-model.md), [LICENSE](../../LICENSE) |
| Deployment and reference architecture guidance | published | [Infrastructure](../devops/infrastructure.md), [Helm chart](../../infrastructure/helm/honua/README.md) |
| Security configuration baseline | published | [Security Configuration](../devops/security.md) |
| Vulnerability disclosure and security response policy | published | [root SECURITY.md](../../SECURITY.md) |
| Versioning and upgrade expectations | published | [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md) |
| MVP scope and known limitations | published | [MVP Compatibility Contract](MVP_COMPATIBILITY_CONTRACT.md) |
| CI-visible security signals | published | [README](../../README.md) badges for `CodeQL` and `Container Security` |
| SOC 2 report | not published in this repository | n/a |
| FedRAMP package | not published in this repository | n/a |
| Penetration-test summary | not published in this repository | n/a |
| Formal DPA/MSA templates | commercial process artifact, not published in this repository | n/a |

## Recommended Procurement Packet Contents

For the minimum current-state procurement packet, send:

1. this document
2. [Security Questionnaire Starter](ENTERPRISE_SECURITY_QUESTIONNAIRE_STARTER.md)
3. [root SECURITY.md](../../SECURITY.md)
4. [Security Configuration](../devops/security.md)
5. [ADR 0024](../contributor/adr/0024-open-core-edition-model.md)
6. [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md)
7. [Infrastructure](../devops/infrastructure.md)

That set covers support commitments, deployment model, licensing posture, security reporting, versioning policy, and currently published technical controls without implying unpublished certifications.

Use the [Enterprise Procurement Checklist](ENTERPRISE_PROCUREMENT_CHECKLIST.md) to verify packet completeness before sending.
