// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Server.Features.ImageServer.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server identify operations.
/// Provides pixel value identification at geographic points.
/// </summary>
internal sealed class ImageServerIdentifyHandler
{
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
        int layerId,
        IdentifyRequest request,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;

        try
        {
            // Validate layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return Results.NotFound();
            }

            // Start telemetry activity
            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "identify",
                HonuaTelemetry.Protocols.ImageServer,
                layerId.ToString(CultureInfo.InvariantCulture));
            featureActivity?.SetTag(HonuaTelemetry.Tags.Operation, "identify-pixel");

            // Get raster data
            var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken);
            if (rasters.Length == 0)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return Results.NotFound();
            }

            // Parse geometry coordinates
            var (x, y, srid) = ParseGeometry(request);
            if (!x.HasValue || !y.HasValue)
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, "Invalid geometry coordinates");
                return Results.BadRequest("Invalid geometry coordinates");
            }

            ImageServerLog.IdentifyStarted(_logger, layerId, x.Value, y.Value);

            // Use the first raster (could be enhanced for multi-raster scenarios)
            var primaryRaster = rasters[0];

            // Identify pixel values
            var pixelResult = await _rasterStore.IdentifyAsync(
                layerId,
                primaryRaster.Id,
                x.Value,
                y.Value,
                srid,
                cancellationToken);

            // Build identify response
            var response = new IdentifyResponse
            {
                ObjectId = primaryRaster.Id,
                Name = primaryRaster.Name,
                Value = FormatPixelValues(pixelResult.BandValues),
                Location = new Point
                {
                    X = pixelResult.X,
                    Y = pixelResult.Y
                },
                Properties = CreateProperties(pixelResult),
                CatalogItems = request.ReturnCatalogItems == true
                    ? [new CatalogItem { Id = primaryRaster.Id, Name = primaryRaster.Name }]
                    : null
            };

            ImageServerLog.IdentifyCompleted(_logger, layerId, pixelResult.HasData, pixelResult.BandValues.Count);

            // Record telemetry success
            HonuaTelemetry.SetSuccess(featureActivity, 1);

            return Results.Ok(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.IdentifyFailed(_logger, ex, layerId);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return Results.Problem("An error occurred while identifying pixel values.", statusCode: 500);
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    private static (double? x, double? y, int? srid) ParseGeometry(IdentifyRequest request)
    {
        try
        {
            // Handle point geometry string (e.g., "x,y" or JSON format)
            if (request.Geometry.Contains(','))
            {
                var coords = request.Geometry.Split(',');
                if (coords.Length >= 2 &&
                    double.TryParse(coords[0], out var x) &&
                    double.TryParse(coords[1], out var y))
                {
                    var srid = SpatialReferenceHelpers.TryParseSrid(request.Sr);
                    return (x, y, srid);
                }
            }

            // Handle JSON geometry format
            if (request.Geometry.StartsWith('{'))
            {
                // Limit JSON size to prevent DoS
                if (request.Geometry.Length > 1000)
                {
                    return (null, null, null);
                }

                try
                {
                    using var geometryDoc = JsonDocument.Parse(request.Geometry);
                    if (geometryDoc.RootElement.TryGetProperty("x", out var xElement) &&
                        geometryDoc.RootElement.TryGetProperty("y", out var yElement))
                    {
                        if (xElement.TryGetDouble(out var x) && yElement.TryGetDouble(out var y))
                        {
                            var srid = SpatialReferenceHelpers.TryParseSrid(request.Sr);
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
        catch (Exception)
        {
            return (null, null, null);
        }
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
