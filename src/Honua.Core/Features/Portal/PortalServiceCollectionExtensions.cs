// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Services;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Portal.Abstractions;
using Honua.Core.Features.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Portal;

public static class PortalServiceCollectionExtensions
{
    public static IServiceCollection AddPortalItemProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IEsriConstructCapabilityRegistry>(
            _ => new GeoServicesConstructCapabilityRegistry(GeoServicesConstructCapabilityRegistry.BuiltInDescriptors));
        services.TryAddSingleton<IPortalItemProjector, PortalItemProjector>();
        services.TryAddSingleton<PublishedMetadataProjection>();
        return services;
    }
}
