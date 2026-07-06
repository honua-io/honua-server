// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> AdminEndpoints =>
    [
        new("GET", "/api/v1/admin/config"),
        new("POST", "/api/v1/admin/config"),
        new("PUT", "/api/v1/admin/config"),
        new("DELETE", "/api/v1/admin/config"),
        new("PATCH", "/api/v1/admin/config"),
        new("GET", "/api/v1/admin/configuration/discover"),
        new("GET", "/api/v1/admin/configuration/metadata"),
        new("GET", "/api/v1/admin/configuration/auto-documentation"),
        new("GET", "/api/v1/admin/configuration/secrets/validate"),
        new("GET", "/api/v1/admin/configuration/audit"),
        new("GET", "/api/v1/admin/configuration/summary"),
        new("GET", "/api/v1/admin/auth/config"),
        new("POST", "/api/v1/admin/auth/bearer"),
        new("POST", "/api/v1/admin/auth/logout"),
        new("GET", "/api/v1/admin/auth/session"),
        new("POST", "/api/v1/admin/auth/providers/{providerKey}/authorize-url"),
        new("POST", "/api/v1/admin/auth/providers/{providerKey}/token"),
        new("GET", "/api/v1/admin/auth/providers/{providerKey}/logout-url"),
        new("GET", "/api/v1/admin/api-keys"),
        new("POST", "/api/v1/admin/api-keys"),
        new("POST", "/api/v1/admin/api-keys/{id}/rotate"),
        new("POST", "/api/v1/admin/api-keys/{id}/revoke"),
        new("GET", "/api/v1/admin/api-keys/{id}/effective-permissions"),

        // Embed governance: keys, scoping, policy, analytics, usage (#1191).
        new("GET", "/api/v1/admin/embed/keys"),
        new("POST", "/api/v1/admin/embed/keys"),
        new("GET", "/api/v1/admin/embed/keys/{id}"),
        new("POST", "/api/v1/admin/embed/keys/{id}/rotate"),
        new("POST", "/api/v1/admin/embed/keys/{id}/revoke"),
        new("GET", "/api/v1/admin/embed/usage"),
        new("GET", "/api/v1/embed/policy"),
        new("POST", "/api/v1/embed/analytics"),

        // OAuth2 client registry + scope catalogue (ADR-0053 Increment 2, #1888).
        new("GET", "/api/v1/admin/oauth-clients"),
        new("POST", "/api/v1/admin/oauth-clients"),
        new("GET", "/api/v1/admin/oauth-clients/{id}"),
        new("DELETE", "/api/v1/admin/oauth-clients/{id}"),
        new("GET", "/api/v1/admin/oauth-scopes"),
        new("PUT", "/api/v1/admin/oauth-scopes"),
        new("DELETE", "/api/v1/admin/oauth-scopes/{scope}"),

        new("POST", "/oauth/token"),
        new("GET", "/api/v1/admin/openapi.json"),
        new("POST", "/api/v1/admin/openapi.json"),
        new("PUT", "/api/v1/admin/openapi.json"),
        new("DELETE", "/api/v1/admin/openapi.json"),
        new("PATCH", "/api/v1/admin/openapi.json"),
        new("GET", "/api/v1/capabilities/manifest"),
        new("GET", "/api/v1/admin/connections/{id}/tables"),
        new("POST", "/api/v1/admin/connections/{id}/tables"),
        new("PUT", "/api/v1/admin/connections/{id}/tables"),
        new("DELETE", "/api/v1/admin/connections/{id}/tables"),
        new("PATCH", "/api/v1/admin/connections/{id}/tables"),
        new("POST", "/api/v1/admin/connections/{id}/tables/validate"),
        new("GET", "/api/v1/admin/connections/tables"),
        new("GET", "/api/v1/admin/connections/{*path}"),
        new("POST", "/api/v1/admin/external-services/discover"),
        new("GET", "/api/v1/admin/connections/{id}/layers"),
        new("POST", "/api/v1/admin/connections/{id}/layers"),
        new("POST", "/api/v1/admin/connections/{id}/layers/extents/refresh"),
        new("POST", "/api/v1/admin/connections/{id}/layers/{layerId}/features/refresh"),
        new("PUT", "/api/v1/admin/connections/{id}/layers/{layerId}/enabled"),
        new("PUT", "/api/v1/admin/connections/{id}/layers/enabled"),
        new("GET", "/api/v1/admin/version"),
        new("GET", "/api/v1/admin/capabilities"),
        // v1 manifest endpoints removed in #1035 cutover (V2 admin UX is tracked under epic #1046).
        new("GET", "/api/v1/admin/metadata/environments/{environment}/inventory"),
        new("POST", "/api/v1/admin/metadata/environment-bindings/query"),
        new("POST", "/api/v1/admin/metadata/release-packages"),
        new("GET", "/api/v1/admin/metadata/release-packages"),
        new("GET", "/api/v1/admin/metadata/release-packages/{packageId}"),
        new("GET", "/api/v1/admin/metadata/release-packages/{packageId}/gitops-manifest"),
        new("GET", "/api/v1/admin/metadata/releases/{packageId}/operation"),
        new("POST", "/api/v1/admin/metadata/releases/operations"),
        new("POST", "/api/v1/admin/metadata/coordinated-releases/operations"),
        new("GET", "/api/v1/admin/metadata/coordinated-releases/{packageId}/operation"),
        new("POST", "/api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}"),
        new("POST", "/api/v1/admin/metadata/coordinated-releases/operations/{operationId}/rollback"),
        new("POST", "/api/v1/admin/metadata/prevalidate"),
        new("GET", "/api/v1/admin/deploy/preflight"),
        new("POST", "/api/v1/admin/deploy/plan"),
        new("POST", "/api/v1/admin/deploy/operations"),
        new("GET", "/api/v1/admin/deploy/operations/{operationId}"),
        new("POST", "/api/v1/admin/deploy/operations/{operationId}/submit"),
        new("POST", "/api/v1/admin/deploy/operations/{operationId}/promote"),
        new("POST", "/api/v1/admin/deploy/operations/{operationId}/rollback"),
        new("GET", "/api/v1/admin/proposals"),
        new("GET", "/api/v1/admin/proposals/{id}"),
        new("POST", "/api/v1/admin/proposals/{id}/approve"),
        new("POST", "/api/v1/admin/proposals/{id}/reject"),
        new("GET", "/api/v1/admin/services"),
        new("GET", "/api/v1/admin/services/{serviceName}/settings"),
        new("PUT", "/api/v1/admin/services/{serviceName}/protocols"),
        new("PUT", "/api/v1/admin/services/{serviceName}/mapserver"),
        new("PUT", "/api/v1/admin/services/{serviceName}/access-policy"),
        new("PUT", "/api/v1/admin/services/{serviceName}/timeinfo"),
        new("PUT", "/api/v1/admin/services/{serviceName}/layers/{layerId}/metadata"),

        // v1 admin named-replica management endpoints (#1167, slice 1)
        new("GET", "/api/v1/admin/services/{serviceId}/replicas"),
        new("GET", "/api/v1/admin/services/{serviceId}/replicas/{replicaId}"),

        // v1 admin replica conflict-review endpoints (#1167, slice 2)
        new("GET", "/api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts"),
        new("GET", "/api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}"),
        new("POST", "/api/v1/admin/services/{serviceId}/replicas/{replicaId}/conflicts/{conflictId}/resolve"),

        // v1 admin secure connection endpoints
        new("GET", "/api/v1/admin/connections"),
        new("GET", "/api/v1/admin/connections/{id}"),
        new("POST", "/api/v1/admin/connections"),
        new("POST", "/api/v1/admin/connections/test"),
        new("PUT", "/api/v1/admin/connections/{id}"),
        new("DELETE", "/api/v1/admin/connections/{id}"),
        new("POST", "/api/v1/admin/connections/{id}/test"),
        new("POST", "/api/v1/admin/connections/encryption/validate"),
        new("POST", "/api/v1/admin/connections/encryption/rotate-key"),

        // v1 admin client-certificate trust profile endpoints (#1171)
        new("GET", "/api/v1/admin/security/client-certificates/profiles"),
        new("POST", "/api/v1/admin/security/client-certificates/profiles"),
        new("GET", "/api/v1/admin/security/client-certificates/profiles/{profileId}"),
        new("PUT", "/api/v1/admin/security/client-certificates/profiles/{profileId}"),
        new("DELETE", "/api/v1/admin/security/client-certificates/profiles/{profileId}"),
        new("GET", "/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings"),
        new("POST", "/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings"),
        new("PUT", "/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings/{mappingId}"),
        new("DELETE", "/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings/{mappingId}"),
        new("GET", "/api/v1/admin/security/client-certificates/profiles/{profileId}/revocations"),
        new("POST", "/api/v1/admin/security/client-certificates/profiles/{profileId}/revocations"),
        new("DELETE", "/api/v1/admin/security/client-certificates/profiles/{profileId}/revocations/{revocationId}"),
        new("POST", "/api/v1/admin/security/client-certificates/validate"),

        // v1 admin license management endpoints (#511)
        new("GET", "/api/v1/admin/license"),
        new("POST", "/api/v1/admin/license"),
        new("GET", "/api/v1/admin/license/entitlements"),
        new("GET", "/api/v1/admin/license/capacity"),
        new("POST", "/api/v1/admin/license/capacity/surge"),

        // v1 platform admin endpoints (#513)
        new("GET", "/api/v1/admin/license/status"),
        new("GET", "/api/v1/admin/license/features"),
        new("POST", "/api/v1/admin/license/upload"),
        new("GET", "/api/v1/admin/identity/providers"),
        new("GET", "/api/v1/admin/identity/providers/{providerType}/test"),
        new("GET", "/api/v1/admin/cache/status"),
        new("POST", "/api/v1/admin/cache/invalidate"),
        new("GET", "/api/v1/admin/geocoding/providers"),
        new("GET", "/api/v1/admin/geoprocessing/tools/usage-ranking"),
        new("GET", "/api/v1/admin/features"),

        // v1 admin rate limit policy endpoints (#355)
        new("GET", "/api/v1/admin/rate-limits"),
        new("POST", "/api/v1/admin/rate-limits"),
        new("GET", "/api/v1/admin/rate-limits/{id}"),
        new("PUT", "/api/v1/admin/rate-limits/{id}"),
        new("DELETE", "/api/v1/admin/rate-limits/{id}"),
        new("GET", "/api/v1/admin/rate-limits/status"),

        // v1 admin tenant lifecycle endpoints (#2156)
        new("GET", "/api/v1/admin/tenants"),
        new("POST", "/api/v1/admin/tenants"),
        new("GET", "/api/v1/admin/tenants/usage"),
        new("GET", "/api/v1/admin/tenants/{tenantId}"),
        new("POST", "/api/v1/admin/tenants/{tenantId}/suspend"),
        new("POST", "/api/v1/admin/tenants/{tenantId}/resume"),
        new("DELETE", "/api/v1/admin/tenants/{tenantId}"),

        // v1 admin compliance endpoints (#352)
        new("GET", "/api/v1/admin/compliance/dashboard"),
        new("GET", "/api/v1/admin/compliance/report"),
        new("POST", "/api/v1/admin/compliance/residency/evaluate"),
        new("POST", "/api/v1/admin/compliance/encryption/rotate-key"),

        // v1 admin OIDC provider endpoints (#511)
        new("GET", "/api/v1/admin/oidc/providers"),
        new("POST", "/api/v1/admin/oidc/providers"),
        new("GET", "/api/v1/admin/oidc/providers/{id}"),
        new("PUT", "/api/v1/admin/oidc/providers/{id}"),
        new("DELETE", "/api/v1/admin/oidc/providers/{id}"),
        new("POST", "/api/v1/admin/oidc/providers/{id}/test"),

        // v1 admin user management endpoints (#511)
        new("GET", "/api/v1/admin/users"),
        new("GET", "/api/v1/admin/users/{id}"),
        new("PUT", "/api/v1/admin/users/{id}/roles"),
        new("DELETE", "/api/v1/admin/users/{id}"),
        new("GET", "/api/v1/admin/users/{id}/effective-permissions"),

        // v1 admin role management endpoints (#511)
        new("GET", "/api/v1/admin/roles"),
        new("POST", "/api/v1/admin/roles"),
        new("GET", "/api/v1/admin/roles/{id}"),
        new("PUT", "/api/v1/admin/roles/{id}"),
        new("DELETE", "/api/v1/admin/roles/{id}"),
        new("GET", "/api/v1/admin/roles/{id}/permissions"),
        new("PUT", "/api/v1/admin/roles/{id}/permissions"),

        // v1 admin row-level security (RLS) policy endpoints (#502)
        new("GET", "/api/v1/admin/rls-policies"),
        new("POST", "/api/v1/admin/rls-policies"),
        new("GET", "/api/v1/admin/rls-policies/{id}"),
        new("DELETE", "/api/v1/admin/rls-policies/{id}"),

        // v1 admin field-level security (column masking) policy endpoints (#1940)
        new("GET", "/api/v1/admin/field-mask-policies"),
        new("POST", "/api/v1/admin/field-mask-policies"),
        new("GET", "/api/v1/admin/field-mask-policies/{id}"),
        new("DELETE", "/api/v1/admin/field-mask-policies/{id}"),
    ];
}
