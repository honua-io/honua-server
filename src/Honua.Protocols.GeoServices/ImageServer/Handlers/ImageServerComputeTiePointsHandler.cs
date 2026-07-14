// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Handler for the ImageServer <c>computeTiePoints</c> operation. Honua returns tie points
/// <b>only</b> when the raster carries pre-registered control points / ground control points in
/// its sensor metadata; those are passed through verbatim. Automatic feature detection /
/// descriptor matching (the way ArcGIS <i>derives</i> tie points) requires a computer-vision
/// dependency this repository bars, so it is out of scope by design — when no control points are
/// modeled the operation returns an actionable <c>501</c> rather than a fabricated result,
/// mirroring the DEM-height 501 discipline (#1879). See ADR-0064.
/// </summary>
internal sealed class ImageServerComputeTiePointsHandler
{
    /// <summary>
    /// Defensive upper bound on the number of pre-registered control points returned in one
    /// response, so a pathological metadata payload cannot produce an unbounded document.
    /// </summary>
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

    /// <summary>
    /// Computes tie points for the layer's primary raster by passing through its pre-registered
    /// control points. Returns 404 when the layer has no primary raster, 400 when a supplied
    /// <c>rasterId</c> is not a valid identifier, and 501 when no control points are modeled.
    /// </summary>
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
                // matcher we do not have (ADR-0064). Automatic tuning parameters (minRegionSize,
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
        catch (Exception ex)
        {
            ImageServerLog.ComputeTiePointsFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "ImageServer computeTiePoints failed.");
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}
