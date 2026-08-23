// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Processes;

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Geoprocessing;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods to register OGC API Processes adapter services.
/// </summary>
internal static class OgcProcessesServiceCollectionExtensions
{
    /// <summary>
    /// Registers OGC API Processes adapter services and configuration.
    /// </summary>
    public static IServiceCollection AddOgcProcesses(
        this IServiceCollection services,
        IConfiguration configuration,
        string? hostEnvironmentName = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<OgcProcessesOptions>(
            configuration.GetSection(OgcProcessesOptions.SectionName));

        if (OgcProcessesCiteEchoFixture.IsEnabled(configuration, hostEnvironmentName))
        {
            RegisterCiteEchoFixture(services);
        }

        return services;
    }

    private static void RegisterCiteEchoFixture(IServiceCollection services)
    {
        var catalogRegistration = services.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(IProcessCatalog));
        if (catalogRegistration?.ImplementationType == typeof(OgcProcessesCiteEchoCatalog))
        {
            return;
        }

        if (catalogRegistration?.ImplementationType == typeof(BuiltInProcessCatalog))
        {
            services.Remove(catalogRegistration);
        }
        else if (catalogRegistration is not null)
        {
            throw new InvalidOperationException(
                "The OGC API Processes certification profile cannot decorate a custom process catalog.");
        }

        services.TryAddSingleton<BuiltInProcessCatalog>();
        services.TryAddSingleton<IProcessCatalog, OgcProcessesCiteEchoCatalog>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProcessExecutor, OgcProcessesCiteEchoExecutor>());
    }
}
