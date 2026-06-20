// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server identify operations.
/// Provides pixel value identification at geographic points.
/// </summary>
internal sealed class ImageServerIdentifyHandler
{
    private const int MaxGeometryInputLength = 1000;
    private const string PointGeometryType = "esriGeometryPoint";
    private const string EnvelopeGeometryType = "esriGeometryEnvelope";
    private const string PolygonGeometryType = "esriGeometryPolygon";

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerIdentifyHandler> _logger;

    public ImageServerIdentifyHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ILogger<ImageServerIdentifyHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Identifies pixel values at a specified geographic location.
    /// </summary>
    public async Task<IResult> IdentifyAsync(
        HttpContext context,
        int layerId,
        IdentifyRequest request,
        CancellationToken cancellationToken = default)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "identify",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "identify-pixel");

        try
        {
            // Validate layer exists in the Metadata v2 graph
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            if (!string.IsNullOrWhiteSpace(request.GeometryType) &&
                !IsSupportedGeometryType(request.GeometryType))
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, "Unsupported geometry type");
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Unsupported geometryType '{request.GeometryType}'. Supported: {PointGeometryType}, {EnvelopeGeometryType}, {PolygonGeometryType}.");
            }

            // Parse geometry coordinates. Point geometries identify at the point; envelope and
            // polygon geometries identify at their centroid (the representative pixel for the
            // AOI), matching how ArcGIS resolves a single identify value for an area geometry.
            var (x, y, srid) = ParseGeometry(request);
            if (!x.HasValue || !y.HasValue)
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, "Invalid geometry coordinates");
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid geometry coordinates");
            }

            if (!ImageServerMosaicHelpers.TryParseTime(request.Time, out var timestamp, out var timeError))
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, timeError ?? "Invalid time parameter");
                return StandardErrorHelpers.CreateBadRequest(context, timeError ?? "Invalid time parameter.");
            }

            // CONTRACT CHANGE (#1660): when a renderingRule is supplied, identify returns the
            // RENDERED pixel value (post clip/stretch/colormap), matching Esri ImageServer's
            // identify-with-renderingRule behaviour, instead of the raw source value. When no
            // renderingRule is supplied the raw-value contract is preserved unchanged. The same
            // Stretch/Colormap/Clip the exportImage path executes are applied at the sampled
            // pixel via the shared raster pipeline; unsupported chains surface 400/501 as in export.
            RasterIdentifyRendering? rendering = null;
            if (!string.IsNullOrWhiteSpace(request.RenderingRule))
            {
                if (!TryMapRenderingRule(request.RenderingRule, out rendering, out var renderingError, out var notImplemented))
                {
                    ImageServerLog.InvalidIdentifyParameters(_logger, layerId, renderingError ?? "Invalid renderingRule");
                    return notImplemented
                        ? StandardErrorHelpers.CreateNotImplemented(context, renderingError ?? "renderingRule is not implemented.")
                        : StandardErrorHelpers.CreateBadRequest(context, renderingError ?? "renderingRule is not supported.");
                }
            }

            var editionError = ImageServerMosaicHelpers.RequireTemporalMosaicAccess(context, timestamp);
            if (editionError != null)
            {
                return editionError;
            }

            var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, request.MosaicRule);
            var selectionQuery = new RasterSelectionQuery
            {
                Geometry = ImageServerMosaicHelpers.CreatePointGeometry(x.Value, y.Value),
                GeometrySrid = srid,
                Timestamp = timestamp
            };
            var selectedRasters = await _rasterStore.QueryRastersAsync(layerId, selectionQuery, cancellationToken);
            if (selectedRasters.Length == 0)
            {
                // ArcGIS ImageServer identify returns a 200 NoData document (not a 404) when the
                // requested location does not intersect any raster, mirroring getSamples which
                // returns an empty 200. Returning 404 here breaks the ArcGIS JS/Python SDK
                // imageService.identify call for out-of-extent or no-data points.
                ImageServerLog.NoRastersFound(_logger, layerId);
                var noDataResponse = new IdentifyResponse
                {
                    ObjectId = null,
                    Name = resolved.DisplayName,
                    Value = "NoData",
                    Location = new Point
                    {
                        X = x.Value,
                        Y = y.Value
                    },
                    Properties = new Dictionary<string, object?>
                    {
                        ["HasData"] = false,
                        ["Coordinates"] = $"{x.Value}, {y.Value}",
                        ["SRID"] = srid,
                        ["BandCount"] = 0
                    },
                    CatalogItems = request.ReturnCatalogItems == true
                        ? Array.Empty<CatalogItem>()
                        : null
                };

                return Results.Json(noDataResponse, ImageServerJsonContext.Default.IdentifyResponse);
            }

            ImageServerLog.IdentifyStarted(_logger, layerId, x.Value, y.Value);

            // Identify pixel values
            var pixelResult = selectedRasters.Length == 1
                ? await _rasterStore.IdentifyAsync(layerId, selectedRasters[0].Id, x.Value, y.Value, srid, rendering, cancellationToken)
                : await _rasterStore.IdentifyMosaicAsync(
                    layerId,
                    selectedRasters.Select(r => r.Id).ToArray(),
                    mergeStrategy,
                    x.Value,
                    y.Value,
                    srid,
                    rendering,
                    cancellationToken);

            // returnGeometry controls whether catalog item footprints are emitted. ArcGIS
            // returns the footprint envelope on each participating catalog item when
            // returnGeometry=true (the default) and omits it when false.
            var includeFootprint = request.ReturnGeometry != false;

            // Build identify response
            var response = new IdentifyResponse
            {
                ObjectId = selectedRasters.Length == 1 ? selectedRasters[0].Id : null,
                Name = selectedRasters.Length == 1 ? selectedRasters[0].Name : $"{resolved.DisplayName} mosaic",
                Value = FormatPixelValues(pixelResult.BandValues),
                Location = new Point
                {
                    X = pixelResult.X,
                    Y = pixelResult.Y
                },
                Properties = CreateProperties(pixelResult, request.PixelSize),
                CatalogItems = request.ReturnCatalogItems == true
                    ? selectedRasters.Select(r => new CatalogItem
                    {
                        Id = r.Id,
                        Name = r.Name,
                        Footprint = includeFootprint ? BuildFootprint(r) : null,
                    }).ToArray()
                    : null
            };

            ImageServerLog.IdentifyCompleted(_logger, layerId, pixelResult.HasData, pixelResult.BandValues.Count);
            scope.SetSuccess(1);

            return Results.Json(response, ImageServerJsonContext.Default.IdentifyResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.IdentifyFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while identifying pixel values.");
        }
    }

    // Translates an Esri renderingRule into the canonical RasterIdentifyRendering threaded
    // onto the identify call. Reuses the shared raster-function planner so identify honours the
    // exact same Stretch/Colormap/Clip support matrix (and 400/501 error semantics) as
    // exportImage and computeClass.
    private static bool TryMapRenderingRule(
        string renderingRuleJson,
        out RasterIdentifyRendering? rendering,
        out string? error,
        out bool notImplemented)
    {
        rendering = null;
        error = null;
        notImplemented = false;

        RasterFunctionDocument document;
        try
        {
            document = JsonSerializer.Deserialize(renderingRuleJson, ImageServerJsonContext.Default.RasterFunctionDocument)
                ?? throw new JsonException("Empty renderingRule document.");
        }
        catch (JsonException)
        {
            error = "renderingRule must be a valid raster function document.";
            return false;
        }

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);
        if (!mapping.Supported)
        {
            error = mapping.Reason ?? "renderingRule is not supported on this service.";
            notImplemented = mapping.IsNotImplemented;
            return false;
        }

        // An Identity-only / StretchType=0 chain is an executable no-op: preserve the raw-value
        // contract by leaving rendering null so the store samples the source pixel. Band
        // arithmetic and terrain functions are now threaded through so the sampled value matches
        // the rendered exportImage output (identify parity, #1803).
        var resolved = new RasterIdentifyRendering(
            mapping.Stretch, mapping.Colormap, mapping.ClipRegion, mapping.BandArithmetic, mapping.Terrain);
        rendering = resolved.HasRendering ? resolved : null;
        return true;
    }

    private static bool IsSupportedGeometryType(string geometryType)
        => geometryType.Equals(PointGeometryType, StringComparison.OrdinalIgnoreCase) ||
           geometryType.Equals(EnvelopeGeometryType, StringComparison.OrdinalIgnoreCase) ||
           geometryType.Equals(PolygonGeometryType, StringComparison.OrdinalIgnoreCase);

    private static (double? x, double? y, int? srid) ParseGeometry(IdentifyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Geometry))
        {
            return (null, null, null);
        }

        if (request.Geometry.Length > MaxGeometryInputLength)
        {
            return (null, null, null);
        }

        var srid = SpatialReferenceHelpers.TryParseSrid(request.Sr);
        if (!string.IsNullOrWhiteSpace(request.Sr) && !srid.HasValue)
        {
            return (null, null, null);
        }

        // Handle point geometry string (e.g., "x,y")
        if (!request.Geometry.TrimStart().StartsWith('{') && request.Geometry.Contains(','))
        {
            var coords = request.Geometry.Split(',', StringSplitOptions.TrimEntries);
            if (coords.Length == 2 &&
                double.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                return (x, y, srid);
            }
        }

        // Handle JSON geometry: point ({x,y}), envelope, or polygon (rings). For area
        // geometries the centroid of the bounding envelope is the representative identify
        // location. ImageServerGeometryHelpers centralises the Esri-JSON envelope math so the
        // geometry parsing stays consistent with the compute/getSamples operations.
        if (request.Geometry.TrimStart().StartsWith('{'))
        {
            if (!ImageServerGeometryHelpers.TryGetEnvelope(request.Geometry, out var envelope, out _))
            {
                return (null, null, null);
            }

            var centerX = (envelope.XMin + envelope.XMax) / 2.0;
            var centerY = (envelope.YMin + envelope.YMax) / 2.0;

            // Explicit sr wins; otherwise honour the geometry's embedded spatialReference.
            return (centerX, centerY, srid ?? envelope.Srid);
        }

        return (null, null, null);
    }

    private static string FormatPixelValues(Dictionary<int, object?> bandValues)
    {
        if (bandValues.Count == 0)
            return "NoData";

        if (bandValues.Count == 1)
            return bandValues.First().Value?.ToString() ?? "NoData";

        // Format multi-band values
        var values = bandValues.OrderBy(kvp => kvp.Key)
            .Select(kvp => $"Band {kvp.Key}: {kvp.Value ?? "NoData"}")
            .ToArray();

        return string.Join("; ", values);
    }

    private static Dictionary<string, object?> CreateProperties(
        Core.Features.Raster.Domain.PixelValueResult pixelResult,
        int? pixelSize)
    {
        var properties = new Dictionary<string, object?>
        {
            ["HasData"] = pixelResult.HasData,
            ["Coordinates"] = $"{pixelResult.X}, {pixelResult.Y}",
            ["SRID"] = pixelResult.Srid,
            ["BandCount"] = pixelResult.BandValues.Count
        };

        // pixelSize is the requested sampling resolution. The shared raster identify
        // path samples at the source/mosaic native resolution today, so the requested
        // value is echoed back for transparency rather than silently dropped; resolution-
        // specific pyramid selection is deferred follow-up scope.
        if (pixelSize.HasValue)
        {
            properties["PixelSize"] = pixelSize.Value;
        }

        // Add individual band values
        foreach (var band in pixelResult.BandValues.OrderBy(kvp => kvp.Key))
        {
            properties[$"Band_{band.Key}"] = band.Value;
        }

        return properties;
    }

    /// <summary>
    /// Builds an Esri envelope object for a raster footprint from its extent, used to
    /// populate <see cref="CatalogItem.Footprint"/> when <c>returnGeometry</c> is true.
    /// </summary>
    private static ImageServerExtent? BuildFootprint(Core.Features.Raster.Domain.RasterInfo raster)
    {
        if (raster.Extent is not { } extent)
        {
            return null;
        }

        var srid = extent.Srid ?? 4326;
        return new ImageServerExtent
        {
            XMin = extent.XMin,
            YMin = extent.YMin,
            XMax = extent.XMax,
            YMax = extent.YMax,
            SpatialReference = new SpatialReference { Wkid = srid, LatestWkid = srid },
        };
    }
}
