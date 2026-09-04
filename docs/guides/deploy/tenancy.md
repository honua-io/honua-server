# Tenancy support

Honua 2026.1 is generally available for **single-tenant deployments**. A default
deployment does not require tenant configuration: tenant resolution and schema
routing are opt-in, and the configured default database schema remains in use when
they are disabled.

**Multi-tenant operation is Preview/trial only and is not for production.** It may be
enabled only for a non-production demo or trial evaluation. Do not connect it to
customer production data. The tenant lifecycle API, tenant usage export, tenant
context selection, and schema-per-tenant routing are all part of this Preview surface.

This Preview carries no GA commitment and no availability, performance, durability,
SLA, per-tenant SLO, quota-as-product, or scale claim. Honua does not provide SaaS,
hosting, or a managed service; any Preview deployment runs in infrastructure selected
and controlled by the evaluator.

Preview changes only the product claim. It does not lower the security floor:
**cross-tenant disclosure is a full-severity security defect** wherever tenant
boundaries exist. Authorization, tenant scoping, fail-closed behavior, and isolation
tests apply unchanged.

Multi-tenant schema routing is disabled by default. Enable it only for an intentional
non-production Preview/trial evaluation with `MultiTenancy:SchemaRouting:Enabled=true`; configure tenant
resolution under `MultiTenancy`, and opt the Preview capability into manifest
discovery with `Capabilities:Experimental:admin.multi-tenancy:Enabled=true` (or the
global experimental/Preview switch used by that deployment).
