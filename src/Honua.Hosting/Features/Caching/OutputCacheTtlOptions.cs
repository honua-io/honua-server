// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

public sealed class OutputCacheTtlOptions
{
    public const string SectionName = "OutputCache";

    public TimeSpan ServiceMetadata { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ServiceDirectory { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan LayerMetadata { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan OgcLandingPage { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan OgcConformance { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcCollections { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan OgcCollection { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan OgcOpenApi { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcTilesLandingPage { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan OgcTilesConformance { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcMapsLandingPage { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan OgcMapsConformance { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcMapsOpenApi { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcStylesList { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan OgcStylesConformance { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcStylesOpenApi { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcStylesStylesheet { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan OgcStylesMetadata { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan OgcCoveragesLandingPage { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan OgcCoveragesConformance { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcCoveragesOpenApi { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcTilesOpenApi { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcTilesCollections { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan OgcTilesCollection { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan OgcTilesTileMatrixSets { get; set; } = TimeSpan.FromHours(12);

    public TimeSpan OgcTilesTileMatrixSet { get; set; } = TimeSpan.FromHours(12);

    public TimeSpan OgcTilesTilesets { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan OgcTilesDatasetTileset { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan OgcTilesCollectionTilesets { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan OgcTilesCollectionTileset { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan OgcTilesDatasetTile { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OgcTilesTile { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan MvtTile { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan TileJson { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan TerrainMetadata { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan TerrainTile { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan SceneTilesetMetadata { get; set; } = TimeSpan.FromMinutes(10);

    // Scene binary payloads are immutable once written, which is why that lifetime is long.
    public TimeSpan SceneTileAsset { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan LayerStyle { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ImageServerMetadata { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan OgcQueryables { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan StacCatalog { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan StacCollections { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan StacCollection { get; set; } = TimeSpan.FromMinutes(10);
}
