// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Protocols.Terrain;

internal static class TerrainServiceCollectionExtensions
{
    public static IServiceCollection AddTerrain(this IServiceCollection services)
    {
        services.TryAddScoped<ITerrainTileService, UnsupportedTerrainTileService>();
        return services;
    }

    private sealed class UnsupportedTerrainTileService : ITerrainTileService
    {
        public Task<TerrainDatasetMetadata?> GetDatasetMetadataAsync(
            int layerId,
            RasterMergeStrategy mergeStrategy,
            CancellationToken cancellationToken = default)
            => Task.FromResult<TerrainDatasetMetadata?>(null);

        public Task<TerrainTileResult> GetTerrainRgbTileAsync(
            TerrainTileRequest request,
            CancellationToken cancellationToken = default)
            => throw new TerrainTileException(
                TerrainTileFailureKind.SourceUnavailable,
                "No terrain tile provider is registered for the active data source.");
    }
}
