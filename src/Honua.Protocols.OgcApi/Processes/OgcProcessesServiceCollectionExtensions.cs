// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Processes;

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Migration.Services;
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
        services.AddHttpClient(OgcProcessInputReferenceHttpClient.Name, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(
                static () => OgcApiFeaturesMigrationScanner.CreatePinnedDnsHttpMessageHandler());

        var citeEchoEnabled = OgcProcessesCiteEchoFixture.IsEnabled(
            configuration,
            hostEnvironmentName);
        services.Replace(
            ServiceDescriptor.Singleton<IOgcProcessesCatalog>(serviceProvider =>
                new OgcProcessesCiteEchoCatalog(
                    serviceProvider.GetRequiredService<IProcessCatalog>(),
                    citeEchoEnabled)));

        if (citeEchoEnabled)
        {
            RegisterCiteEchoFixture(services);
        }

        return services;
    }

    private static void RegisterCiteEchoFixture(IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProcessExecutor, OgcProcessesCiteEchoExecutor>());
    }
}
