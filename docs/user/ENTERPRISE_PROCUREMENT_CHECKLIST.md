# Enterprise Procurement Checklist

This checklist helps teams assemble and verify a complete enterprise evaluation packet. It is designed so that the packet can be sent within 24 hours without bespoke document hunting.

Use this alongside [Enterprise Procurement Readiness](ENTERPRISE_PROCUREMENT_READINESS.md) and the [Security Questionnaire Starter](ENTERPRISE_SECURITY_QUESTIONNAIRE_STARTER.md).

---

## Pre-Send Checklist

### Technical Packet

- [ ] [Enterprise Procurement Readiness](ENTERPRISE_PROCUREMENT_READINESS.md) included (covers commercial posture, SLOs, support matrix, sizing profiles)
- [ ] [Security Questionnaire Starter](ENTERPRISE_SECURITY_QUESTIONNAIRE_STARTER.md) included (reusable Q&A for common security topics)
- [ ] [SECURITY.md](../../SECURITY.md) included (vulnerability disclosure and response policy)
- [ ] [Security Configuration](../devops/security.md) included (authentication, authorization, edge security baseline)
- [ ] [ADR 0024](../contributor/adr/0024-open-core-edition-model.md) included (edition boundaries, licensing model)

### Architecture and Deployment

- [ ] Reference architecture matches evaluator's target (Kubernetes HA, AWS/Azure managed, or Docker Compose)
- [ ] [Infrastructure & Deployment](../devops/infrastructure.md) guide included for deployment details and production checklist
- [ ] [Deployment Scenarios](../devops/DEPLOYMENT_SCENARIOS.md) included if evaluator needs sizing context
- [ ] [Architecture Diagrams](../contributor/ARCHITECTURE_DIAGRAMS.md) linked or exported for technical reviewers (system context, data flow, deployment topology)

### Versioning and Upgrade Policy

- [ ] [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md) included (compatibility contract, deprecation lifecycle, release channels, LTS terms)
- [ ] [MVP Compatibility Contract](MVP_COMPATIBILITY_CONTRACT.md) included if evaluator is assessing current-release scope and known limitations

### Support and SLA

- [ ] Support tier confirmed (Community, Pro, or Enterprise) and response targets documented
- [ ] Availability and performance objectives reviewed against evaluator's deployment model (see sizing profiles in [Procurement Readiness](ENTERPRISE_PROCUREMENT_READINESS.md))
- [ ] Monitoring and alerting capabilities referenced ([Monitoring & Alerting](../devops/monitoring.md))

### Legal and Procurement

- [ ] Software license (ELv2) confirmed and evaluator's legal team notified
- [ ] DPA/MSA request routed to Honua's commercial contact (not published in-repo)
- [ ] Data residency posture confirmed: customer-managed, no data leaves customer environment
- [ ] Pricing structure communicated: software subscription, support tier, professional services are separate line items

### Compliance and Security Gaps

- [ ] Evaluator notified of unpublished artifacts (SOC 2, FedRAMP, penetration test summary) with current status
- [ ] If evaluator requires compliance certifications, timeline expectations set via direct contact
- [ ] Security questionnaire responses customized from the [starter template](ENTERPRISE_SECURITY_QUESTIONNAIRE_STARTER.md) for evaluator-specific questions

---

## Post-Send Follow-Up

- [ ] Identify evaluator's specific security questionnaire format and map responses from the starter template
- [ ] Schedule architecture review call if evaluator selects Pro or Enterprise tier
- [ ] Track open legal/procurement questions (DPA, MSA, pricing) to resolution
- [ ] Confirm deployment scenario and sizing profile for pilot or proof-of-concept

---

## Packet Assembly Quick Reference

For the minimum viable packet, send these documents in order:

| # | Document | Purpose |
| --- | --- | --- |
| 1 | [Enterprise Procurement Readiness](ENTERPRISE_PROCUREMENT_READINESS.md) | Commercial posture, SLOs, support, sizing |
| 2 | [Security Questionnaire Starter](ENTERPRISE_SECURITY_QUESTIONNAIRE_STARTER.md) | Reusable security Q&A |
| 3 | [SECURITY.md](../../SECURITY.md) | Vulnerability disclosure policy |
| 4 | [Security Configuration](../devops/security.md) | Technical security controls |
| 5 | [ADR 0024](../contributor/adr/0024-open-core-edition-model.md) | Edition and licensing model |
| 6 | [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md) | Versioning, deprecation, LTS |
| 7 | [Infrastructure & Deployment](../devops/infrastructure.md) | Deployment details and production checklist |

For evaluators who need architecture detail, also include:

| # | Document | Purpose |
| --- | --- | --- |
| 8 | [Architecture Diagrams](../contributor/ARCHITECTURE_DIAGRAMS.md) | System context, data flow, deployment topology |
| 9 | [Deployment Scenarios](../devops/DEPLOYMENT_SCENARIOS.md) | Tier-specific deployment guidance |
