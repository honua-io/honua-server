---
type: reference
title: "Namespaced layer publication wire contract"
description: "HTTP contract for optional tenant-owned publication namespaces."
---
# Namespaced layer publication wire contract

This reference documents the additive `namespace` request field for client
implementers and interoperability tests. The endpoint is
`POST /api/v1/admin/connections/{connectionId}/layers`; its request schema is
`PublishLayerRequest` in the [admin OpenAPI contract](../developer/api-specs/admin-api.json).
The JSON body below is a contract example, not an operator command. The new field
must be projected into CLI, SDK, and MCP product surfaces before they can expose it.

<!-- wire-reference -->
```json
{
  "schema": "public",
  "table": "parcels",
  "layerName": "Parcels",
  "serviceName": "city-planning",
  "namespace": "planning",
  "primaryKey": "id",
  "geometryColumn": "geom"
}
```

Use an authenticated administrator credential and the existing operator approval
policy. The server derives ownership from its trusted tenant context (authenticated
tenant claims, an authorized tenant override, or the configured default tenant).
The request cannot select a tenant through a JSON field. A namespaced request with
no resolved tenant returns `400` before connection lookup or migrations.

Namespace identifiers preserve case and contain 1–128 ASCII letters, digits, `.`,
`_`, or `-`. A namespace groups metadata; it is not an authorization grant. It may
exist without a Console workspace. The new service, resource, FeatureServer and
STAC publications, and storage binding receive the same namespace and tenant.
Existing shared connection and style metadata retain their ownership and grouping;
a namespaced publication cannot reuse a dependency owned by another tenant.

Omitting `namespace` or sending `null` preserves legacy unscoped publication for
new services. An existing service must match both namespace and tenant exactly.
You can append another layer to a service in the same scope. You cannot adopt an
existing unscoped service, move it between namespaces or tenants, or append an
unscoped layer to a scoped service: these requests return `409`. Service names
remain globally unique; namespaces do not create duplicate service identities.

The legacy existing-layer linking operation does not accept scope intent and
rejects scoped sources or destinations. Publish another layer with the matching
scope instead. Listing, enablement, extent refresh, and materialized snapshot
refresh enforce tenant ownership. Administrators of another tenant receive `404`;
same-tenant administrators can operate across their own grouping namespaces.

To discover these services in Console, configure an explicit workspace mapping to
the same tenant and namespace as described in
[Console catalog discovery](console-catalog-discovery.md). Mapping scopes discovery;
the canonical service URL remains `/rest/services/{serviceName}/FeatureServer`,
where normal tenant and publication access policies still apply.
