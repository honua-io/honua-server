// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Configurable TTL values for output cache policies.
/// Bind to the "OutputCache" configuration section.
/// </summary>
public sealed class OutputCacheTtlOptions
{
    /// <summary>
    /// Configuration section name for appsettings.json binding.
    /// </summary>
    public const string SectionName = "OutputCache";

    /// <summary>
    /// Service metadata endpoint cache duration. Default: 5 minutes.
    /// </summary>
    public TimeSpan ServiceMetadata { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Service directory endpoint cache duration. Default: 2 minutes.
    /// </summary>
    public TimeSpan ServiceDirectory { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Layer metadata endpoint cache duration. Default: 5 minutes.
    /// </summary>
    public TimeSpan LayerMetadata { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// OGC landing page cache duration. Default: 30 minutes.
    /// </summary>
    public TimeSpan OgcLandingPage { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// OGC conformance endpoint cache duration. Default: 1 hour.
    /// </summary>
    public TimeSpan OgcConformance { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// OGC collections list cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan OgcCollections { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// OGC single collection cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan OgcCollection { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// OGC OpenAPI spec cache duration. Default: 1 hour.
    /// </summary>
    public TimeSpan OgcOpenApi { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// OGC Tiles landing page cache duration. Default: 30 minutes.
    /// </summary>
    public TimeSpan OgcTilesLandingPage { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// OGC Tiles conformance endpoint cache duration. Default: 1 hour.
    /// </summary>
    public TimeSpan OgcTilesConformance { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// OGC Maps landing page cache duration. Default: 30 minutes.
    /// </summary>
    public TimeSpan OgcMapsLandingPage { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// OGC Maps conformance endpoint cache duration. Default: 1 hour.
    /// </summary>
    public TimeSpan OgcMapsConformance { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// OGC Maps OpenAPI spec cache duration. Default: 1 hour.
    /// </summary>
    public TimeSpan OgcMapsOpenApi { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// OGC Tiles OpenAPI spec cache duration. Default: 1 hour.
    /// </summary>
    public TimeSpan OgcTilesOpenApi { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// OGC Tiles collections list cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan OgcTilesCollections { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// OGC Tiles single collection cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan OgcTilesCollection { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// OGC tile matrix sets list cache duration. Default: 12 hours.
    /// </summary>
    public TimeSpan OgcTilesTileMatrixSets { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// OGC tile matrix set definition cache duration. Default: 12 hours.
    /// </summary>
    public TimeSpan OgcTilesTileMatrixSet { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// OGC tilesets list cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan OgcTilesTilesets { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// OGC dataset tileset metadata cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan OgcTilesDatasetTileset { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// OGC collection tilesets list cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan OgcTilesCollectionTilesets { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// OGC collection tileset metadata cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan OgcTilesCollectionTileset { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// OGC dataset tile cache duration. Default: 1 hour.
    /// </summary>
    public TimeSpan OgcTilesDatasetTile { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// OGC individual tile cache duration. Default: 1 hour.
    /// </summary>
    public TimeSpan OgcTilesTile { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// MVT tile cache duration. Default: 1 hour.
    /// </summary>
    public TimeSpan MvtTile { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// TileJSON metadata cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan TileJson { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Layer style cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan LayerStyle { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Image Server metadata cache duration. Default: 5 minutes.
    /// </summary>
    public TimeSpan ImageServerMetadata { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// OGC queryables cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan OgcQueryables { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// STAC catalog (root landing page) cache duration. Default: 30 minutes.
    /// </summary>
    public TimeSpan StacCatalog { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// STAC collections list cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan StacCollections { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// STAC single collection cache duration. Default: 10 minutes.
    /// </summary>
    public TimeSpan StacCollection { get; set; } = TimeSpan.FromMinutes(10);
}
