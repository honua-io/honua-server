// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// DI registration for geoprocessing service dependencies.
/// </summary>
internal static class GeoprocessingServiceCollectionExtensions
{
    /// <summary>
    /// Registers geoprocessing service dependencies including the execution job store.
    /// </summary>
    public static IServiceCollection AddGeoprocessing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IExecutionJobStore, RedisExecutionJobStore>();

        return services;
    }
}
