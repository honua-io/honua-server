# Console catalog discovery

The Console discovery registry can project published FeatureServer and MapServer
entries from the canonical GeoServices directory. Configure each workspace explicitly:

```json
{
  "Console": {
    "CatalogDiscovery": {
      "Workspaces": [
        {
          "Id": "planning",
          "TenantId": "city",
          "Namespace": "planning",
          "DisplayName": "Planning"
        }
      ]
    }
  }
}
```

The equivalent environment keys begin with
`Console__CatalogDiscovery__Workspaces__0__Id`, `TenantId`, `Namespace`, and
`DisplayName`. IDs, tenants and namespaces must contain 1–128 ASCII letters,
digits, dots, underscores or hyphens. Workspace IDs are case-insensitive as in the
existing registry; duplicate IDs fail startup. Tenant and namespace matching is
ordinal, matching canonical metadata identity and tenant visibility semantics.
There is no wildcard or implicit default mapping.

Requests still require administrative authorization. The resolved request tenant
must match the mapping. Services, publications and resources must each belong to
the mapped namespace and be visible to that tenant under the existing metadata
tenant rules. Unscoped metadata retains its existing shared visibility, within the
explicit namespace. Canonical lifecycle and service/resource access policy checks
still apply. A mapping grants no access and never changes the request tenant.
There is no separate per-workspace role grant: the existing administrative route
authorization and these tenant/publication policies remain the access checks.

Item handles include the workspace, tenant and namespace identity, so a handle
from another mapped workspace does not resolve even when service names match.
The endpoint URL still links to the canonical `/rest/services` directory. That URL
does not carry the workspace namespace boundary and can list other namespaces
authorized by the canonical directory policies. Mapping scopes the discovery
projection, not the linked serving routes or their authorization.

The existing registry, detail and item routes remain under
`/api/v1/console/catalog-endpoints/{workspaceId}`. An unmapped workspace, absent
tenant context, or tenant mismatch returns the existing 404 contract. A configured
workspace with no visible published entries returns an empty registry. Setting
`MultiTenancy:Enabled=false` leaves requests without a tenant and consequently does
not expose these mapped registries. Configure tenant resolution explicitly; do not
use workspace mappings to infer a tenant.

This first projection reports only real FeatureServer/MapServer directory entries,
with detail and item fields derived from those entries. It does not populate
unnamespaced legacy metadata, publish sample entries, expose scene registrations,
add other discovery dialects, or implement endpoint enable/auto-default mutations.
Those remain separate from the registry read contract.
