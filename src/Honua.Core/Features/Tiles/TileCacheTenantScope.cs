// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Resolves the ownership scope stored beside generated tile-cache entries.
/// </summary>
public static class TileCacheTenantScope
{
    /// <summary>
    /// Prefers the routed database schema and otherwise uses the request tenant, matching the
    /// ImageServer write path when schema routing is disabled.
    /// </summary>
    public static string? Resolve(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return (serviceProvider.GetService(typeof(ISchemaContext)) as ISchemaContext)?.CurrentSchema
            ?? (serviceProvider.GetService(typeof(ITenantContext)) as ITenantContext)?.TenantId;
    }
}
