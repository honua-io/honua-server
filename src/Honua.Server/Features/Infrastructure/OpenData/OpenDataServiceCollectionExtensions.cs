// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.OpenData.Abstractions;
using Honua.Server.Features.Infrastructure.OpenData.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Infrastructure.OpenData;

/// <summary>
/// Dependency registration for the open-data publication slice.
/// </summary>
internal static class OpenDataServiceCollectionExtensions
{
    /// <summary>
    /// Adds open-data publication services.
    /// </summary>
    public static IServiceCollection AddOpenDataPublication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<OpenDataPublicationOptions>(
            configuration.GetSection(OpenDataPublicationOptions.SectionName));

        var openDataEnabled = configuration.GetValue<bool>(
            $"{OpenDataPublicationOptions.SectionName}:{nameof(OpenDataPublicationOptions.Enabled)}");
        var useInMemoryStore = configuration.GetValue<bool>(
            $"{OpenDataPublicationOptions.SectionName}:{nameof(OpenDataPublicationOptions.UseInMemoryStore)}");
        if (useInMemoryStore || !openDataEnabled)
        {
            services.TryAddSingleton<IOpenDataStore, InMemoryOpenDataStore>();
        }
        else if (UsesNonPostgresProvider(configuration) &&
                 !services.Any(static descriptor => descriptor.ServiceType == typeof(IOpenDataStore)))
        {
            throw new InvalidOperationException(
                "OpenData:Enabled=true requires a registered IOpenDataStore or OpenData:UseInMemoryStore=true.");
        }

        services.TryAddScoped<OpenDataPublicationService>();
        return services;
    }

    private static bool UsesNonPostgresProvider(IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("DataSource:Provider");
        return !string.IsNullOrWhiteSpace(provider) &&
               !provider.Equals("postgres", StringComparison.OrdinalIgnoreCase) &&
               !provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase) &&
               !provider.Equals("postgis", StringComparison.OrdinalIgnoreCase);
    }
}
