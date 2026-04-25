// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Extension methods to register OGC API Processes adapter services.
/// </summary>
internal static class OgcProcessesServiceCollectionExtensions
{
    /// <summary>
    /// Registers OGC API Processes adapter services and configuration.
    /// </summary>
    public static IServiceCollection AddOgcProcesses(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<OgcProcessesOptions>(
            configuration.GetSection(OgcProcessesOptions.SectionName));

        return services;
    }
}
