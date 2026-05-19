// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Services;

namespace Honua.Server.Features.Protocols.Coverages.Multidimensional;

/// <summary>
/// Service collection extensions for cloud-optimized HDF5 / NetCDF4
/// multidimensional coverage support.
/// </summary>
internal static class MultidimensionalCoverageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the not-enabled default metadata reader. The catalog store
    /// implementation is registered by <c>AddPostgresRasterStore</c> in
    /// <c>Honua.Postgres</c>. See ADR-0039 for the reader strategy.
    /// </summary>
    public static IServiceCollection AddMultidimensionalCoverageServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IMultidimensionalCoverageMetadataReader, NotEnabledMultidimensionalCoverageMetadataReader>();
        return services;
    }
}
