// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public sealed record OgcTileCacheExportRequest
{
    public string? JobId { get; init; }

    public required string ServiceUrl { get; init; }

    public string? Version { get; init; }

    public string[]? LayerIdentifiers { get; init; }

    public string[]? TileMatrixSetIdentifiers { get; init; }

    public string? TileFormat { get; init; }

    public string? StyleIdentifier { get; init; }

    public int MinZoom { get; init; } = OgcTileCacheExportSafetyLimits.DefaultMinZoom;

    public int MaxZoom { get; init; } = OgcTileCacheExportSafetyLimits.DefaultMaxZoom;

    public OgcTileCacheTileWindow? Window { get; init; }

    public bool DryRun { get; init; }

    public bool ApplyMode { get; init; }

    public int RequestTimeoutSeconds { get; init; } = 30;

    public bool AllowUnsafeLocalUrls { get; init; }

    public IReadOnlyCollection<string>? AllowedServiceHostSuffixes { get; init; }
}

public sealed record OgcTileCacheTileWindow
{
    public required int MinColumn { get; init; }

    public required int MaxColumn { get; init; }

    public required int MinRow { get; init; }

    public required int MaxRow { get; init; }
}

public static class OgcTileCacheExportSafetyLimits
{
    public const int DefaultMinZoom = 0;

    public const int DefaultMaxZoom = 2;

    // A zoom past this bound is reclassified as manual review, because one run would request too many tiles.
    public const int MaxAutomatedZoomLevel = 6;

    public const int MaxAutomatedTileCount = 1024;
}

public sealed record OgcTileCacheExportResult
{
    public required bool Success { get; init; }

    public bool WasDryRun { get; init; }

    public required string SourceServiceUrl { get; init; }

    public int TileSetsPlanned { get; init; }

    public int TileSetsExported { get; init; }

    public int TileSetsSkipped { get; init; }

    public int TilesPersisted { get; init; }

    public int TilesAlreadyPresent { get; init; }

    public int TilesFailed { get; init; }

    public IReadOnlyList<OgcTileCacheExportedTileSet> TileSets { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? ErrorMessage { get; init; }

    public required MigrationManifestArtifact Manifest { get; init; }

    public TimeSpan Duration { get; init; }
}

public sealed record OgcTileCacheExportedTileSet
{
    public required string LayerIdentifier { get; init; }

    public required string TileMatrixSetIdentifier { get; init; }

    public string? TargetTileCacheId { get; init; }

    public int MinZoom { get; init; }

    public int MaxZoom { get; init; }

    public int TilesPersisted { get; init; }

    public int TilesAlreadyPresent { get; init; }

    public int TilesFailed { get; init; }

    public required string Classification { get; init; }

    public required string Code { get; init; }

    public string[] Warnings { get; init; } = [];
}
