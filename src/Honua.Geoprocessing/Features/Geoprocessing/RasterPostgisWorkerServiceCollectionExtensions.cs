// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing.Execution;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Provider-neutral registration seam for the dedicated managed PostGIS raster worker.
/// </summary>
public static class RasterPostgisWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers one provider implementation behind the Core raster executor contract. The
    /// caller's provider package owns <typeparamref name="TExecutor"/>; Honua.Geoprocessing does
    /// not reference that package or its SQL/client types.
    /// </summary>
    public static IServiceCollection AddRasterProviderExecutor<TExecutor>(
        this IServiceCollection services)
        where TExecutor : class, IRasterProviderExecutor
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TExecutor>();
        services.AddSingleton<IRasterProviderExecutor>(sp =>
            sp.GetRequiredService<TExecutor>());
        return services;
    }

    /// <summary>
    /// Registers the exclusive <c>raster-postgis</c> job dispatcher. Call this only from the
    /// dedicated worker composition; the ordinary managed/web and native GDAL compositions must
    /// not register this dispatcher and therefore cannot claim its runtime profile.
    /// </summary>
    public static IServiceCollection AddRasterPostgisExecutionDispatcher(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IJobExecutor, RasterPostgisDispatchJobExecutor>());
        return services;
    }
}
