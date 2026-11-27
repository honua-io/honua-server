// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Caching;

// The output-cache store refuses a zero or negative lifetime when it stores an entry, which is after the response is sent;
// a bad OutputCache value has to stop the host at startup instead.
internal static class OutputCacheTtlValidation
{
    internal static void EnsureValid(OutputCacheTtlOptions ttl)
    {
        ArgumentNullException.ThrowIfNull(ttl);

        var invalid = new List<string>();
        ValidateCoreAndOgcMetadataDurations(ttl, invalid);
        ValidateOgcTilesDurations(ttl, invalid);
        ValidateDeliveryAndStacDurations(ttl, invalid);

        if (invalid.Count > 0)
        {
            throw new OptionsValidationException(OutputCacheTtlOptions.SectionName, typeof(OutputCacheTtlOptions), invalid);
        }
    }

    private static void ValidateCoreAndOgcMetadataDurations(OutputCacheTtlOptions ttl, List<string> invalid)
    {
        ValidateDuration(nameof(OutputCacheTtlOptions.ServiceMetadata), ttl.ServiceMetadata, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.ServiceDirectory), ttl.ServiceDirectory, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.LayerMetadata), ttl.LayerMetadata, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcLandingPage), ttl.OgcLandingPage, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcConformance), ttl.OgcConformance, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcCollections), ttl.OgcCollections, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcCollection), ttl.OgcCollection, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcOpenApi), ttl.OgcOpenApi, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcMapsLandingPage), ttl.OgcMapsLandingPage, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcMapsConformance), ttl.OgcMapsConformance, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcMapsOpenApi), ttl.OgcMapsOpenApi, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcStylesList), ttl.OgcStylesList, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcStylesConformance), ttl.OgcStylesConformance, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcStylesOpenApi), ttl.OgcStylesOpenApi, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcStylesStylesheet), ttl.OgcStylesStylesheet, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcStylesMetadata), ttl.OgcStylesMetadata, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcCoveragesLandingPage), ttl.OgcCoveragesLandingPage, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcCoveragesConformance), ttl.OgcCoveragesConformance, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcCoveragesOpenApi), ttl.OgcCoveragesOpenApi, invalid);
    }

    private static void ValidateOgcTilesDurations(OutputCacheTtlOptions ttl, List<string> invalid)
    {
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesLandingPage), ttl.OgcTilesLandingPage, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesConformance), ttl.OgcTilesConformance, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesOpenApi), ttl.OgcTilesOpenApi, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesCollections), ttl.OgcTilesCollections, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesCollection), ttl.OgcTilesCollection, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesTileMatrixSets), ttl.OgcTilesTileMatrixSets, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesTileMatrixSet), ttl.OgcTilesTileMatrixSet, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesTilesets), ttl.OgcTilesTilesets, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesDatasetTileset), ttl.OgcTilesDatasetTileset, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesCollectionTilesets), ttl.OgcTilesCollectionTilesets, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesCollectionTileset), ttl.OgcTilesCollectionTileset, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesDatasetTile), ttl.OgcTilesDatasetTile, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcTilesTile), ttl.OgcTilesTile, invalid);
    }

    private static void ValidateDeliveryAndStacDurations(OutputCacheTtlOptions ttl, List<string> invalid)
    {
        ValidateDuration(nameof(OutputCacheTtlOptions.MvtTile), ttl.MvtTile, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.TileJson), ttl.TileJson, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.TerrainMetadata), ttl.TerrainMetadata, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.TerrainTile), ttl.TerrainTile, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.SceneTilesetMetadata), ttl.SceneTilesetMetadata, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.SceneTileAsset), ttl.SceneTileAsset, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.LayerStyle), ttl.LayerStyle, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.ImageServerMetadata), ttl.ImageServerMetadata, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.OgcQueryables), ttl.OgcQueryables, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.StacCatalog), ttl.StacCatalog, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.StacCollections), ttl.StacCollections, invalid);
        ValidateDuration(nameof(OutputCacheTtlOptions.StacCollection), ttl.StacCollection, invalid);
    }

    private static void ValidateDuration(string propertyName, TimeSpan value, List<string> invalid)
    {
        if (value <= TimeSpan.Zero)
        {
            invalid.Add($"{OutputCacheTtlOptions.SectionName}:{propertyName} must be a positive duration.");
        }
    }
}
