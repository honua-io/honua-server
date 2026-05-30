// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Filtering;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Protocols.Stac;

/// <summary>
/// Registers STAC API services in the dependency injection container.
/// </summary>
internal static class StacServiceCollectionExtensions
{
    /// <summary>
    /// Adds STAC API services.
    /// </summary>
    public static IServiceCollection AddStac(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<Cql2FilterProcessor>();
        services.AddScoped<StacSearchDependencies>();

        return services;
    }
}
