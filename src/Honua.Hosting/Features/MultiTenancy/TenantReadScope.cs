// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Infrastructure.MultiTenancy;

// Only disabled-tenancy middleware can authorize the upgrade alias. Claims,
// headers, enabled defaults and frozen execution bindings remain exact.
internal sealed class TenantReadScope
{
    internal bool IncludesLegacy { get; init; }
    internal string? TenantId { get; }
    internal string[] StorageKeys => IncludesLegacy && TenantId is not null
        ? [TenantId, string.Empty] : [TenantId ?? string.Empty];

    private TenantReadScope(string? tenantId)
    {
        TenantId = tenantId;
    }

    internal static TenantReadScope Exact(string? tenantId) => new(tenantId);

    internal static TenantReadScope For(ITenantContext? context, string? fallback = null)
        => new(context is null ? fallback : context.TenantId)
        {
            IncludesLegacy = context is RequestTenantContext { IncludesLegacyUnassigned: true }
        };

    internal bool Contains(string? tenantId)
        => string.Equals(tenantId, TenantId, StringComparison.Ordinal)
            || (IncludesLegacy && tenantId is null);
}
