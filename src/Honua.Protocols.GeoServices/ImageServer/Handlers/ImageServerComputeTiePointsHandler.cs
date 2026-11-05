// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

internal sealed class ImageServerComputeTiePointsHandler
{
    // The control-point list is capped so a metadata payload cannot return an unbounded document.
    private const int MaxTiePoints = 10_000;

    private const string NoControlPointsMessage =
        "tie-point computation requires pre-registered control points / GCPs on this raster; " +
        "automatic feature matching is not supported on this service.";

    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerComputeTiePointsHandler> _logger;

    public ImageServerComputeTiePointsHandler(
        IRasterStore rasterStore,
        ILogger<ImageServerComputeTiePointsHandler> logger)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IResult> ComputeTiePointsAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "computeTiePoints",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "computeTiePoints")
            .WithTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

        try
        {
            var primary = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken).ConfigureAwait(false);
            if (primary is not { } raster || raster.Id <= 0)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            // rasterId selects the source raster in the mosaic. When supplied it must be a valid
            // identifier; when omitted we resolve control points on the layer's primary raster.
            var rasterId = raster.Id;
            var rasterIdRaw = GetString(values, "rasterId") ?? GetString(values, "imageID");
            if (!string.IsNullOrWhiteSpace(rasterIdRaw))
            {
                if (!long.TryParse(rasterIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRasterId) ||
                    parsedRasterId <= 0)
                {
                    ImageServerLog.InvalidComputeTiePointsParameters(_logger, layerId, "rasterId must be a positive integer.");
                    return StandardErrorHelpers.CreateBadRequest(context, "rasterId must be a positive integer.");
                }

                rasterId = parsedRasterId;
            }

            var metadata = await _rasterStore.GetSensorMetadataAsync([rasterId], cancellationToken).ConfigureAwait(false);
            var sensor = metadata.TryGetValue(rasterId, out var meta) ? meta : raster.SensorMetadata;

            var controlPoints = ImageServerSensorModel.ReadControlPoints(sensor, raster.Srid);
            if (controlPoints.Count == 0)
            {
                // No pre-registered control points: be honest (501) rather than invoking a feature
                // matcher we do not have (ADR-0065). Automatic tuning parameters (minRegionSize,
                // maxLevel, skipFactor, searchSize, similarity) are accepted for wire compatibility
                // but have no effect because no matching is performed.
                return StandardErrorHelpers.CreateNotImplemented(context, NoControlPointsMessage);
            }

            var count = Math.Min(controlPoints.Count, MaxTiePoints);
            var sourcePoints = new ImageServerTiePoint[count];
            var targetPoints = new ImageServerTiePoint[count];
            for (var i = 0; i < count; i++)
            {
                var point = controlPoints[i];
                sourcePoints[i] = new ImageServerTiePoint
                {
                    X = point.ImageX,
                    Y = point.ImageY,
                    SpatialReference = point.ImageSrid is int imageSrid
                        ? new SpatialReference { Wkid = imageSrid, LatestWkid = imageSrid }
                        : null,
                };
                targetPoints[i] = new ImageServerTiePoint
                {
                    X = point.ReferenceX,
                    Y = point.ReferenceY,
                    Z = point.ReferenceZ,
                    SpatialReference = point.ReferenceSrid is int referenceSrid
                        ? new SpatialReference { Wkid = referenceSrid, LatestWkid = referenceSrid }
                        : null,
                };
            }

            var response = new ImageServerComputeTiePointsResponse
            {
                TiePoints = new ImageServerTiePointSet
                {
                    SourcePoints = sourcePoints,
                    TargetPoints = targetPoints,
                },
            };

            ImageServerLog.ComputeTiePointsCompleted(_logger, layerId, count);
            scope.SetSuccess(count);
            return Results.Json(response, ImageServerJsonContext.Default.ImageServerComputeTiePointsResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is a top-level protocol request handler; any unexpected
        // failure must map to a generic 500 rather than crash the host or leak internals.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ImageServerLog.ComputeTiePointsFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "ImageServer computeTiePoints failed.");
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}
