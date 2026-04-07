// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.SpatialAnalytics.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.SpatialAnalytics;

/// <summary>
/// Buffer aggregate handler. REST entry:
/// <c>POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate</c>.
/// OGC mirror: <c>POST /ogc/features/collections/{collectionId}/buffer-aggregate</c>.
/// </summary>
internal static partial class SpatialAnalyticsRequestHandlers
{
    private const string BufferAggregateOperation = "buffer-aggregate";

    public static async Task<IResult> HandleBufferAggregatePost(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var logger = context.RequestServices
            .GetService<ILoggerFactory>()?.CreateLogger("Honua.Server.Features.SpatialAnalytics.BufferAggregate");
        var editionError = RequireProEdition(context, BufferAggregateOperation, logger);
        if (editionError != null)
        {
            return editionError;
        }

        var cancellationToken = FeatureServerEndpoints.GetTimeoutAwareCancellationToken(context);

        var (values, readError) = await ReadRequestValuesAsync(context, cancellationToken);
        if (values == null)
        {
            return readError!;
        }

        var (layer, _, resourceError) = await ValidateRestResourceAsync(
            context, serviceId, layerId, cancellationToken);
        if (resourceError != null)
        {
            return resourceError;
        }

        return await HandleBufferAggregateCoreAsync(
            context,
            values,
            layer!,
            serviceId,
            HonuaTelemetry.Protocols.FeatureServer,
            logger,
            cancellationToken);
    }

    public static async Task<IResult> HandleOgcBufferAggregatePost(
        string collectionId,
        HttpContext context)
    {
        var logger = context.RequestServices
            .GetService<ILoggerFactory>()?.CreateLogger("Honua.Server.Features.SpatialAnalytics.BufferAggregate");
        var editionError = RequireProEdition(context, BufferAggregateOperation, logger);
        if (editionError != null)
        {
            return editionError;
        }

        var cancellationToken = FeatureServerEndpoints.GetTimeoutAwareCancellationToken(context);

        var (values, readError) = await ReadRequestValuesAsync(context, cancellationToken);
        if (values == null)
        {
            return readError!;
        }

        var (layer, resourceError) = await ValidateOgcResourceAsync(context, collectionId, cancellationToken);
        if (resourceError != null)
        {
            return resourceError;
        }

        return await HandleBufferAggregateCoreAsync(
            context,
            values,
            layer!,
            serviceId: null,
            HonuaTelemetry.Protocols.OgcFeatures,
            logger,
            cancellationToken);
    }

    private static async Task<IResult> HandleBufferAggregateCoreAsync(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        LayerDefinition layer,
        string? serviceId,
        string protocol,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        using var activity = StartAnalyticsActivity(BufferAggregateOperation, protocol, serviceId, layer.Id);

        if (logger != null)
        {
            SpatialAnalyticsLog.RequestReceived(logger, BufferAggregateOperation, layer.Id);
        }

        var startTimestamp = TimeProvider.System.GetTimestamp();
        var analyticsLimits = context.RequestServices
            .GetRequiredService<IOptions<LimitsOptions>>().Value.Analytics;

        // distance (required). Parsed as a non-negative number; the per-unit
        // upper bound is enforced after we know the unit so callers cannot
        // bypass MaxBufferDistanceMeters by switching to a larger unit.
        var distanceStr = GetValueString(values, SpatialAnalyticsParameters.BufferDistance);
        if (string.IsNullOrWhiteSpace(distanceStr) ||
            !double.TryParse(distanceStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) ||
            double.IsNaN(distance) || double.IsInfinity(distance) || distance < BufferAggregateQuery.MinDistanceMeters)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "distance parameter is required",
                [$"distance must be a non-negative number ≥ {BufferAggregateQuery.MinDistanceMeters}."]);
        }

        // unit (default: meters).
        var unitStr = GetValueString(values, SpatialAnalyticsParameters.BufferUnit);
        DistanceUnit unit;
        if (string.IsNullOrWhiteSpace(unitStr))
        {
            unit = DistanceUnit.Meters;
        }
        else
        {
            switch (unitStr.ToLowerInvariant())
            {
                case "meters":
                case "meter":
                case "m":
                    unit = DistanceUnit.Meters;
                    break;
                case "kilometers":
                case "kilometer":
                case "km":
                    unit = DistanceUnit.Kilometers;
                    break;
                case "feet":
                case "foot":
                case "ft":
                    unit = DistanceUnit.Feet;
                    break;
                case "miles":
                case "mile":
                case "mi":
                    unit = DistanceUnit.Miles;
                    break;
                default:
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid unit",
                        ["unit must be one of: meters, kilometers, feet, miles."]);
            }
        }

        // Cap the buffer in meters so non-meter units cannot bypass the limit
        // (e.g. distance=80 unit=miles → ~128.7 km vs a 100 km cap).
        var distanceInMeters = ConvertDistanceToMeters(distance, unit);
        if (distanceInMeters > analyticsLimits.MaxBufferDistanceMeters)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid distance",
                [$"distance must not exceed {analyticsLimits.MaxBufferDistanceMeters} meters (in the supplied unit)."]);
        }

        activity?.SetTag("buffer.distance", distance);
        activity?.SetTag("buffer.distance_meters", distanceInMeters);

        // dissolve (default: true — one dissolved polygon per group).
        var dissolve = true;
        var dissolveStr = GetValueString(values, SpatialAnalyticsParameters.Dissolve);
        if (!string.IsNullOrWhiteSpace(dissolveStr) &&
            !bool.TryParse(dissolveStr, out dissolve))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid dissolve",
                ["dissolve must be 'true' or 'false'."]);
        }

        // groupByFields (optional comma-separated list).
        ImmutableArray<string>? groupByFields = null;
        var groupByStr = GetValueString(values, SpatialAnalyticsParameters.GroupByFields);
        if (!string.IsNullOrWhiteSpace(groupByStr))
        {
            var parsed = groupByStr
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parsed.Length > 0)
            {
                groupByFields = [.. parsed];
            }
        }

        // outStatistics (optional).
        if (!TryParseOutStatistics(context, values, out var outStatistics, out var outStatsError))
        {
            return outStatsError!;
        }

        // Feature query.
        if (!TryBuildFeatureQuery(context, values, layer, out var featureQuery, out var filterError))
        {
            return filterError!;
        }

        var bufferQuery = new BufferAggregateQuery
        {
            Distance = distance,
            Unit = unit,
            Dissolve = dissolve,
            GroupByFields = groupByFields,
            OutStatistics = outStatistics,
            MaxInputFeatures = analyticsLimits.MaxInputFeatures
        };

        var reader = context.RequestServices.GetRequiredService<ISpatialAnalyticsReader>();

        ImmutableArray<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = await reader.QueryBufferAggregateAsync(layer.Id, featureQuery, bufferQuery, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (logger != null)
            {
                SpatialAnalyticsLog.RequestFailed(logger, BufferAggregateOperation, layer.Id, ex.Message, ex);
            }
            return CreateReaderFailureResult(context, BufferAggregateOperation, ex);
        }

        // Buffer aggregate is bounded by MaxInputFeatures (no distinct result cap).
        var (inputTruncated, _) = DetectOverflow(rows.Length, analyticsLimits.MaxInputFeatures, maxOutputRows: null);
        if (inputTruncated && logger != null)
        {
            SpatialAnalyticsLog.InputTruncated(logger, BufferAggregateOperation, layer.Id, analyticsLimits.MaxInputFeatures);
        }

        rows = TrimOverflowRows(rows, analyticsLimits.MaxInputFeatures, maxOutputRows: null);

        var features = new SpatialAnalyticsFeature[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            features[i] = MapRowToFeature(rows[i]);
        }

        var elapsed = TimeProvider.System.GetElapsedTime(startTimestamp);
        if (logger != null)
        {
            SpatialAnalyticsLog.RequestCompleted(
                logger, BufferAggregateOperation, layer.Id, features.Length, elapsed.TotalMilliseconds);
        }

        var response = new SpatialAnalyticsFeatureCollection
        {
            Features = features,
            NumberReturned = features.Length,
            Metadata = new SpatialAnalyticsMetadata
            {
                Operation = BufferAggregateOperation,
                InputTruncated = inputTruncated,
                ResultTruncated = false,
                MaxInputFeatures = analyticsLimits.MaxInputFeatures,
                MaxOutputRows = null
            }
        };

        return WriteAnalyticsResponse(response);
    }

    /// <summary>
    /// Converts a distance value to meters using the same factors the
    /// PostgreSQL <c>GeometryProcessor.ConvertDistanceToMeters</c> uses, so the
    /// server-side cap check stays in sync with what the SQL builder will
    /// actually emit. Inlined to avoid a Postgres dependency from the slice.
    /// </summary>
    private static double ConvertDistanceToMeters(double distance, DistanceUnit unit) => unit switch
    {
        DistanceUnit.Meters => distance,
        DistanceUnit.Feet => distance * 0.3048d,
        DistanceUnit.Kilometers => distance * 1000d,
        DistanceUnit.Miles => distance * 1609.344d,
        _ => distance
    };
}
