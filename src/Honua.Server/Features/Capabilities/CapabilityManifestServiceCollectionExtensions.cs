// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Features.Capabilities;

internal static class CapabilityManifestServiceCollectionExtensions
{
    public static IServiceCollection AddCapabilityManifest(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        // #2339 (T2): bind the experimental feature-flag options so the manifest
        // snapshot can carry them. T4 (#2340) is where the manifest acts on them.
        services.AddCapabilityFlagOptions(configuration);
        services.AddSingleton<CapabilityManifestOptionsSnapshot>();
        services.AddScoped<ICapabilityManifestService, CapabilityManifestService>();
        return services;
    }
}
