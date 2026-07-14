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
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Tiles;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using static Honua.Infrastructure.Rendering.RasterMapRenderingPipeline;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private const string ExportTilesContentType = "application/zip";
    private const string ExportTilesStorageFormat = "zip";
    private const string ExportTilesTpkStorageFormat = "tpk";
    private const string ExportTilesTpkContentType = "application/octet-stream";
    private const int ExportTilesTileSizeBytesEstimate = 32 * 1024;

    private readonly record struct ExportTileCoordinate(int Z, int X, int Y);

    private sealed record ExportTilesPlan(
        string ServiceId,
        MetadataV2Service Service,
        ExportRenderLayer[] Layers,
        RenderLayerDescriptor[] RenderLayers,
        ExportTileCoordinate[] Tiles,
        long TotalTileCount,
        double[] Bounds,
        int MinZoom,
        int MaxZoom,
        bool ExceededTransferLimit,
        bool TilePackage);

    private static async Task<IResult> HandleEstimateExportTilesSize(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var (plan, error) = await TryBuildExportTilesPlanAsync(context, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        // Report the TRUE tile count (not the maxTiles-bounded build size) so the estimate stays
        // meaningful even when the request exceeds the transfer limit; the bounded build only ever
        // exists to keep the allocation from OOM-ing the host (#2065).
        var estimatedSizeBytes = checked(plan!.TotalTileCount * ExportTilesTileSizeBytesEstimate);
        var response = new ExportTilesEstimateResponse
        {
            TileCount = plan.TotalTileCount,
            Size = estimatedSizeBytes,
            SizeUnit = "bytes",
            EstimatedSizeBytes = estimatedSizeBytes,
            MinZoom = plan.MinZoom,
            MaxZoom = plan.MaxZoom,
            TilePackage = plan.TilePackage,
            StorageFormat = plan.TilePackage ? ExportTilesTpkStorageFormat : ExportTilesStorageFormat,
            ContentType = plan.TilePackage ? ExportTilesTpkContentType : ExportTilesContentType,
            ExceededTransferLimit = plan.ExceededTransferLimit
        };

        return Results.Json(
            response,
            MapServerJsonContext.Default.ExportTilesEstimateResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleExportTiles(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var (plan, error) = await TryBuildExportTilesPlanAsync(context, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var storage = context.RequestServices.GetService<ICloudFileStorage>();
        if (storage is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Cloud file storage is not configured. exportTiles requires storage-backed artifacts.");
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");
        MapServerLog.ExportTilesRequested(logger, plan!.ServiceId, plan.Tiles.Length);

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.MapServerExport,
            ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, plan.ServiceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "exportTiles");
        activity?.SetTag("honua.mapserver.tile_count", plan.Tiles.Length);

        var stopwatch = Stopwatch.StartNew();
        await using var archiveStream = new MemoryStream();
        var totalFeatureCount = 0;
        IResult? renderError = null;

        try
        {
            // Render each tile once; either flatten into a {z}/{x}/{y}.png ZIP or
            // package into the Esri exploded-cache TPK layout via the shared writer.
            async IAsyncEnumerable<TilePackageWriter.PackagedTile> RenderTilesAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
            {
                foreach (var tile in plan.Tiles)
                {
                    token.ThrowIfCancellationRequested();
                    var renderResult = await RenderRasterTileV2Async(
                        context,
                        ResolveExportServiceSrid(plan.Service, plan.Layers.Select(static layer => layer.Layer).ToArray()),
                        plan.RenderLayers,
                        tile.Z,
                        tile.Y,
                        tile.X,
                        plan.Service.Settings?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer,
                        token).ConfigureAwait(false);

                    if (!renderResult.IsSuccess)
                    {
                        renderError = renderResult.Error!;
                        yield break;
                    }

                    totalFeatureCount += renderResult.FeatureCount;
                    yield return new TilePackageWriter.PackagedTile(tile.Z, tile.X, tile.Y, renderResult.ImageBytes);
                }
            }

            if (plan.TilePackage)
            {
                await TilePackageWriter.WriteAsync(
                    archiveStream,
                    SanitizeExportTilesKeySegment(plan.ServiceId),
                    "png",
                    "PNG",
                    plan.Bounds,
                    RenderTilesAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true);
                await foreach (var tile in RenderTilesAsync(cancellationToken).ConfigureAwait(false))
                {
                    var entry = archive.CreateEntry(
                        $"{tile.Level}/{tile.Column}/{tile.Row}.png",
                        CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(tile.Bytes, cancellationToken).ConfigureAwait(false);
                }
            }

            if (renderError is not null)
            {
                return renderError;
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
            var fileName = $"{SanitizeExportTilesKeySegment(plan.ServiceId)}-{uploadedAt:yyyyMMddHHmmss}-tiles.{archiveExtension}";
            var uploadResult = await storage.UploadAsync(new FileUploadRequest
            {
                Content = archiveStream,
                FileName = fileName,
                ContentType = archiveContentType,
                SizeBytes = archiveStream.Length,
                TimeToLive = ttl,
                Folder = "mapserver/export-tiles",
                Metadata = ImmutableDictionary<string, string>.Empty
                    .Add("operation", "exportTiles")
                    .Add("serviceId", plan.ServiceId)
                    .Add("tileMatrixSetId", "WebMercatorQuad")
                    .Add("storageFormat", plan.TilePackage ? ExportTilesTpkStorageFormat : ExportTilesStorageFormat)
                    .Add("minZoom", plan.MinZoom.ToString(CultureInfo.InvariantCulture))
                    .Add("maxZoom", plan.MaxZoom.ToString(CultureInfo.InvariantCulture))
            }, cancellationToken).ConfigureAwait(false);

            if (!uploadResult.Success || uploadResult.File is null)
            {
                return StandardErrorHelpers.CreateInternalServerError(
                    context,
                    "MapServer exportTiles storage upload failed.");
            }

            var signedUrlLifetime = storageOptions?.SignedUrlLifetime;
            if (!signedUrlLifetime.HasValue || signedUrlLifetime.Value <= TimeSpan.Zero)
            {
                signedUrlLifetime = TimeSpan.FromMinutes(15);
            }

            var downloadUrl = await storage.GetPresignedUrlAsync(
                uploadResult.File.FileId,
                signedUrlLifetime,
                cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            MapServerLog.ExportTilesCompleted(
                logger,
                plan.ServiceId,
                plan.Tiles.Length,
                uploadResult.File.SizeBytes,
                stopwatch.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(activity, totalFeatureCount);
            HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);

            var response = new ExportTilesResponse
            {
                JobStatus = "esriJobSucceeded",
                ServiceId = plan.ServiceId,
                TileCount = plan.Tiles.LongLength,
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
                    new ExportTilesFileInfo
                    {
                        Name = uploadResult.File.FileName,
                        FileId = uploadResult.File.FileId,
                        Url = downloadUrl,
                        ContentType = uploadResult.File.ContentType,
                        Size = uploadResult.File.SizeBytes
                    }
                ],
                Results = new ExportTilesResults
                {
                    OutServiceUrl = new ExportTilesResultValue
                    {
                        ParamUrl = downloadUrl,
                        Value = downloadUrl
                    }
                }
            };

            return Results.Json(response, MapServerJsonContext.Default.ExportTilesResponse, contentType: "application/json");
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
            MapServerLog.ExportTilesFailed(logger, plan.ServiceId, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "MapServer exportTiles failed.");
        }
    }

    private static async Task<(ExportTilesPlan? Plan, IResult? Error)> TryBuildExportTilesPlanAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return (null, serviceError);
        }

        var (values, readError) = await TryReadMapServerRequestValuesAsync(context).ConfigureAwait(false);
        if (values == null)
        {
            if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return (null, GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType));
            }

            return (null, StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body."));
        }

        var outputFormat = GetValue(values, "f") ?? "json";
        if (!string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputFormat, "pjson", StringComparison.OrdinalIgnoreCase))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context,
                $"Output format '{outputFormat}' is not supported."));
        }

        // Esri exportTiles selects the package format via tilePackage=true (TPK)
        // or storageFormat=tpk/compact/zip. compact/tpkx is not implemented; see
        // the parity matrix for the documented deferral.
        if (!TryResolveExportTilesPackage(
                GetValue(values, "tilePackage"),
                GetValue(values, "storageFormat") ?? GetValue(values, "exportBy"),
                out var tilePackage,
                out var packageError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, packageError!));
        }

        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;
        if (!TryParseExportTileLevels(
                GetValue(values, "levels"),
                GetValue(values, "minZoom"),
                GetValue(values, "maxZoom"),
                limits,
                out var requestedZooms,
                out var minZoom,
                out var maxZoom,
                out var levelsError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, levelsError ?? "Invalid levels parameter."));
        }

        if (!TryParseExportTilesMaxTiles(GetValue(values, "maxTiles"), limits, out var maxTiles, out var maxTilesError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, maxTilesError ?? "Invalid maxTiles parameter."));
        }

        if (!TryParseExportTilesExtent(
                GetValue(values, "exportExtent") ?? GetValue(values, "bbox"),
                GetValue(values, "exportExtentSR") ?? GetValue(values, "bboxSR"),
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
            SpatialReference.WGS84.Wkid,
            cancellationToken).ConfigureAwait(false);
        if (!extentTransform.IsSuccess)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context,
                extentTransform.Error ?? "Invalid exportExtent spatial reference."));
        }

        var bounds = NormalizeExportTilesBounds(extentTransform.Extent);

        // Run service validation and the access-policy gate BEFORE materializing the tile
        // grid so an unauthenticated request against a non-existent or unauthorized service
        // is rejected without allocating anything (#2065).
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            ServiceProtocols.MapServer,
            context,
            cancellationToken: cancellationToken);
        if (!serviceResult.IsValid)
        {
            return (null, serviceResult.ErrorResult!);
        }

        var service = serviceResult.Service!;
        var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var publishedLayers = ResolveMapServerMetadataLayers(snapshot, service);

        var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            publishedLayers.Select(static layer => layer.Resource),
            service);
        if (accessError != null)
        {
            return (null, accessError);
        }

        // Compute the total tile count Σ(xMax-xMin+1)*(yMax-yMin+1) per zoom level FIRST and
        // reject when it exceeds maxTiles, BEFORE building the full grid. The previous code
        // materialized every coordinate then .Take(maxTiles)'d, so a whole-world request at a
        // high zoom (e.g. z18 → ~6.9e10 entries) allocated hundreds of GB before any cap was
        // applied — an unauthenticated OOM DoS (#2065).
        var totalTileCount = CountExportTileCoordinates(bounds, requestedZooms);
        var exceededTransferLimit = totalTileCount > maxTiles;
        if (totalTileCount <= 0)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, "exportTiles selected no tiles."));
        }

        // Bound the build to maxTiles tiles. When the request exceeds the transfer limit the
        // estimate path still reports exceededTransferLimit; the export path returns only the
        // first maxTiles tiles (never the unbounded full grid).
        var buildLimit = (int)Math.Min(totalTileCount, maxTiles);
        var selectedTiles = BuildExportTileCoordinates(bounds, requestedZooms, buildLimit);
        if (selectedTiles.Length == 0)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, "exportTiles selected no tiles."));
        }

        var renderSelection = ResolveRenderLayers(
            publishedLayers,
            service,
            GetValue(values, "layers"),
            Array.Empty<DynamicLayerDefinition>(),
            context);
        if (renderSelection.Error is not null)
        {
            return (null, renderSelection.Error);
        }

        var renderLayers = renderSelection.Layers
            .Where(static layer => HasMapServerGeometry(layer.Layer.Resource))
            .ToArray();
        var descriptors = renderLayers
            .Select(static layer => CreateRenderLayerDescriptorFromV2(
                layer.Layer.StorageLayerId,
                HasMapServerGeometry(layer.Layer.Resource),
                layer.Layer.Resource.ReadGeometryType()))
            .ToArray();

        return (new ExportTilesPlan(
            serviceId,
            service,
            renderLayers,
            descriptors,
            selectedTiles,
            totalTileCount,
            bounds,
            minZoom,
            maxZoom,
            exceededTransferLimit,
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

        levels = parsed.ToArray();
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
        srid = SpatialReference.WGS84.Wkid;
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

        srid = parsedSrid ?? SpatialReference.WGS84.Wkid;
        var isGeographic = SpatialReference.Create(srid).IsGeographic;
        if (!TryParseBbox(trimmed, AxisOrder.EastNorth, isGeographic, out extent))
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
        srid = SpatialReference.WGS84.Wkid;
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
        srid = SpatialReference.WGS84.Wkid;
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

        srid = parsedSrid ?? SpatialReference.WGS84.Wkid;
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

    private static double[] NormalizeExportTilesBounds(SkiaMapRenderer.RenderExtent extent)
    {
        var minLon = Math.Clamp(Math.Min(extent.MinX, extent.MaxX), -180d, 180d);
        var maxLon = Math.Clamp(Math.Max(extent.MinX, extent.MaxX), -180d, 180d);
        var minLat = Math.Clamp(Math.Min(extent.MinY, extent.MaxY), -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude);
        var maxLat = Math.Clamp(Math.Max(extent.MinY, extent.MaxY), -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude);
        return [minLon, minLat, maxLon, maxLat];
    }

    /// <summary>
    /// Counts the total number of tiles the request would produce as
    /// <c>Σ (xMax-xMin+1)*(yMax-yMin+1)</c> across the requested zoom levels, using
    /// <see cref="long"/> arithmetic so a whole-world high-zoom request cannot overflow.
    /// This is computed before the grid is materialized so the caller can reject an
    /// over-limit request without allocating the full coordinate list (#2065).
    /// </summary>
    private static long CountExportTileCoordinates(double[] bounds, IReadOnlyList<int> levels)
    {
        var minLon = bounds[0];
        var minLat = bounds[1];
        var maxLon = bounds[2];
        var maxLat = bounds[3];
        long total = 0;

        foreach (var z in levels)
        {
            var n = 1 << z;
            var xMin = LonToExportTileX(minLon, z, n);
            var xMax = LonToExportTileX(maxLon, z, n);
            var yMin = LatToExportTileY(maxLat, z, n);
            var yMax = LatToExportTileY(minLat, z, n);

            var width = (long)(xMax - xMin + 1);
            var height = (long)(yMax - yMin + 1);
            total += width * height;
        }

        return total;
    }

    private static ExportTileCoordinate[] BuildExportTileCoordinates(
        double[] bounds,
        IReadOnlyList<int> levels,
        int maxTiles)
    {
        var minLon = bounds[0];
        var minLat = bounds[1];
        var maxLon = bounds[2];
        var maxLat = bounds[3];
        // Pre-size to the bounded cap, never the (potentially enormous) full grid, so the
        // build itself can never allocate more than maxTiles coordinates (#2065).
        var coordinates = new List<ExportTileCoordinate>(Math.Max(0, maxTiles));

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
                    if (coordinates.Count >= maxTiles)
                    {
                        return [.. coordinates];
                    }

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

    private static string SanitizeExportTilesKeySegment(string value)
    {
        var trimmed = value.Trim().Trim('/');
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        }

        return builder.Length == 0 ? "mapserver" : builder.ToString();
    }
}
