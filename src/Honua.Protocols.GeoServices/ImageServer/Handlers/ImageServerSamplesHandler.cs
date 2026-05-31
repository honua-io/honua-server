// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for the Image Server <c>getSamples</c> operation. Samples pixel values at
/// the points of (or vertices along) a geometry, reusing the shared raster identify
/// pipeline rather than building a parallel raster reader.
/// </summary>
internal sealed class ImageServerSamplesHandler
{
    /// <summary>
    /// Default cap on the number of sampled points. Matches the Esri default
    /// <c>sampleCount</c> ceiling and bounds per-point identify work.
    /// </summary>
    private const int DefaultMaxSampleCount = 1000;

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerSamplesHandler> _logger;

    public ImageServerSamplesHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ILogger<ImageServerSamplesHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Samples pixel values at the requested geometry locations.
    /// </summary>
    public async Task<IResult> GetSamplesAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "get-samples",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "get-samples");

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            if (!IsSupportedFormat(GetString(values, "f")))
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, "Unsupported format");
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Only JSON format is supported. Use f=json or f=pjson.");
            }

            if (!ImageServerGeometryHelpers.TryGetSamplePoints(GetString(values, "geometry"), out var samplePoints, out var geometryError))
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, geometryError ?? "Invalid geometry");
                return StandardErrorHelpers.CreateBadRequest(context, geometryError ?? "Invalid geometry.");
            }

            if (!ImageServerMosaicHelpers.TryParseTime(GetString(values, "time"), out var timestamp, out var timeError))
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, timeError ?? "Invalid time");
                return StandardErrorHelpers.CreateBadRequest(context, timeError ?? "Invalid time.");
            }

            var editionError = ImageServerMosaicHelpers.RequireTemporalMosaicAccess(context, timestamp);
            if (editionError != null)
            {
                return editionError;
            }

            var requestSrid = ImageServerGeometryHelpers.TryParseSrid(GetString(values, "sr"));
            var maxSampleCount = ResolveSampleCount(GetString(values, "sampleCount"));
            var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, GetString(values, "mosaicRule"));

            var samples = new List<SampleEntry>(Math.Min(samplePoints.Count, maxSampleCount));
            foreach (var point in samplePoints)
            {
                if (samples.Count >= maxSampleCount)
                {
                    break;
                }

                var srid = point.Srid ?? requestSrid;
                var selectionQuery = new RasterSelectionQuery
                {
                    Geometry = ImageServerMosaicHelpers.CreatePointGeometry(point.X, point.Y),
                    GeometrySrid = srid,
                    Timestamp = timestamp,
                };

                var selectedRasters = await _rasterStore.QueryRastersAsync(layerId, selectionQuery, cancellationToken);
                if (selectedRasters.Length == 0)
                {
                    continue;
                }

                var pixelResult = selectedRasters.Length == 1
                    ? await _rasterStore.IdentifyAsync(layerId, selectedRasters[0].Id, point.X, point.Y, srid, cancellationToken)
                    : await _rasterStore.IdentifyMosaicAsync(
                        layerId,
                        selectedRasters.Select(r => r.Id).ToArray(),
                        mergeStrategy,
                        point.X,
                        point.Y,
                        srid,
                        cancellationToken);

                samples.Add(new SampleEntry
                {
                    RasterId = selectedRasters.Length == 1 ? selectedRasters[0].Id : null,
                    Location = new SampleLocation
                    {
                        X = pixelResult.X,
                        Y = pixelResult.Y,
                        SpatialReference = srid is null ? null : new SpatialReference { Wkid = srid.Value, LatestWkid = srid.Value },
                    },
                    Value = FormatValue(pixelResult.BandValues),
                    Resolution = ResolveResolution(selectedRasters),
                    Attributes = BuildAttributes(pixelResult.BandValues),
                });
            }

            ImageServerLog.IdentifyCompleted(_logger, layerId, samples.Count > 0, samples.Count);
            scope.SetSuccess(samples.Count);

            var response = new GetSamplesResponse { Samples = samples.ToArray() };
            return Results.Json(response, ImageServerJsonContext.Default.GetSamplesResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.IdentifyFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while sampling pixel values.");
        }
    }

    private static int ResolveSampleCount(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            value > 0)
        {
            return Math.Min(value, DefaultMaxSampleCount);
        }

        return DefaultMaxSampleCount;
    }

    private static double? ResolveResolution(RasterInfo[] rasters)
    {
        var resolution = rasters
            .Select(r => r.GeoTransform is { Length: >= 2 } ? Math.Abs(r.GeoTransform[1]) : 0d)
            .Where(v => v > 0)
            .DefaultIfEmpty(0d)
            .Min();
        return resolution > 0 ? resolution : null;
    }

    private static string FormatValue(Dictionary<int, object?> bandValues)
    {
        if (bandValues.Count == 0)
        {
            return "NoData";
        }

        return string.Join(' ', bandValues
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => kvp.Value?.ToString() ?? "NoData"));
    }

    private static Dictionary<string, object?> BuildAttributes(Dictionary<int, object?> bandValues)
    {
        var attributes = new Dictionary<string, object?>(bandValues.Count);
        foreach (var band in bandValues.OrderBy(kvp => kvp.Key))
        {
            attributes[$"Band_{band.Key}"] = band.Value;
        }

        return attributes;
    }

    private static bool IsSupportedFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ||
           string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}
