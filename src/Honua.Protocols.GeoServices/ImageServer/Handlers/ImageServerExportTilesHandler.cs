// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Tiles;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Microsoft.AspNetCore.Http;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using CoreSpatialReference = Honua.Core.Features.Shared.Models.SpatialReference;
using static Honua.Infrastructure.Rendering.RasterMapRenderingPipeline;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for storage-backed ImageServer tile archive export operations.
/// </summary>
internal sealed class ImageServerExportTilesHandler
{
    private const string ExportTilesContentType = "application/zip";
    private const string ExportTilesStorageFormat = "zip";
    private const string ExportTilesTpkStorageFormat = "tpk";
    private const string ExportTilesTpkContentType = "application/octet-stream";
    private const int ExportTilesTileSizeBytesEstimate = 32 * 1024;

    private readonly record struct ExportTileCoordinate(int Z, int X, int Y);

    private sealed record ExportTilesPlan(
        int LayerId,
        RasterMergeStrategy MergeStrategy,
        ExportTileCoordinate[] Tiles,
        double[] Bounds,
        int MinZoom,
        int MaxZoom,
        bool ExceededTransferLimit,
        RasterFormat RasterFormat,
        string TileExtension,
        DateTimeOffset? Timestamp,
        bool TilePackage);

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerExportTilesHandler> _logger;
    private readonly ICloudFileStorage? _storage;
    private readonly ITileExportJobService? _tileExportJobService;

    public ImageServerExportTilesHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ILogger<ImageServerExportTilesHandler> logger,
        ICloudFileStorage? storage = null,
        ITileExportJobService? tileExportJobService = null)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storage = storage;
        _tileExportJobService = tileExportJobService;
    }

    /// <summary>
    /// Estimates a storage-backed ImageServer tile archive request.
    /// </summary>
    public async Task<IResult> EstimateAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        var (plan, error) = await TryBuildExportTilesPlanAsync(context, layerId, values, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var estimatedSizeBytes = checked(plan!.Tiles.LongLength * ExportTilesTileSizeBytesEstimate);
        var response = new ImageServerExportTilesEstimateResponse
        {
            TileCount = plan.Tiles.LongLength,
            Size = estimatedSizeBytes,
            SizeUnit = "bytes",
            EstimatedSizeBytes = estimatedSizeBytes,
            MinZoom = plan.MinZoom,
            MaxZoom = plan.MaxZoom,
            TilePackage = plan.TilePackage,
            StorageFormat = plan.TilePackage ? ExportTilesTpkStorageFormat : ExportTilesStorageFormat,
            ContentType = plan.TilePackage ? ExportTilesTpkContentType : ExportTilesContentType,
            ExceededTransferLimit = plan.ExceededTransferLimit,
        };

        return Results.Json(response, ImageServerJsonContext.Default.ImageServerExportTilesEstimateResponse);
    }

    /// <summary>
    /// Exports a bounded ImageServer tile archive into configured cloud file storage.
    /// </summary>
    public async Task<IResult> ExportTilesAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "export-tiles",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "exportTiles");

        // Explicit Compact Cache V2 / TPKX negotiation submits a durable asynchronous job and
        // returns an ArcGIS { jobId, jobStatus } envelope. All other requests keep the existing
        // synchronous flat-ZIP / exploded-TPK behavior byte-for-byte.
        if (IsCompactV2Requested(values) && _tileExportJobService is not null)
        {
            return await SubmitDurableExportAsync(context, layerId, values, cancellationToken).ConfigureAwait(false);
        }

        var (plan, error) = await TryBuildExportTilesPlanAsync(context, layerId, values, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (_storage is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Cloud file storage is not configured. exportTiles requires storage-backed artifacts.");
        }

        ImageServerLog.ExportTilesRequested(_logger, layerId, plan!.Tiles.Length);
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "ImageServer exportTiles",
            ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.ImageServer);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "exportTiles");
        activity?.SetTag("honua.imageserver.tile_count", plan.Tiles.Length);

        var stopwatch = Stopwatch.StartNew();
        await using var archiveStream = new MemoryStream();
        var exportedTileCount = 0;

        try
        {
            // Render each populated tile once; flatten into a {z}/{x}/{y}.ext ZIP
            // or package into the Esri exploded-cache TPK layout via the shared writer.
            async IAsyncEnumerable<TilePackageWriter.PackagedTile> ReadTilesAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
            {
                foreach (var tile in plan.Tiles)
                {
                    token.ThrowIfCancellationRequested();
                    var tileGeometry = CreateTileEnvelope(tile.Z, tile.Y, tile.X);
                    var selectedRasters = await _rasterStore.QueryRastersAsync(
                        layerId,
                        new RasterSelectionQuery
                        {
                            Geometry = tileGeometry,
                            GeometrySrid = 3857,
                            Timestamp = plan.Timestamp,
                        },
                        token).ConfigureAwait(false);

                    if (selectedRasters.Length == 0)
                    {
                        continue;
                    }

                    var tileResult = selectedRasters.Length == 1
                        ? await _rasterStore.GetImageTileAsync(
                            layerId,
                            selectedRasters[0].Id,
                            tile.Z,
                            tile.Y,
                            tile.X,
                            plan.RasterFormat,
                            token).ConfigureAwait(false)
                        : await _rasterStore.GetMosaicImageTileAsync(
                            layerId,
                            selectedRasters.Select(static raster => raster.Id).ToArray(),
                            plan.MergeStrategy,
                            tile.Z,
                            tile.Y,
                            tile.X,
                            plan.RasterFormat,
                            token).ConfigureAwait(false);

                    if (tileResult is null)
                    {
                        continue;
                    }

                    yield return new TilePackageWriter.PackagedTile(tile.Z, tile.X, tile.Y, tileResult.Value.Data);
                }
            }

            if (plan.TilePackage)
            {
                exportedTileCount = await TilePackageWriter.WriteAsync(
                    archiveStream,
                    $"imageserver-{layerId.ToString(CultureInfo.InvariantCulture)}",
                    plan.TileExtension,
                    ResolveEsriCacheFormat(plan.RasterFormat),
                    plan.Bounds,
                    ReadTilesAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true);
                await foreach (var tile in ReadTilesAsync(cancellationToken).ConfigureAwait(false))
                {
                    var entry = archive.CreateEntry(
                        $"{tile.Level}/{tile.Column}/{tile.Row}.{plan.TileExtension}",
                        CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(tile.Bytes, cancellationToken).ConfigureAwait(false);
                    exportedTileCount++;
                }
            }

            if (exportedTileCount == 0)
            {
                return StandardErrorHelpers.CreateNotFound(context, "No exportable image tiles found for the request.");
            }

            archiveStream.Position = 0;
            var storageOptions = context.RequestServices.GetService<IOptions<CloudStorageOptions>>()?.Value;
            var ttl = storageOptions?.DefaultTimeToLive;
            if (!ttl.HasValue || ttl.Value <= TimeSpan.Zero)
            {
                ttl = TimeSpan.FromHours(24);
            }

            var uploadedAt = DateTimeOffset.UtcNow;
            var archiveContentType = plan.TilePackage ? ExportTilesTpkContentType : ExportTilesContentType;
            var archiveExtension = plan.TilePackage ? ExportTilesTpkStorageFormat : "zip";
            var fileName = $"imageserver-{layerId.ToString(CultureInfo.InvariantCulture)}-{uploadedAt:yyyyMMddHHmmss}-tiles.{archiveExtension}";
            var uploadResult = await _storage.UploadAsync(new FileUploadRequest
            {
                Content = archiveStream,
                FileName = fileName,
                ContentType = archiveContentType,
                SizeBytes = archiveStream.Length,
                TimeToLive = ttl,
                Folder = "imageserver/export-tiles",
                Metadata = ImmutableDictionary<string, string>.Empty
                    .Add("operation", "exportTiles")
                    .Add("layerId", layerId.ToString(CultureInfo.InvariantCulture))
                    .Add("tileMatrixSetId", "WebMercatorQuad")
                    .Add("storageFormat", plan.TilePackage ? ExportTilesTpkStorageFormat : ExportTilesStorageFormat)
                    .Add("minZoom", plan.MinZoom.ToString(CultureInfo.InvariantCulture))
                    .Add("maxZoom", plan.MaxZoom.ToString(CultureInfo.InvariantCulture))
            }, cancellationToken).ConfigureAwait(false);

            if (!uploadResult.Success || uploadResult.File is null)
            {
                return StandardErrorHelpers.CreateInternalServerError(
                    context,
                    "ImageServer exportTiles storage upload failed.");
            }

            var signedUrlLifetime = storageOptions?.SignedUrlLifetime;
            if (!signedUrlLifetime.HasValue || signedUrlLifetime.Value <= TimeSpan.Zero)
            {
                signedUrlLifetime = TimeSpan.FromMinutes(15);
            }

            var downloadUrl = await _storage.GetPresignedUrlAsync(
                uploadResult.File.FileId,
                signedUrlLifetime,
                cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            ImageServerLog.ExportTilesCompleted(
                _logger,
                layerId,
                exportedTileCount,
                uploadResult.File.SizeBytes,
                stopwatch.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(activity, exportedTileCount);
            HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);
            scope.SetSuccess(exportedTileCount);

            var response = new ImageServerExportTilesResponse
            {
                JobStatus = "esriJobSucceeded",
                LayerId = layerId,
                TileCount = exportedTileCount,
                MinZoom = plan.MinZoom,
                MaxZoom = plan.MaxZoom,
                TilePackage = plan.TilePackage,
                StorageFormat = plan.TilePackage ? ExportTilesTpkStorageFormat : ExportTilesStorageFormat,
                ContentType = uploadResult.File.ContentType,
                ArchiveFileId = uploadResult.File.FileId,
                FileId = uploadResult.File.FileId,
                Size = uploadResult.File.SizeBytes,
                SizeBytes = uploadResult.File.SizeBytes,
                DownloadUrl = downloadUrl,
                ExpiresAt = uploadResult.File.ExpiresAt,
                Bounds = plan.Bounds,
                Files =
                [
                    new ImageServerExportTilesFileInfo
                    {
                        Name = uploadResult.File.FileName,
                        FileId = uploadResult.File.FileId,
                        Url = downloadUrl,
                        ContentType = uploadResult.File.ContentType,
                        Size = uploadResult.File.SizeBytes,
                    },
                ],
                Results = new ImageServerExportTilesResults
                {
                    OutServiceUrl = new ImageServerExportTilesResultValue
                    {
                        ParamUrl = downloadUrl,
                        Value = downloadUrl,
                    },
                },
            };

            return Results.Json(response, ImageServerJsonContext.Default.ImageServerExportTilesResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex)
        {
            ImageServerLog.ExportTilesFailed(_logger, ex, layerId, ex.Message);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "ImageServer exportTiles failed.");
        }
    }

    private async Task<(ExportTilesPlan? Plan, IResult? Error)> TryBuildExportTilesPlanAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
        {
            ImageServerLog.LayerNotFound(_logger, layerId);
            return (null, StandardErrorHelpers.CreateNotFound(context, "Layer not found."));
        }

        if (!RasterParsingHelpers.TryParseRasterFormat(GetString(values, "format"), out var rasterFormat) ||
            rasterFormat is RasterFormat.COG or RasterFormat.Raw)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context,
                "format must be one of the supported tile formats: png, jpg, jpeg, tiff, tif."));
        }

        if (!TryResolveTileExtension(rasterFormat, out var tileExtension))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, "Unsupported tile format."));
        }

        if (!ImageServerMosaicHelpers.TryParseTime(GetString(values, "time"), out var timestamp, out var timeError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, timeError ?? "Invalid time parameter."));
        }

        var editionError = ImageServerMosaicHelpers.RequireTemporalMosaicAccess(context, timestamp);
        if (editionError != null)
        {
            return (null, editionError);
        }

        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;
        if (!TryParseExportTileLevels(
                GetString(values, "levels"),
                GetString(values, "minZoom"),
                GetString(values, "maxZoom"),
                limits,
                out var requestedZooms,
                out var minZoom,
                out var maxZoom,
                out var levelsError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, levelsError ?? "Invalid levels parameter."));
        }

        if (!TryParseExportTilesMaxTiles(GetString(values, "maxTiles"), limits, out var maxTiles, out var maxTilesError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, maxTilesError ?? "Invalid maxTiles parameter."));
        }

        if (!TryParseExportTilesExtent(
                GetString(values, "exportExtent") ?? GetString(values, "bbox"),
                GetString(values, "exportExtentSR") ?? GetString(values, "bboxSR"),
                out var sourceExtent,
                out var sourceSrid,
                out var extentError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, extentError ?? "Invalid exportExtent parameter."));
        }

        var extentTransform = await TryTransformExtentAsync(
            context,
            sourceExtent,
            sourceSrid,
            CoreSpatialReference.WGS84.Wkid,
            cancellationToken).ConfigureAwait(false);
        if (!extentTransform.IsSuccess)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context,
                extentTransform.Error ?? "Invalid exportExtent spatial reference."));
        }

        var bounds = NormalizeExportTilesBounds(extentTransform.Extent);
        var allTiles = BuildExportTileCoordinates(bounds, requestedZooms);
        var exceededTransferLimit = allTiles.Length > maxTiles;
        var selectedTiles = allTiles.Take(maxTiles).ToArray();
        if (selectedTiles.Length == 0)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, "exportTiles selected no tiles."));
        }

        if (!TryResolveExportTilesPackage(
                GetString(values, "tilePackage"),
                GetString(values, "storageFormat") ?? GetString(values, "exportBy"),
                out var tilePackage,
                out var packageError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, packageError!));
        }

        var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, GetString(values, "mosaicRule"));
        return (new ExportTilesPlan(
            layerId,
            mergeStrategy,
            selectedTiles,
            bounds,
            minZoom,
            maxZoom,
            exceededTransferLimit,
            rasterFormat,
            tileExtension,
            timestamp,
            tilePackage), null);
    }

    /// <summary>
    /// Resolves whether the request asked for an Esri tile package (TPK).
    /// Accepts <c>tilePackage=true</c> or <c>storageFormat=tpk</c> for the
    /// exploded-cache package; <c>zip</c>/empty selects the flat ZIP archive.
    /// The proprietary compact (TPKX) form is rejected with a 400.
    /// </summary>
    private static bool TryResolveExportTilesPackage(
        string? tilePackageValue,
        string? storageFormatValue,
        out bool tilePackage,
        out string? error)
    {
        tilePackage = false;
        error = null;

        if (!string.IsNullOrWhiteSpace(tilePackageValue) &&
            bool.TryParse(tilePackageValue.Trim(), out var parsedFlag))
        {
            tilePackage = parsedFlag;
        }

        if (string.IsNullOrWhiteSpace(storageFormatValue))
        {
            return true;
        }

        switch (storageFormatValue.Trim().ToLowerInvariant())
        {
            case "tpk":
            case "esritpk":
            case "exploded":
                tilePackage = true;
                return true;
            case "zip":
            case "":
                return true;
            case "tpkx":
            case "compact":
            case "compactv2":
                error = "storageFormat 'tpkx'/'compact' (Esri compact bundle cache) is not supported. Use storageFormat=tpk for an exploded tile package or storageFormat=zip for a flat archive.";
                return false;
            default:
                error = $"storageFormat '{storageFormatValue}' is not supported. Use tpk or zip.";
                return false;
        }
    }

    private static string ResolveEsriCacheFormat(RasterFormat format)
        => format switch
        {
            RasterFormat.PNG => "PNG",
            RasterFormat.JPEG => "JPEG",
            RasterFormat.TIFF => "TIFF",
            _ => "PNG",
        };

    private static bool TryParseExportTileLevels(
        string? levelsValue,
        string? minZoomValue,
        string? maxZoomValue,
        TileLimits limits,
        out int[] levels,
        out int minZoom,
        out int maxZoom,
        out string? error)
    {
        levels = [];
        minZoom = limits.MinTileZoom;
        maxZoom = limits.MinTileZoom;
        error = null;

        var parsed = new SortedSet<int>();
        if (!string.IsNullOrWhiteSpace(levelsValue))
        {
            foreach (var token in levelsValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var rangeParts = token.Split('-', StringSplitOptions.TrimEntries);
                if (rangeParts.Length == 1)
                {
                    if (!TryAddExportTilesLevel(rangeParts[0], limits, parsed, out error))
                    {
                        return false;
                    }
                }
                else if (rangeParts.Length == 2)
                {
                    if (!int.TryParse(rangeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
                        !int.TryParse(rangeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) ||
                        start > end)
                    {
                        error = "Invalid levels range.";
                        return false;
                    }

                    for (var level = start; level <= end; level++)
                    {
                        if (!TryAddExportTilesLevel(level.ToString(CultureInfo.InvariantCulture), limits, parsed, out error))
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    error = "Invalid levels parameter.";
                    return false;
                }
            }
        }

        if (parsed.Count == 0)
        {
            if (!TryParseOptionalZoom(minZoomValue, limits.MinTileZoom, limits, out minZoom, out error) ||
                !TryParseOptionalZoom(maxZoomValue, minZoom, limits, out maxZoom, out error))
            {
                return false;
            }

            if (minZoom > maxZoom)
            {
                error = "minZoom must be less than or equal to maxZoom.";
                return false;
            }

            for (var level = minZoom; level <= maxZoom; level++)
            {
                parsed.Add(level);
            }
        }

        levels = [.. parsed];
        minZoom = levels[0];
        maxZoom = levels[^1];
        return true;
    }

    private static bool TryAddExportTilesLevel(
        string value,
        TileLimits limits,
        SortedSet<int> levels,
        out string? error)
    {
        error = null;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
        {
            error = "Invalid levels parameter.";
            return false;
        }

        if (level < limits.MinTileZoom || level > limits.MaxTileZoom)
        {
            error = $"Tile level {level} is outside supported range ({limits.MinTileZoom}-{limits.MaxTileZoom}).";
            return false;
        }

        levels.Add(level);
        return true;
    }

    private static bool TryParseOptionalZoom(
        string? value,
        int defaultValue,
        TileLimits limits,
        out int zoom,
        out string? error)
    {
        zoom = defaultValue;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out zoom))
        {
            error = "Invalid zoom parameter.";
            return false;
        }

        if (zoom < limits.MinTileZoom || zoom > limits.MaxTileZoom)
        {
            error = $"Zoom level {zoom} is outside supported range ({limits.MinTileZoom}-{limits.MaxTileZoom}).";
            return false;
        }

        return true;
    }

    private static bool TryParseExportTilesMaxTiles(
        string? value,
        TileLimits limits,
        out int maxTiles,
        out string? error)
    {
        maxTiles = Math.Max(1, limits.MaxTilesPerRequest);
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) ||
            requested <= 0)
        {
            error = "maxTiles must be a positive integer.";
            return false;
        }

        maxTiles = Math.Min(requested, Math.Max(1, limits.MaxTilesPerRequest));
        return true;
    }

    private static bool TryParseExportTilesExtent(
        string? extentValue,
        string? fallbackSridValue,
        out SkiaMapRenderer.RenderExtent extent,
        out int srid,
        out string? error)
    {
        extent = new SkiaMapRenderer.RenderExtent(
            -180d,
            -SpatialConstants.WebMercatorMaxLatitude,
            180d,
            SpatialConstants.WebMercatorMaxLatitude);
        srid = CoreSpatialReference.WGS84.Wkid;
        error = null;

        if (string.IsNullOrWhiteSpace(extentValue))
        {
            return true;
        }

        var trimmed = extentValue.Trim();
        if (trimmed.StartsWith('{'))
        {
            return TryParseExportTilesJsonExtent(trimmed, fallbackSridValue, out extent, out srid, out error);
        }

        var parsedSrid = SpatialReferenceHelpers.TryParseSrid(fallbackSridValue);
        if (!string.IsNullOrWhiteSpace(fallbackSridValue) && !parsedSrid.HasValue)
        {
            error = "Invalid exportExtentSR parameter.";
            return false;
        }

        srid = parsedSrid ?? CoreSpatialReference.WGS84.Wkid;
        if (!TryParseBbox(trimmed, out extent))
        {
            error = "Invalid exportExtent parameter. Expected xmin,ymin,xmax,ymax or an extent JSON object.";
            return false;
        }

        return true;
    }

    private static bool TryParseExportTilesJsonExtent(
        string value,
        string? fallbackSridValue,
        out SkiaMapRenderer.RenderExtent extent,
        out int srid,
        out string? error)
    {
        extent = default;
        srid = CoreSpatialReference.WGS84.Wkid;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetJsonDouble(root, "xmin", out var xmin) ||
                !TryGetJsonDouble(root, "ymin", out var ymin) ||
                !TryGetJsonDouble(root, "xmax", out var xmax) ||
                !TryGetJsonDouble(root, "ymax", out var ymax))
            {
                error = "exportExtent JSON must include xmin, ymin, xmax, and ymax.";
                return false;
            }

            if (!TryResolveExportTilesExtentSrid(root, fallbackSridValue, out srid, out error))
            {
                return false;
            }

            extent = new SkiaMapRenderer.RenderExtent(xmin, ymin, xmax, ymax);
            return ymin < ymax && (xmin < xmax || CoordinateTransformer.IsWrappedGeographicExtent(extent));
        }
        catch (JsonException)
        {
            error = "exportExtent contains invalid JSON.";
            return false;
        }
    }

    private static bool TryResolveExportTilesExtentSrid(
        JsonElement root,
        string? fallbackSridValue,
        out int srid,
        out string? error)
    {
        srid = CoreSpatialReference.WGS84.Wkid;
        error = null;

        if (root.TryGetProperty("spatialReference", out var srElement) &&
            srElement.ValueKind == JsonValueKind.Object)
        {
            if (TryGetJsonInt(srElement, "latestWkid", out var latestWkid))
            {
                srid = latestWkid;
                return true;
            }

            if (TryGetJsonInt(srElement, "wkid", out var wkid))
            {
                srid = wkid;
                return true;
            }
        }

        var parsedSrid = SpatialReferenceHelpers.TryParseSrid(fallbackSridValue);
        if (!string.IsNullOrWhiteSpace(fallbackSridValue) && !parsedSrid.HasValue)
        {
            error = "Invalid exportExtentSR parameter.";
            return false;
        }

        srid = parsedSrid ?? CoreSpatialReference.WGS84.Wkid;
        return true;
    }

    private static bool TryParseBbox(string value, out SkiaMapRenderer.RenderExtent extent)
    {
        extent = default;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var xmin) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ymin) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var xmax) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var ymax) ||
            ymin >= ymax ||
            xmin >= xmax)
        {
            return false;
        }

        extent = new SkiaMapRenderer.RenderExtent(xmin, ymin, xmax, ymax);
        return true;
    }

    private static bool TryGetJsonDouble(JsonElement element, string propertyName, out double value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetJsonInt(JsonElement element, string propertyName, out int value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static double[] NormalizeExportTilesBounds(SkiaMapRenderer.RenderExtent extent)
    {
        var minLon = Math.Clamp(Math.Min(extent.MinX, extent.MaxX), -180d, 180d);
        var maxLon = Math.Clamp(Math.Max(extent.MinX, extent.MaxX), -180d, 180d);
        var minLat = Math.Clamp(Math.Min(extent.MinY, extent.MaxY), -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude);
        var maxLat = Math.Clamp(Math.Max(extent.MinY, extent.MaxY), -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude);
        return [minLon, minLat, maxLon, maxLat];
    }

    private static ExportTileCoordinate[] BuildExportTileCoordinates(double[] bounds, IReadOnlyList<int> levels)
    {
        var minLon = bounds[0];
        var minLat = bounds[1];
        var maxLon = bounds[2];
        var maxLat = bounds[3];
        var coordinates = new List<ExportTileCoordinate>();

        foreach (var z in levels)
        {
            var n = 1 << z;
            var xMin = LonToExportTileX(minLon, z, n);
            var xMax = LonToExportTileX(maxLon, z, n);
            var yMin = LatToExportTileY(maxLat, z, n);
            var yMax = LatToExportTileY(minLat, z, n);

            for (var x = xMin; x <= xMax; x++)
            {
                for (var y = yMin; y <= yMax; y++)
                {
                    coordinates.Add(new ExportTileCoordinate(z, x, y));
                }
            }
        }

        return [.. coordinates];
    }

    private static int LonToExportTileX(double lon, int z, int n)
    {
        var clampedLon = Math.Clamp(lon, -180d, 180d);
        var x = (int)Math.Floor((clampedLon + 180d) / 360d * n);
        return Math.Clamp(x, 0, n - 1);
    }

    private static int LatToExportTileY(double lat, int z, int n)
    {
        var clampedLat = Math.Clamp(lat, -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude);
        var latRad = clampedLat * Math.PI / 180d;
        var y = (int)Math.Floor(
            (1d - Math.Log(Math.Tan(latRad) + 1d / Math.Cos(latRad)) / Math.PI) / 2d * n);
        return Math.Clamp(y, 0, n - 1);
    }

    private static bool TryResolveTileExtension(RasterFormat format, out string extension)
    {
        extension = format switch
        {
            RasterFormat.PNG => "png",
            RasterFormat.JPEG => "jpg",
            RasterFormat.TIFF => "tif",
            _ => string.Empty,
        };

        return extension.Length > 0;
    }

    private static byte[] CreateTileEnvelope(int level, int row, int col)
    {
        const double worldExtent = 20037508.342789244;
        var tileSpan = (worldExtent * 2d) / (1 << level);
        var minX = -worldExtent + (col * tileSpan);
        var maxX = minX + tileSpan;
        var maxY = worldExtent - (row * tileSpan);
        var minY = maxY - tileSpan;
        return ImageServerMosaicHelpers.CreateEnvelopeGeometry(minX, minY, maxX, maxY);
    }

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;

    // -----------------------------------------------------------------------
    // Asynchronous durable exportTiles (Compact Cache V2 / TPKX) — #2707
    // -----------------------------------------------------------------------

    private const string CompactV2StorageMode = "esriMapCacheStorageModeCompactV2";

    /// <summary>
    /// Detects an explicit Compact Cache V2 negotiation via the official
    /// <c>storageFormatType=esriMapCacheStorageModeCompactV2</c> parameter or the documented
    /// compatible TPKX aliases on <c>storageFormat</c>/<c>exportBy</c>.
    /// </summary>
    private static bool IsCompactV2Requested(IReadOnlyDictionary<string, StringValues> values)
    {
        var storageFormatType = GetString(values, "storageFormatType");
        if (!string.IsNullOrWhiteSpace(storageFormatType)
            && string.Equals(storageFormatType.Trim(), CompactV2StorageMode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var storageFormat = GetString(values, "storageFormat") ?? GetString(values, "exportBy");
        return storageFormat?.Trim().ToLowerInvariant() is "tpkx" or "compact" or "compactv2";
    }

    /// <summary>
    /// Builds and submits a durable tile-export job for a Compact Cache V2 request, returning the
    /// ArcGIS <c>{ jobId, jobStatus: "esriJobSubmitted" }</c> envelope. Validation, ownership,
    /// admission, and store-availability failures surface through the shared sanitized mapping.
    /// </summary>
    private async Task<IResult> SubmitDurableExportAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        var (plan, error) = await TryBuildDurableExportPlanAsync(context, layerId, values, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        try
        {
            var job = await _tileExportJobService!.SubmitAsync(
                plan!,
                idempotencyKey: GetString(values, "idempotencyKey"),
                correlationId: context.TraceIdentifier,
                context.User,
                cancellationToken).ConfigureAwait(false);
            ImageServerLog.ExportTilesRequested(_logger, layerId, plan!.ZoomLevels.Length);
            return TypedResults.Json(
                new ImageServerExportTilesJobSubmitResponse { JobId = job.OperationId },
                ImageServerJsonContext.Default.ImageServerExportTilesJobSubmitResponse);
        }
        catch (Exception exception) when (TileExportAdapterResults.TryMap(context, exception) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>Projects a durable tile-export job's status onto the ArcGIS Image Service status envelope.</summary>
    public async Task<IResult> GetJobStatusAsync(HttpContext context, int layerId, string jobId, CancellationToken cancellationToken)
    {
        if (_tileExportJobService is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Asynchronous tile-export jobs are not available.");
        }

        try
        {
            var job = await _tileExportJobService
                .GetStatusAsync(jobId, ScopeFor(layerId), context.User, cancellationToken).ConfigureAwait(false);
            return TypedResults.Json(
                new ImageServerExportTilesJobStatusResponse
                {
                    JobId = job.OperationId,
                    JobStatus = GPServerStatusMapping.ToEsriJobStatus(job.Status),
                    PercentComplete = job.PercentComplete,
                    Messages = BuildJobMessages(job),
                },
                ImageServerJsonContext.Default.ImageServerExportTilesJobStatusResponse);
        }
        catch (Exception exception) when (TileExportAdapterResults.TryMap(context, exception) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>Cancels a durable tile-export job scoped to the submitting principal and this image service.</summary>
    public async Task<IResult> CancelJobAsync(HttpContext context, int layerId, string jobId, CancellationToken cancellationToken)
    {
        if (_tileExportJobService is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Asynchronous tile-export jobs are not available.");
        }

        try
        {
            await _tileExportJobService.CancelAsync(jobId, ScopeFor(layerId), context.User, cancellationToken).ConfigureAwait(false);
            var job = await _tileExportJobService.GetStatusAsync(jobId, ScopeFor(layerId), context.User, cancellationToken).ConfigureAwait(false);
            return TypedResults.Json(
                new ImageServerExportTilesJobStatusResponse
                {
                    JobId = job.OperationId,
                    JobStatus = GPServerStatusMapping.ToEsriJobStatus(job.Status),
                    PercentComplete = job.PercentComplete,
                    Messages = BuildJobMessages(job),
                },
                ImageServerJsonContext.Default.ImageServerExportTilesJobStatusResponse);
        }
        catch (Exception exception) when (TileExportAdapterResults.TryMap(context, exception) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>Returns the ArcGIS <c>results/out_service_url</c> for a completed durable tile-export job.</summary>
    public async Task<IResult> GetJobResultAsync(HttpContext context, int layerId, string jobId, CancellationToken cancellationToken)
    {
        if (_tileExportJobService is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Asynchronous tile-export jobs are not available.");
        }

        try
        {
            var result = await _tileExportJobService
                .GetResultAsync(jobId, ScopeFor(layerId), context.User, cancellationToken).ConfigureAwait(false);
            return TypedResults.Json(
                new ImageServerExportTilesJobResultResponse
                {
                    JobId = jobId,
                    Results = new ImageServerExportTilesJobResults
                    {
                        OutServiceUrl = new ImageServerExportTilesJobResultValue
                        {
                            Value = result.DownloadUrl,
                            ExpiresAt = result.ExpiresAt,
                        },
                    },
                },
                ImageServerJsonContext.Default.ImageServerExportTilesJobResultResponse);
        }
        catch (Exception exception) when (TileExportAdapterResults.TryMap(context, exception) is { } mapped)
        {
            return mapped;
        }
    }

    private static TileExportJobScope ScopeFor(int layerId)
        => new(TileExportSourceKind.Raster, layerId.ToString(CultureInfo.InvariantCulture));

    private static IReadOnlyList<ImageServerExportTilesJobMessage> BuildJobMessages(
        Honua.Core.Features.ControlPlane.Domain.ExecutionJobRecord job)
        => job.Status == Honua.Core.Features.ControlPlane.Domain.ExecutionJobStatus.Failed
            && !string.IsNullOrWhiteSpace(job.ErrorMessage)
                ? [new ImageServerExportTilesJobMessage { Type = "esriJobMessageTypeError", Description = job.ErrorMessage }]
                : [];

    private async Task<(TileExportJobPlan? Plan, IResult? Error)> TryBuildDurableExportPlanAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
        {
            return (null, StandardErrorHelpers.CreateNotFound(context, "Layer not found."));
        }

        if (!RasterParsingHelpers.TryParseRasterFormat(GetString(values, "format"), out var rasterFormat)
            || !TryResolveDurableImageFormat(rasterFormat, out var tileImageFormat))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context, "Compact Cache V2 tile export supports only png or jpeg tile formats."));
        }

        if (!ImageServerMosaicHelpers.TryParseTime(GetString(values, "time"), out var timestamp, out var timeError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, timeError ?? "Invalid time parameter."));
        }

        var editionError = ImageServerMosaicHelpers.RequireTemporalMosaicAccess(context, timestamp);
        if (editionError is not null)
        {
            return (null, editionError);
        }

        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;
        if (!TryParseExportTileLevels(
                GetString(values, "levels"),
                GetString(values, "minZoom"),
                GetString(values, "maxZoom"),
                limits,
                out var requestedZooms,
                out _,
                out _,
                out var levelsError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, levelsError ?? "Invalid levels parameter."));
        }

        if (requestedZooms.Length < 2)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context, "Compact Cache V2 tile export requires at least two zoom levels."));
        }

        if (!TryParseExportTilesMaxTiles(GetString(values, "maxTiles"), limits, out var maxTiles, out var maxTilesError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, maxTilesError ?? "Invalid maxTiles parameter."));
        }

        if (!TryParseExportTilesExtent(
                GetString(values, "exportExtent") ?? GetString(values, "bbox"),
                GetString(values, "exportExtentSR") ?? GetString(values, "bboxSR"),
                out var sourceExtent,
                out var sourceSrid,
                out var extentError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, extentError ?? "Invalid exportExtent parameter."));
        }

        var extentTransform = await TryTransformExtentAsync(
            context, sourceExtent, sourceSrid, CoreSpatialReference.WGS84.Wkid, cancellationToken).ConfigureAwait(false);
        if (!extentTransform.IsSuccess)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context, extentTransform.Error ?? "Invalid exportExtent spatial reference."));
        }

        var bounds = NormalizeExportTilesBounds(extentTransform.Extent);
        var mosaicRuleRaw = GetString(values, "mosaicRule");
        var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, mosaicRuleRaw);
        var timeRaw = GetString(values, "time");

        var descriptor = new TileExportRasterSourceDescriptor(
            snapshot.Revision,
            layerId.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(mosaicRuleRaw) ? mergeStrategy.ToString() : mosaicRuleRaw.Trim(),
            string.IsNullOrWhiteSpace(timeRaw) ? null : timeRaw.Trim(),
            BuildRasterFingerprint(mergeStrategy, mosaicRuleRaw, timeRaw, tileImageFormat));

        var plan = new TileExportJobPlan
        {
            SourceKind = TileExportSourceKind.Raster,
            ResourceId = layerId.ToString(CultureInfo.InvariantCulture),
            Source = descriptor,
            ZoomLevels = [.. requestedZooms],
            West = bounds[0],
            South = bounds[1],
            East = bounds[2],
            North = bounds[3],
            TileImageFormat = tileImageFormat,
            PackageFormat = TileExportPackageFormat.Tpkx,
            MaxTiles = maxTiles,
            MaxArtifactBytes = 1024L * 1024 * 1024,
            RetentionSeconds = ResolveRetentionSeconds(context),
        };

        return (plan, null);
    }

    private static bool TryResolveDurableImageFormat(RasterFormat format, out string tileImageFormat)
    {
        tileImageFormat = format switch
        {
            RasterFormat.PNG => "PNG",
            RasterFormat.JPEG => "JPEG",
            _ => string.Empty,
        };
        return tileImageFormat.Length > 0;
    }

    private static int ResolveRetentionSeconds(HttpContext context)
    {
        var ttl = context.RequestServices.GetService<IOptions<CloudStorageOptions>>()?.Value.DefaultTimeToLive;
        var seconds = ttl is { } value && value > TimeSpan.Zero ? (long)value.TotalSeconds : 86_400L;
        return (int)Math.Clamp(seconds, 60L, 7L * 24 * 60 * 60);
    }

    private static string BuildRasterFingerprint(RasterMergeStrategy mergeStrategy, string? mosaicRule, string? time, string format)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{mergeStrategy}|{mosaicRule ?? string.Empty}|{time ?? string.Empty}|{format}");
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
