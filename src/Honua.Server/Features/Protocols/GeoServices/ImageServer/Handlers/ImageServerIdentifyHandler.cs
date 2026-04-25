// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server identify operations.
/// Provides pixel value identification at geographic points.
/// </summary>
internal sealed class ImageServerIdentifyHandler
{
    private const int MaxGeometryInputLength = 1000;
    private const string SupportedGeometryType = "esriGeometryPoint";

    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerIdentifyHandler> _logger;

    public ImageServerIdentifyHandler(
        ILayerCatalog layerCatalog,
        IRasterStore rasterStore,
        ILogger<ImageServerIdentifyHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
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
            // Validate layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            // Resolve the primary raster without scanning the entire layer.
            var primaryRaster = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken);
            if (primaryRaster is null)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }
            var primaryRasterInfo = primaryRaster.Value;

            if (!string.IsNullOrWhiteSpace(request.GeometryType) &&
                !string.Equals(request.GeometryType, SupportedGeometryType, StringComparison.OrdinalIgnoreCase))
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, "Unsupported geometry type");
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Unsupported geometryType '{request.GeometryType}'. Only {SupportedGeometryType} is supported.");
            }

            // Parse geometry coordinates
            var (x, y, srid) = ParseGeometry(request);
            if (!x.HasValue || !y.HasValue)
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, "Invalid geometry coordinates");
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid geometry coordinates");
            }

            ImageServerLog.IdentifyStarted(_logger, layerId, x.Value, y.Value);

            // Identify pixel values
            var pixelResult = await _rasterStore.IdentifyAsync(
                layerId,
                primaryRasterInfo.Id,
                x.Value,
                y.Value,
                srid,
                cancellationToken);

            // Build identify response
            var response = new IdentifyResponse
            {
                ObjectId = primaryRasterInfo.Id,
                Name = primaryRasterInfo.Name,
                Value = FormatPixelValues(pixelResult.BandValues),
                Location = new Point
                {
                    X = pixelResult.X,
                    Y = pixelResult.Y
                },
                Properties = CreateProperties(pixelResult),
                CatalogItems = request.ReturnCatalogItems == true
                    ? [new CatalogItem { Id = primaryRasterInfo.Id, Name = primaryRasterInfo.Name }]
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

        // Handle point geometry string (e.g., "x,y" or JSON format)
        if (request.Geometry.Contains(','))
        {
            var coords = request.Geometry.Split(',', StringSplitOptions.TrimEntries);
            if (coords.Length == 2 &&
                double.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                return (x, y, srid);
            }
        }

        // Handle JSON geometry format
        if (request.Geometry.StartsWith('{'))
        {
            try
            {
                using var geometryDoc = JsonDocument.Parse(request.Geometry);
                if (geometryDoc.RootElement.TryGetProperty("x", out var xElement) &&
                    geometryDoc.RootElement.TryGetProperty("y", out var yElement))
                {
                    if (xElement.TryGetDouble(out var x) && yElement.TryGetDouble(out var y))
                    {
                        return (x, y, srid);
                    }
                }
            }
            catch (JsonException)
            {
                return (null, null, null);
            }
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

    private static Dictionary<string, object?> CreateProperties(Core.Features.Raster.Domain.PixelValueResult pixelResult)
    {
        var properties = new Dictionary<string, object?>
        {
            ["HasData"] = pixelResult.HasData,
            ["Coordinates"] = $"{pixelResult.X}, {pixelResult.Y}",
            ["SRID"] = pixelResult.Srid,
            ["BandCount"] = pixelResult.BandValues.Count
        };

        // Add individual band values
        foreach (var band in pixelResult.BandValues.OrderBy(kvp => kvp.Key))
        {
            properties[$"Band_{band.Key}"] = band.Value;
        }

        return properties;
    }
}
