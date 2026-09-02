# Tenancy support

Honua 2026.1 is generally available for **single-tenant deployments**. A default
deployment does not require tenant configuration: tenant resolution and schema
routing are opt-in, and the configured default database schema remains in use when
they are disabled.

**Multi-tenant operation is Preview.** It remains available for explicitly configured
deployments, including the Honua demo area, but carries no 2026.1 GA operational,
SLA, per-tenant SLO, quota-as-product, or scale commitment. The tenant lifecycle API,
tenant usage export, tenant context selection, and schema-per-tenant routing are part
of this Preview surface.

Preview changes only the product claim. It does not lower the security floor:
**cross-tenant disclosure is a full-severity security defect** wherever tenant
boundaries exist. Authorization, tenant scoping, fail-closed behavior, and isolation
tests apply unchanged.

Multi-tenant schema routing is disabled by default. Enable it only for an intentional
Preview deployment with `MultiTenancy:SchemaRouting:Enabled=true`; configure tenant
resolution under `MultiTenancy`, and opt the Preview capability into manifest
discovery with `Capabilities:Experimental:admin.multi-tenancy:Enabled=true` (or the
global experimental/Preview switch used by that deployment).
