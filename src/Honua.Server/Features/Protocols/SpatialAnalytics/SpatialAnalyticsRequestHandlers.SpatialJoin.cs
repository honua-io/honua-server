// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Protocols.GeoServices;
using Honua.Infrastructure.Analytics;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.Server.Features.Protocols.SpatialAnalytics.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.SpatialAnalytics;

internal static partial class SpatialAnalyticsRequestHandlers
{
    private const string SpatialJoinOperation = "spatial-join";

    public static async Task<IResult> HandleSpatialJoinPost(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var logger = context.RequestServices
            .GetService<ILoggerFactory>()?.CreateLogger("Honua.Server.Features.Protocols.SpatialAnalytics.SpatialJoin");
        var editionError = RequireProEdition(context, SpatialJoinOperation, logger);
        if (editionError != null)
        {
            return editionError;
        }

        var cancellationToken = GeoServicesRequestValueHelpers.GetTimeoutAwareCancellationToken(context);

        var (values, readError) = await ReadRequestValuesAsync(context, cancellationToken);
        if (values == null)
        {
            return readError!;
        }

        var (targetLayerId, targetResource, _, resourceError) = await ValidateRestResourceAsync(
            context, serviceId, layerId, cancellationToken);
        if (resourceError != null)
        {
            return resourceError;
        }

        return await HandleSpatialJoinCoreAsync(
            context,
            values,
            targetLayerId,
            targetResource!,
            serviceId,
            HonuaTelemetry.Protocols.FeatureServer,
            FeatureServerProtocolName,
            logger,
            cancellationToken);
    }

    public static async Task<IResult> HandleOgcSpatialJoinPost(
        string collectionId,
        HttpContext context)
    {
        var logger = context.RequestServices
            .GetService<ILoggerFactory>()?.CreateLogger("Honua.Server.Features.Protocols.SpatialAnalytics.SpatialJoin");
        var editionError = RequireProEdition(context, SpatialJoinOperation, logger);
        if (editionError != null)
        {
            return editionError;
        }

        var cancellationToken = GeoServicesRequestValueHelpers.GetTimeoutAwareCancellationToken(context);

        var (values, readError) = await ReadRequestValuesAsync(context, cancellationToken);
        if (values == null)
        {
            return readError!;
        }

        var (targetLayerId, targetResource, resourceError) = await ValidateOgcResourceAsync(context, collectionId, cancellationToken);
        if (resourceError != null)
        {
            return resourceError;
        }

        return await HandleSpatialJoinCoreAsync(
            context,
            values,
            targetLayerId,
            targetResource!,
            serviceId: null,
            HonuaTelemetry.Protocols.OgcFeatures,
            OgcFeaturesProtocolName,
            logger,
            cancellationToken);
    }

    private static async Task<IResult> HandleSpatialJoinCoreAsync(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        int targetLayerId,
        MetadataV2Object targetResource,
        string? serviceId,
        string protocol,
        string catalogProtocol,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        using var activity = StartAnalyticsActivity(SpatialJoinOperation, protocol, serviceId, targetLayerId);

        if (logger != null)
        {
            SpatialAnalyticsLog.RequestReceived(logger, SpatialJoinOperation, targetLayerId);
        }

        var startTimestamp = TimeProvider.System.GetTimestamp();
        var analyticsLimits = context.RequestServices
            .GetRequiredService<IOptions<LimitsOptions>>().Value.Analytics;

        // joinLayerId (required).
        var joinLayerStr = GetValueString(values, SpatialAnalyticsParameters.JoinLayerId);
        if (string.IsNullOrWhiteSpace(joinLayerStr) ||
            !int.TryParse(joinLayerStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var joinLayerId))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "joinLayerId parameter is required",
                ["joinLayerId must be an integer layer identifier."]);
        }

        // Resolve and authorize the join layer through the same shared pipeline
        // used by the target. This pulls in the join layer's parent service so
        // both the layer + service access policy AND the protocol-enabled gate
        // are evaluated, matching design.md Q4 ("resolve the join layer through
        // IResourceValidator exactly like the target"). Without this the caller
        // could read any layer in the catalog as a join input, even when its
        // parent service is disabled or has stricter access policy than the
        // target.
        var joinLayerValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            joinLayerId,
            scope: AccessScope.Read,
            requiredProtocol: catalogProtocol,
            cancellationToken: cancellationToken);
        if (!joinLayerValidation.IsValid)
        {
            return joinLayerValidation.ErrorResult!;
        }

        var joinStorageLayerId = joinLayerValidation.Snapshot is not null && joinLayerValidation.Publication is not null
            ? joinLayerValidation.Snapshot.ResolveStorageLayerId(joinLayerValidation.Publication, joinLayerValidation.Resource)
            : null;
        if (!joinStorageLayerId.HasValue)
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                $"Layer {joinLayerId} is not available for spatial analytics.");
        }

        if (joinStorageLayerId.Value == targetLayerId)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid joinLayerId",
                ["joinLayerId must differ from the target layerId."]);
        }

        activity?.SetTag("spatialJoin.joinLayerId", joinStorageLayerId.Value);

        // Capture the join layer's stored SRID so the SQL builder can tag the
        // join geometry column (Bytea storage) and wrap it with ST_Transform when
        // the two layers live in different CRSs. Without this the join would tag
        // the join geometry with the target's SRID and either silently produce
        // wrong results (mixed CRS comparison) or surface a confusing PostGIS
        // error at execution. ValidateLayerWithAccessV2Async already authorized
        // the join layer's parent service so it is safe to read its metadata.
        var joinLayerSrid = joinLayerValidation.Resource!.ReadSrid() ?? SpatialReference.WGS84.ToSrid();

        // Predicate and distance (DWithin only).
        var (predicate, distanceMeters, predicateError) = ParseSpatialJoinPredicateAndDistance(context, values, analyticsLimits);
        if (predicateError is not null)
        {
            return predicateError;
        }

        activity?.SetTag("spatialJoin.predicate", predicate.ToString());

        // carryFields (optional comma-separated list).
        ImmutableArray<string>? carryFields = null;
        var carryStr = GetValueString(values, SpatialAnalyticsParameters.CarryFields);
        if (!string.IsNullOrWhiteSpace(carryStr))
        {
            var parsed = carryStr
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parsed.Length > 0)
            {
                var fieldError = ValidateFieldNameList(context, "carryFields", parsed)
                    ?? ValidateFieldsVisible(context, new FieldVisibilityCheck
                    {
                        ParameterName = "carryFields",
                        Fields = parsed,
                        Resource = joinLayerValidation.Resource!
                    });
                if (fieldError != null)
                {
                    return fieldError;
                }

                carryFields = [.. parsed];
            }
        }

        // outStatistics (optional).
        if (!TryParseOutStatistics(context, values, out var outStatistics, out var outStatsError))
        {
            return outStatsError!;
        }

        var outStatsVisibilityError = ValidateOutStatisticsVisible(context, outStatistics, joinLayerValidation.Resource!);
        if (outStatsVisibilityError != null)
        {
            return outStatsVisibilityError;
        }

        outStatistics = AnalyticsFeatureQueryFactory.ApplyStatisticFieldTypes(outStatistics, joinLayerValidation.Resource!);

        // Feature query for the target layer (where + SQL filter + objectIds + spatial + temporal).
        var (featureQuery, filterError) = await AnalyticsFeatureQueryFactory.TryBuildAsync(
            context, values, targetResource, cancellationToken);
        if (featureQuery == null)
        {
            return filterError!;
        }

        var joinQuery = new SpatialJoinQuery
        {
            JoinLayerId = joinStorageLayerId.Value,
            JoinLayerSrid = joinLayerSrid,
            Predicate = predicate,
            DistanceMeters = distanceMeters,
            CarryFields = carryFields,
            OutStatistics = outStatistics,
            MaxInputFeatures = analyticsLimits.MaxInputFeatures
        };

        if (!TryGetAnalyticsReader(context, SpatialJoinOperation, logger, out var reader, out var readerError))
        {
            return readerError!;
        }

        ImmutableArray<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = await reader.QuerySpatialJoinAsync(targetLayerId, featureQuery.Value, joinQuery, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: this is the spatial-join
            // reader call boundary; the failure is logged and mapped to a generic error
            // response below.
            LogRequestFailed(logger, (SpatialJoinOperation, targetLayerId), ex);
            return CreateReaderFailureResult(context, SpatialJoinOperation, ex);
        }

        // Spatial join is bounded by MaxInputFeatures (no per-result cap). The
        // SQL builder emits one row per target row, so rows.Length already
        // reflects the input count after the LIMIT n+1 guard fired.
        var (inputTruncated, _) = DetectOverflow(
            rows.Length, rows.Length, analyticsLimits.MaxInputFeatures, maxOutputRows: null);
        if (inputTruncated && logger != null)
        {
            SpatialAnalyticsLog.InputTruncated(logger, SpatialJoinOperation, targetLayerId, analyticsLimits.MaxInputFeatures);
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
                logger, SpatialJoinOperation, targetLayerId, features.Length, elapsed.TotalMilliseconds);
        }

        var response = new SpatialAnalyticsFeatureCollection
        {
            Features = features,
            NumberReturned = features.Length,
            Metadata = new SpatialAnalyticsMetadata
            {
                Operation = SpatialJoinOperation,
                InputTruncated = inputTruncated,
                ResultTruncated = false,
                MaxInputFeatures = analyticsLimits.MaxInputFeatures,
                MaxOutputRows = null
            }
        };

        return WriteAnalyticsResponse(response);
    }

    private sealed class FieldVisibilityCheck
    {
        public required string ParameterName { get; init; }
        public required string[] Fields { get; init; }
        public required MetadataV2Object Resource { get; init; }
    }

    private static (SpatialJoinPredicate Predicate, double? DistanceMeters, IResult? Error) ParseSpatialJoinPredicateAndDistance(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        AnalyticsLimits analyticsLimits)
    {
        var predicateStr = GetValueString(values, SpatialAnalyticsParameters.Predicate);
        var predicate = SpatialJoinPredicate.Intersects;
        if (!string.IsNullOrWhiteSpace(predicateStr))
        {
            switch (predicateStr.ToLowerInvariant())
            {
                case "intersects": predicate = SpatialJoinPredicate.Intersects; break;
                case "contains": predicate = SpatialJoinPredicate.TargetContainsJoin; break;
                case "within": predicate = SpatialJoinPredicate.JoinContainsTarget; break;
                case "dwithin": predicate = SpatialJoinPredicate.DWithin; break;
                default:
                    return (default, null, StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid predicate",
                        ["predicate must be one of: intersects, contains, within, dwithin."]));
            }
        }

        var (distanceMeters, distanceError) = ParseDWithinDistance(context, values, analyticsLimits);
        if (distanceError is not null)
        {
            return (default, null, distanceError);
        }

        if (predicate == SpatialJoinPredicate.DWithin && !distanceMeters.HasValue)
        {
            return (default, null, StandardErrorHelpers.CreateBadRequest(context,
                "distance parameter is required",
                ["distance (meters) is required when predicate is 'dwithin'."]));
        }

        return (predicate, distanceMeters, null);
    }

    private static (double? DistanceMeters, IResult? Error) ParseDWithinDistance(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        AnalyticsLimits analyticsLimits)
    {
        var distanceStr = GetValueString(values, SpatialAnalyticsParameters.Distance);
        if (string.IsNullOrWhiteSpace(distanceStr))
        {
            return (null, null);
        }

        if (!double.TryParse(distanceStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var distanceValue) ||
            double.IsNaN(distanceValue) || double.IsInfinity(distanceValue) || distanceValue <= 0d)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid distance",
                ["distance must be a positive number of meters."]));
        }

        if (distanceValue > analyticsLimits.MaxDWithinDistanceMeters)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid distance",
                [$"distance must not exceed {analyticsLimits.MaxDWithinDistanceMeters} meters."]));
        }

        return (distanceValue, null);
    }

    private static IResult? ValidateFieldsVisible(HttpContext context, FieldVisibilityCheck check)
    {
        for (var i = 0; i < check.Fields.Length; i++)
        {
            if (check.Resource.IsHiddenSchemaField(check.Fields[i]))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Invalid {check.ParameterName}",
                    [$"Unknown field '{check.Fields[i]}' in {check.ParameterName}."]);
            }
        }

        return null;
    }

    private static IResult? ValidateOutStatisticsVisible(
        HttpContext context,
        ImmutableArray<StatisticDefinition>? outStatistics,
        MetadataV2Object resource)
    {
        if (!outStatistics.HasValue || outStatistics.Value.IsDefaultOrEmpty)
        {
            return null;
        }

        foreach (var statistic in outStatistics.Value)
        {
            if (resource.IsHiddenSchemaField(statistic.OnStatisticField))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid outStatistics",
                    [$"Unknown field '{statistic.OnStatisticField}' in outStatistics."]);
            }
        }

        return null;
    }
}
