---
type: reference
title: "Console catalog discovery"
description: "Explicit environment mappings for authorized published service discovery."
---
# Console catalog discovery

The Console discovery registry can project published FeatureServer and MapServer
entries from the canonical GeoServices directory. Configure each environment explicitly:

Use the supported [namespaced layer publication API](../publish/namespaced-layer-publication.md)
to publish retained PostGIS tables into the mapped tenant and namespace. The server
derives tenant ownership from the trusted request context; an environment mapping does
not authorize publication or create metadata on its own.

```json
{
  "Console": {
    "CatalogDiscovery": {
      "Environments": [
        {
          "Id": "production",
          "TenantId": "city",
          "Namespace": "planning",
          "DisplayName": "Production"
        }
      ]
    }
  }
}
```

The equivalent environment keys begin with
`Console__CatalogDiscovery__Environments__0__Id`, `TenantId`, `Namespace`, and
`DisplayName`. IDs, tenants and namespaces must contain 1–128 ASCII letters,
digits, dots, underscores or hyphens. Environment IDs are case-insensitive as in the
existing registry; duplicate IDs fail startup. Tenant and namespace matching is
ordinal, matching canonical metadata identity and tenant visibility semantics.
There is no wildcard or implicit default mapping.

Requests still require administrative authorization. The resolved request tenant
must match the mapping. Services, publications and resources must each belong to
the mapped namespace and be visible to that tenant under the existing metadata
tenant rules. Unscoped metadata retains its existing shared visibility, within the
explicit namespace. Canonical lifecycle and service/resource access policy checks
still apply. A mapping grants no access and never changes the request tenant.
There is no separate per-environment role grant: the existing administrative route
authorization and these tenant/publication policies remain the access checks.

Item handles include the environment, tenant and namespace identity, so a handle
from another mapped environment does not resolve even when service names match.
The endpoint URL still links to the canonical `/rest/services` directory. That URL
does not carry the environment namespace boundary and can list other namespaces
authorized by the canonical directory policies. Mapping scopes the discovery
projection, not the linked serving routes or their authorization.

The existing registry, detail and item routes remain under
`/api/v1/console/catalog-endpoints/{environmentId}`. An unmapped environment, absent
tenant context, or tenant mismatch returns the existing 404 contract. A configured
environment with no visible published entries returns an empty registry. Setting
`MultiTenancy:Enabled=false` (the default) resolves requests to
`MultiTenancy:DefaultTenantId` (`public` by default), exposing mapped registries for
that deployment tenant. Enable multi-tenancy explicitly to resolve tenants from
identity claims; do not use environment mappings to infer a tenant.

This first projection reports only real FeatureServer/MapServer directory entries,
with detail and item fields derived from those entries. It does not populate
unnamespaced legacy metadata, publish sample entries, expose scene registrations,
add other discovery dialects, or implement endpoint enable/auto-default mutations.
Those remain separate from the registry read contract.

Use Environment for this server connection and its discovery configuration. Folder groups Services; it does not define a tenant or permission boundary. Review discovery changes against this vocabulary and the registry wire-contract tests.

When upgrading an earlier evaluation build, rename the configuration collection to `Environments` and registry fields to `environmentId` and `environmentName`. Keep every existing mapping ID, tenant and namespace value unchanged: the rename preserves item handles and tenant boundaries. No retired configuration or wire aliases are provided.
