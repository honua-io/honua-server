// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Protocols.GeoServices;
using Honua.Infrastructure.Analytics;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Protocols.SpatialAnalytics.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.SpatialAnalytics;

/// <summary>
/// Clustering handler (DBSCAN / K-Means). REST entry:
/// <c>POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters</c>.
/// OGC mirror: <c>POST /ogc/features/collections/{collectionId}/clusters</c>.
/// Both entries delegate to <see cref="HandleClustersCoreAsync"/> so REST and
/// OGC callers receive identical payloads.
/// </summary>
internal static partial class SpatialAnalyticsRequestHandlers
{
    private const string ClusterOperation = "cluster";

    public static async Task<IResult> HandleQueryClustersPost(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var logger = context.RequestServices
            .GetService<ILoggerFactory>()?.CreateLogger("Honua.Server.Features.Protocols.SpatialAnalytics.Clusters");
        var editionError = RequireProEdition(context, ClusterOperation, logger);
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

        var (validatedLayerId, resource, _, resourceError) = await ValidateRestResourceAsync(
            context, serviceId, layerId, cancellationToken);
        if (resourceError != null)
        {
            return resourceError;
        }

        return await HandleClustersCoreAsync(
            context,
            values,
            validatedLayerId,
            resource!,
            serviceId,
            HonuaTelemetry.Protocols.FeatureServer,
            logger,
            cancellationToken);
    }

    public static async Task<IResult> HandleOgcClustersPost(
        string collectionId,
        HttpContext context)
    {
        var logger = context.RequestServices
            .GetService<ILoggerFactory>()?.CreateLogger("Honua.Server.Features.Protocols.SpatialAnalytics.Clusters");
        var editionError = RequireProEdition(context, ClusterOperation, logger);
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

        var (layerId, resource, resourceError) = await ValidateOgcResourceAsync(context, collectionId, cancellationToken);
        if (resourceError != null)
        {
            return resourceError;
        }

        return await HandleClustersCoreAsync(
            context,
            values,
            layerId,
            resource!,
            serviceId: null,
            HonuaTelemetry.Protocols.OgcFeatures,
            logger,
            cancellationToken);
    }

    private static async Task<IResult> HandleClustersCoreAsync(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        int layerId,
        MetadataV2Resource resource,
        string? serviceId,
        string protocol,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        using var activity = StartAnalyticsActivity(ClusterOperation, protocol, serviceId, layerId);

        if (logger != null)
        {
            SpatialAnalyticsLog.RequestReceived(logger, ClusterOperation, layerId);
        }

        var startTimestamp = TimeProvider.System.GetTimestamp();
        var analyticsLimits = context.RequestServices
            .GetRequiredService<IOptions<LimitsOptions>>().Value.Analytics;

        // Algorithm (default DBSCAN).
        var algorithmStr = GetValueString(values, SpatialAnalyticsParameters.Algorithm);
        ClusterAlgorithm algorithm;
        if (string.IsNullOrWhiteSpace(algorithmStr))
        {
            algorithm = ClusterAlgorithm.DbScan;
        }
        else
        {
            switch (algorithmStr.ToLowerInvariant())
            {
                case "dbscan":
                    algorithm = ClusterAlgorithm.DbScan;
                    break;
                case "kmeans":
                case "k-means":
                    algorithm = ClusterAlgorithm.KMeans;
                    break;
                default:
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid algorithm",
                        ["algorithm must be either 'dbscan' or 'kmeans'."]);
            }
        }

        activity?.SetTag("cluster.algorithm", algorithm.ToString());

        // Eps (DBSCAN distance, in meters by default).
        double? eps = null;
        var epsStr = GetValueString(values, SpatialAnalyticsParameters.Eps);
        if (!string.IsNullOrWhiteSpace(epsStr))
        {
            if (!double.TryParse(epsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var epsValue) ||
                double.IsNaN(epsValue) || double.IsInfinity(epsValue) ||
                epsValue <= ClusterQuery.MinEps)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid eps",
                    ["eps must be a positive number."]);
            }

            if (epsValue > analyticsLimits.MaxDbscanEpsMeters)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid eps",
                    [$"eps must not exceed {analyticsLimits.MaxDbscanEpsMeters} meters."]);
            }

            eps = epsValue;
        }

        // MinPoints (DBSCAN cluster floor).
        int? minPoints = null;
        var minPointsStr = GetValueString(values, SpatialAnalyticsParameters.MinPoints);
        if (!string.IsNullOrWhiteSpace(minPointsStr))
        {
            if (!int.TryParse(minPointsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minPts) ||
                minPts < ClusterQuery.MinMinPoints)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid minPoints",
                    [$"minPoints must be an integer ≥ {ClusterQuery.MinMinPoints}."]);
            }

            minPoints = minPts;
        }

        // K (K-Means partition count).
        int? k = null;
        var kStr = GetValueString(values, SpatialAnalyticsParameters.K);
        if (!string.IsNullOrWhiteSpace(kStr))
        {
            if (!int.TryParse(kStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kValue) ||
                kValue < ClusterQuery.MinK)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid k",
                    [$"k must be an integer ≥ {ClusterQuery.MinK}."]);
            }

            if (kValue > analyticsLimits.MaxKMeansK)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid k",
                    [$"k must not exceed {analyticsLimits.MaxKMeansK}."]);
            }

            k = kValue;
        }

        // Algorithm-specific required parameter validation.
        if (algorithm == ClusterAlgorithm.DbScan)
        {
            if (!eps.HasValue)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "eps parameter is required",
                    ["eps is required for DBSCAN clustering."]);
            }

            if (!minPoints.HasValue)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "minPoints parameter is required",
                    ["minPoints is required for DBSCAN clustering."]);
            }
        }
        else if (algorithm == ClusterAlgorithm.KMeans && !k.HasValue)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "k parameter is required",
                ["k is required for K-Means clustering."]);
        }

        // returnHullPerCluster (default: one row per feature).
        var returnHull = false;
        var returnHullStr = GetValueString(values, SpatialAnalyticsParameters.ReturnHullPerCluster);
        if (!string.IsNullOrWhiteSpace(returnHullStr) &&
            !bool.TryParse(returnHullStr, out returnHull))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid returnHullPerCluster",
                ["returnHullPerCluster must be 'true' or 'false'."]);
        }

        // outStatistics (optional; only honored when returnHullPerCluster=true).
        if (!TryParseOutStatistics(context, values, out var outStatistics, out var outStatsError))
        {
            return outStatsError!;
        }

        // Mirror the buffer-aggregate guard: outStatistics only make sense in the
        // hull-per-cluster path, which is the branch that emits GROUP BY + aggregate
        // columns in BuildClusterQuery. The per-feature path writes one row per
        // source feature with no aggregation, so silently discarding outStatistics
        // there would hide a contract mismatch from callers. Reject it at the
        // handler so the behavior stays consistent with queryBufferAggregate.
        if (!returnHull && outStatistics.HasValue && !outStatistics.Value.IsDefaultOrEmpty)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid outStatistics",
                ["outStatistics requires returnHullPerCluster=true; per-feature cluster assignments cannot carry aggregate statistics."]);
        }

        outStatistics = AnalyticsFeatureQueryFactory.ApplyStatisticFieldTypes(outStatistics, resource);

        // Feature query (where + SQL filter + objectIds + spatial + temporal).
        var (featureQuery, filterError) = await AnalyticsFeatureQueryFactory.TryBuildAsync(
            context, values, resource, cancellationToken);
        if (featureQuery == null)
        {
            return filterError!;
        }

        var clusterQuery = new ClusterQuery
        {
            Algorithm = algorithm,
            Eps = eps,
            MinPoints = minPoints,
            K = k,
            DistanceUnit = DistanceUnit.Meters,
            ReturnHullPerCluster = returnHull,
            OutStatistics = outStatistics,
            MaxInputFeatures = analyticsLimits.MaxInputFeatures,
            MaxClusters = analyticsLimits.MaxClusters
        };

        if (!TryGetAnalyticsReader(context, ClusterOperation, logger, out var reader, out var readerError))
        {
            return readerError!;
        }

        ImmutableArray<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = await reader.QueryClustersAsync(layerId, featureQuery.Value, clusterQuery, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (logger != null)
            {
                SpatialAnalyticsLog.RequestFailed(logger, ClusterOperation, layerId, ex.Message, ex);
            }
            return CreateReaderFailureResult(context, ClusterOperation, ex);
        }

        // Overflow detection: per-feature mode uses maxInputFeatures, hull-per-cluster
        // mode uses maxClusters as the per-result cap.
        // - Per-feature: every row is one input feature, so inputCount == outputCount.
        // - Hull mode: aggregated, so the SQL builder emits "_inputCount" on every
        //   row (capped at MaxInputFeatures+1 by the src CTE LIMIT). Strip it from
        //   the rows so MapRowToFeature does not leak it back to the caller.
        int? maxOutputRows = returnHull ? analyticsLimits.MaxClusters : null;
        var inputCount = returnHull ? ExtractInputCount(ref rows) : rows.Length;
        var (inputTruncated, resultTruncated) = DetectOverflow(
            inputCount, rows.Length, analyticsLimits.MaxInputFeatures, maxOutputRows);
        if (inputTruncated && logger != null)
        {
            SpatialAnalyticsLog.InputTruncated(logger, ClusterOperation, layerId, analyticsLimits.MaxInputFeatures);
        }
        if (resultTruncated && logger != null)
        {
            SpatialAnalyticsLog.ResultTruncated(logger, ClusterOperation, layerId, maxOutputRows!.Value);
        }

        rows = TrimOverflowRows(rows, analyticsLimits.MaxInputFeatures, maxOutputRows);

        var features = new SpatialAnalyticsFeature[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            features[i] = MapRowToFeature(rows[i]);
        }

        var elapsed = TimeProvider.System.GetElapsedTime(startTimestamp);
        if (logger != null)
        {
            SpatialAnalyticsLog.RequestCompleted(
                logger, ClusterOperation, layerId, features.Length, elapsed.TotalMilliseconds);
        }

        var response = new SpatialAnalyticsFeatureCollection
        {
            Features = features,
            NumberReturned = features.Length,
            Metadata = new SpatialAnalyticsMetadata
            {
                Operation = ClusterOperation,
                InputTruncated = inputTruncated,
                ResultTruncated = resultTruncated,
                MaxInputFeatures = analyticsLimits.MaxInputFeatures,
                MaxOutputRows = maxOutputRows
            }
        };

        return WriteAnalyticsResponse(response);
    }
}
