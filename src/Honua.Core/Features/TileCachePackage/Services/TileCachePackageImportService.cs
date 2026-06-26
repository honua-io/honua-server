// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.TileCachePackage.Abstractions;
using Honua.Core.Features.TileCachePackage.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.TileCachePackage.Services;

/// <summary>
/// Imports Esri tile-cache packages into Honua's tile catalog and binds them to a
/// served tileset (#1269). Reads the documented package layout via
/// <see cref="ITileCachePackageReader"/> and persists tiles through the shared
/// <see cref="IOgcTileCacheSink"/> so re-imports converge on the same catalog rows.
/// </summary>
public sealed partial class TileCachePackageImportService : ITileCachePackageImportService
{
    private const string StyleIdentifier = "default";

    private readonly ITileCachePackageReader _reader;
    private readonly IOgcTileCacheSink _sink;
    private readonly ILogger<TileCachePackageImportService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TileCachePackageImportService"/> class.
    /// </summary>
    /// <param name="reader">Package reader.</param>
    /// <param name="sink">Tile catalog sink.</param>
    /// <param name="logger">Logger.</param>
    public TileCachePackageImportService(
        ITileCachePackageReader reader,
        IOgcTileCacheSink sink,
        ILogger<TileCachePackageImportService> logger)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<TileCachePackageImportResult> ImportAsync(
        TileCachePackageImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TilesetId))
        {
            throw new ArgumentException("A tileset identifier is required.", nameof(request));
        }

        if (!_reader.CanRead(request.FileName))
        {
            throw new ArgumentException(
                $"'{request.FileName}' is not a supported tile-cache package (.tpk/.tpkx/.vtpk).",
                nameof(request));
        }

        if (!request.Package.CanSeek)
        {
            throw new ArgumentException("Tile-cache package stream must be seekable.", nameof(request));
        }

        request.Package.Position = 0;
        var descriptor = await _reader.ReadDescriptorAsync(request.Package, cancellationToken).ConfigureAwait(false);

        var minZoom = Math.Max(request.MinZoom ?? descriptor.MinLevel, descriptor.MinLevel);
        var maxZoom = Math.Min(request.MaxZoom ?? descriptor.MaxLevel, descriptor.MaxLevel);
        if (maxZoom < minZoom)
        {
            throw new ArgumentException("Resolved maxZoom is below minZoom for this package.", nameof(request));
        }

        var dataType = descriptor.DataType == TileCacheDataType.Vector ? "vector" : "raster";
        var sourceUrl = $"tpk:{request.FileName}";

        Log.ImportStarted(_logger, request.TilesetId, descriptor.StorageFormat.ToString(), dataType, minZoom, maxZoom, request.DryRun);

        if (request.DryRun)
        {
            return new TileCachePackageImportResult
            {
                Success = true,
                TileCacheId = null,
                StorageFormat = descriptor.StorageFormat.ToString(),
                DataType = dataType,
                ContentType = descriptor.ContentType,
                TileMatrixSet = descriptor.TileMatrixSetIdentifier,
                MinZoom = minZoom,
                MaxZoom = maxZoom,
                TilesImported = 0,
                TilesSkipped = 0,
                DryRun = true
            };
        }

        var cacheId = await _sink.EnsureTileCacheAsync(
            new OgcTileCacheDescriptor
            {
                LayerIdentifier = request.TilesetId,
                TileMatrixSetIdentifier = descriptor.TileMatrixSetIdentifier,
                SourceServiceUrl = sourceUrl,
                TileFormat = descriptor.ContentType,
                StyleIdentifier = StyleIdentifier,
                MinZoom = minZoom,
                MaxZoom = maxZoom,
                DataType = dataType,
                Title = descriptor.Title
            },
            cancellationToken).ConfigureAwait(false);

        var imported = 0;
        var skipped = 0;

        request.Package.Position = 0;
        await foreach (var tile in _reader
            .ReadTilesAsync(request.Package, descriptor, minZoom, maxZoom, cancellationToken)
            .ConfigureAwait(false))
        {
            var status = await _sink.WriteTileAsync(
                new OgcTileCacheRecord
                {
                    TileCacheId = cacheId,
                    Z = tile.Z,
                    X = tile.X,
                    Y = tile.Y,
                    ContentType = descriptor.ContentType,
                    Content = tile.Content,
                    SourceUrl = sourceUrl
                },
                cancellationToken).ConfigureAwait(false);

            if (status == OgcTileCacheWriteStatus.Inserted)
            {
                imported++;
            }
            else
            {
                skipped++;
            }
        }

        Log.ImportCompleted(_logger, cacheId, imported, skipped);

        return new TileCachePackageImportResult
        {
            Success = true,
            TileCacheId = cacheId,
            StorageFormat = descriptor.StorageFormat.ToString(),
            DataType = dataType,
            ContentType = descriptor.ContentType,
            TileMatrixSet = descriptor.TileMatrixSetIdentifier,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            TilesImported = imported,
            TilesSkipped = skipped,
            DryRun = false
        };
    }

    private static partial class Log
    {
        [LoggerMessage(7970, LogLevel.Information,
            "Tile-cache package import started for tileset {TilesetId}: format {StorageFormat}, type {DataType}, zoom {MinZoom}-{MaxZoom}, dryRun {DryRun}")]
        public static partial void ImportStarted(ILogger logger, string tilesetId, string storageFormat, string dataType, int minZoom, int maxZoom, bool dryRun);

        [LoggerMessage(7971, LogLevel.Information,
            "Tile-cache package import completed for cache {TileCacheId}: {Imported} inserted, {Skipped} already present")]
        public static partial void ImportCompleted(ILogger logger, string tileCacheId, int imported, int skipped);
    }
}
