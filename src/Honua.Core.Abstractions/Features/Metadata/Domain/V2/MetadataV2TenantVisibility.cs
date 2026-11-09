// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

// A request with no tenant never sees a scoped entity, and a hidden publication is reported as not found.
public static class MetadataV2TenantVisibility
{
    public static bool IsVisibleToTenant(string? entityTenant, string? requestTenant)
    {
        if (string.IsNullOrWhiteSpace(entityTenant))
        {
            // Unscoped entity: visible to everyone (single-tenant back-compat).
            return true;
        }

        if (string.IsNullOrWhiteSpace(requestTenant))
        {
            // Scoped entity but no tenant resolved for the request: fail closed.
            return false;
        }

        return string.Equals(entityTenant, requestTenant, StringComparison.Ordinal);
    }

    public static bool IsVisibleToTenant(MetadataV2Object? resource, string? requestTenant)
        => resource is null || IsVisibleToTenant(resource.Metadata.Tenant, requestTenant);

    public static bool IsVisibleToTenant(MetadataV2Service? service, string? requestTenant)
        => service is null || IsVisibleToTenant(service.Metadata.Tenant, requestTenant);

    public static bool IsVisibleToTenant(
        MetadataV2Layer publication,
        MetadataV2Object? resource,
        MetadataV2Service? service,
        string? requestTenant)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return IsVisibleToTenant(publication.Metadata.Tenant, requestTenant)
            && IsVisibleToTenant(resource, requestTenant)
            && IsVisibleToTenant(service, requestTenant);
    }
}
