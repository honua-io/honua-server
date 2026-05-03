// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Protocols.Elevation;

internal static class ElevationServiceCollectionExtensions
{
    public static IServiceCollection AddElevation(this IServiceCollection services)
    {
        services.TryAddScoped<IElevationService, UnsupportedElevationService>();
        return services;
    }

    private sealed class UnsupportedElevationService : IElevationService
    {
        public Task<ElevationPointResult> QueryPointAsync(
            int layerId,
            double x,
            double y,
            int? srid,
            RasterMergeStrategy mergeStrategy,
            CancellationToken cancellationToken = default)
            => throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "No elevation provider is registered for the active data source.");

        public Task<ElevationProfileResult> QueryProfileAsync(
            int layerId,
            byte[] lineWkb,
            int lineSrid,
            ProfileSamplingOptions options,
            RasterMergeStrategy mergeStrategy,
            CancellationToken cancellationToken = default)
            => throw new ElevationQueryException(
                ElevationFailureKind.SourceUnavailable,
                "No elevation provider is registered for the active data source.");
    }
}
