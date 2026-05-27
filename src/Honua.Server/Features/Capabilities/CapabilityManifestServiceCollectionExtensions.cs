// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Capabilities;

internal static class CapabilityManifestServiceCollectionExtensions
{
    public static IServiceCollection AddCapabilityManifest(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ICapabilityManifestService, CapabilityManifestService>();
        return services;
    }
}
