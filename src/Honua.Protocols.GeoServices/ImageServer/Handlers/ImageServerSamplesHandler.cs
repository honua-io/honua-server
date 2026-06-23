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
    private readonly ZarrPointSampler _zarrPointSampler;
    private readonly ILogger<ImageServerSamplesHandler> _logger;

    public ImageServerSamplesHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ZarrPointSampler zarrPointSampler,
        ILogger<ImageServerSamplesHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _zarrPointSampler = zarrPointSampler ?? throw new ArgumentNullException(nameof(zarrPointSampler));
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

            // multidimensionalDefinition selects a per-slice (time/StdZ/elevation coordinate) view
            // of a registered multidimensional cube. The requested dimension coordinate is resolved
            // to a Zarr slice index and the pinned cell is read through the shared Zarr subset
            // pipeline (#1869). Layers without a servable Zarr store stay metadata-only and return
            // an honest 501 rather than silently sampling the dimension-collapsed raster.
            if (!ImageServerMultidimensionalDefinition.TryParse(
                    GetString(values, "multidimensionalDefinition"), out var dimensionConstraints, out var multidimError))
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, multidimError ?? "Invalid multidimensionalDefinition");
                return StandardErrorHelpers.CreateBadRequest(context, multidimError ?? "Invalid multidimensionalDefinition.");
            }

            if (dimensionConstraints.Count > 0)
            {
                return await GetMultidimensionalSamplesAsync(
                        context, layerId, samplePoints, dimensionConstraints, GetString(values, "sr"), cancellationToken)
                    .ConfigureAwait(false);
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
            var processedPoints = 0;
            foreach (var point in samplePoints)
            {
                // Cap the INPUT points processed (Esri sampleCount semantics), not just
                // successful hits — otherwise a huge geometry whose vertices fall outside
                // raster coverage drives one raster-store query per vertex.
                if (processedPoints++ >= maxSampleCount)
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
                    ? await _rasterStore.IdentifyAsync(layerId, selectedRasters[0].Id, point.X, point.Y, srid, cancellationToken: cancellationToken)
                    : await _rasterStore.IdentifyMosaicAsync(
                        layerId,
                        selectedRasters.Select(r => r.Id).ToArray(),
                        mergeStrategy,
                        point.X,
                        point.Y,
                        srid,
                        cancellationToken: cancellationToken);

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

    /// <summary>
    /// Samples each requested point on the slice pinned by a
    /// <c>multidimensionalDefinition</c> against the layer's servable Zarr store
    /// (#1869). Returns a 501 when the layer has no readable multidimensional
    /// backing store, and a 400 when the requested slice cannot be resolved.
    /// </summary>
    private async Task<IResult> GetMultidimensionalSamplesAsync(
        HttpContext context,
        int layerId,
        IReadOnlyList<ImageServerGeometryHelpers.SamplePoint> samplePoints,
        IReadOnlyList<ImageServerDimensionConstraint> constraints,
        string? srRaw,
        CancellationToken cancellationToken)
    {
        var registration = await _zarrPointSampler.FindServableRegistrationAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            const string message =
                "getSamples multidimensionalDefinition (per-slice sampling of a multidimensional cube) requires a registered Zarr coverage for this layer; none is available.";
            ImageServerLog.InvalidIdentifyParameters(_logger, layerId, message);
            return StandardErrorHelpers.CreateNotImplemented(context, message);
        }

        var requestSrid = ImageServerGeometryHelpers.TryParseSrid(srRaw);
        var maxSampleCount = DefaultMaxSampleCount;
        var samples = new List<SampleEntry>(Math.Min(samplePoints.Count, maxSampleCount));
        var processedPoints = 0;

        foreach (var point in samplePoints)
        {
            if (processedPoints++ >= maxSampleCount)
            {
                break;
            }

            var srid = point.Srid ?? requestSrid;
            var (ok, value, error) = await _zarrPointSampler
                .TrySampleAsync(registration, point.X, point.Y, constraints, cancellationToken)
                .ConfigureAwait(false);

            if (!ok)
            {
                // A point outside the grid yields no sample (consistent with the 2D path);
                // a genuine resolution error (unknown axis, out-of-range coordinate) is a 400.
                if (IsPointOutsideError(error))
                {
                    continue;
                }
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, error ?? "multidimensionalDefinition could not be resolved");
                return StandardErrorHelpers.CreateBadRequest(context, error ?? "multidimensionalDefinition could not be resolved.");
            }

            samples.Add(new SampleEntry
            {
                RasterId = null,
                Location = new SampleLocation
                {
                    X = point.X,
                    Y = point.Y,
                    SpatialReference = srid is null ? null : new SpatialReference { Wkid = srid.Value, LatestWkid = srid.Value },
                },
                Value = ZarrPointSampler.FormatValue(value),
                Resolution = null,
                Attributes = new Dictionary<string, object?> { ["Value"] = value },
            });
        }

        ImageServerLog.IdentifyCompleted(_logger, layerId, samples.Count > 0, samples.Count);
        var response = new GetSamplesResponse { Samples = samples.ToArray() };
        return Results.Json(response, ImageServerJsonContext.Default.GetSamplesResponse);
    }

    private static bool IsPointOutsideError(string? error)
        => error is not null && error.Contains("outside the coverage extent", StringComparison.Ordinal);

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
