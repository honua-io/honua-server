// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Federation;
using Honua.Core.Features.Federation.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Admin.Federation;

/// <summary>
/// Service registration for the federation admin surface and query planner (issue #341).
/// </summary>
public static class FederationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the federation query planner, the configuration-backed source registry, and
    /// binds <see cref="FederationSourceOptions"/> from the <c>Federation</c> section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFederationServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<FederationSourceOptions>(configuration.GetSection(FederationSourceOptions.SectionName));
        services.AddFederationCore();
        services.TryAddSingleton<IFederationSourceRegistry, ConfiguredFederationSourceRegistry>();

        return services;
    }
}
