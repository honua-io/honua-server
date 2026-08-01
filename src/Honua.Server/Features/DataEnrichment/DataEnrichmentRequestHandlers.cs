// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Infrastructure.Analytics;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.Server.Features.DataEnrichment.Models;
using Honua.Server.Features.Protocols.SpatialAnalytics.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.DataEnrichment;

/// <summary>
/// Request handlers for the data-enrichment API (#374; #2280/#2282/#2284). Discovery
/// (<c>GET /api/enrich/datasets</c>) reads the managed catalog filtered by the
/// caller's edition; the synchronous compute endpoint (<c>POST /api/enrich</c>) is a
/// thin adapter over the shared <see cref="ISpatialAnalyticsReader"/> spatial-join
/// pipeline. Compute is Pro-tier gated (reuses the <c>analytics.spatial-join</c>
/// entitlement).
/// </summary>
internal static class DataEnrichmentRequestHandlers
{
    private const string LoggerCategory = "Honua.Server.Features.DataEnrichment";
    private const string ProtocolName = "DataEnrichment";

    // Enrichment compute is a curated facade over spatial join, so it shares the
    // spatial-join entitlement rather than introducing a separate SKU line.
    private const string EnrichmentEntitlementKey = "analytics.spatial-join";

    /// <summary>
    /// Lists the configuration-driven enrichment catalog (back-compat
    /// <c>GET /api/enrich/catalog</c>). Pro-tier gated.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The enrichment catalog response, or a 402 when not entitled.</returns>
    public static IResult HandleCatalogGet(HttpContext context)
    {
        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);

        var editionError = LicenseGate.RequireEntitlement(
            context, EnrichmentEntitlementKey, "Data enrichment", logger);
        if (editionError != null)
        {
            return editionError;
        }

        var catalog = context.RequestServices.GetRequiredService<EnrichmentCatalog>();

        var descriptors = catalog.Datasets
            .Select(d => new EnrichmentDatasetDescriptor
            {
                Key = d.Key,
                DisplayName = d.DisplayName,
                Category = d.Category,
                DefaultPredicate = string.IsNullOrWhiteSpace(d.Predicate) ? "intersects" : d.Predicate,
                Attributes = d.Attributes.ToArray()
            })
            .ToArray();

        var response = new EnrichmentCatalogResponse { Datasets = descriptors };
        return Results.Json(response, EnrichmentJsonContext.Default.EnrichmentCatalogResponse);
    }

    /// <summary>
    /// Discovers the managed enrichment-dataset catalog filtered by the caller's
    /// edition (<c>GET /api/enrich/datasets</c>, #2280). Carries
    /// provenance/attribution/license so downstream consumers can comply with the
    /// data provider's terms.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The edition-filtered enrichment datasets.</returns>
    public static async Task<IResult> HandleDatasetsGet(HttpContext context)
    {
        var catalog = context.RequestServices.GetRequiredService<EnrichmentDatasetCatalogService>();
        var edition = ResolveEdition(context);

        var datasets = await catalog.ListMetadataAsync(edition, context.RequestAborted).ConfigureAwait(false);
        var response = new EnrichmentDatasetsResponse { Datasets = datasets.ToArray() };
        return Results.Json(response, EnrichmentJsonContext.Default.EnrichmentDatasetsResponse);
    }

    /// <summary>
    /// Discovers a single managed enrichment dataset by id, edition-filtered
    /// (<c>GET /api/enrich/datasets/{id}</c>, #2280).
    /// </summary>
    /// <param name="id">Dataset id (slug).</param>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The dataset metadata, or 404 when missing or hidden by edition.</returns>
    public static async Task<IResult> HandleDatasetGet(string id, HttpContext context)
    {
        var catalog = context.RequestServices.GetRequiredService<EnrichmentDatasetCatalogService>();
        var edition = ResolveEdition(context);

        var dataset = await catalog.GetMetadataAsync(id, edition, context.RequestAborted).ConfigureAwait(false);
        if (dataset is null)
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                "Unknown enrichment dataset",
                [$"No enrichment dataset is registered with id '{id}' for your edition."]);
        }

        return Results.Json(dataset, EnrichmentJsonContext.Default.EnrichmentDatasetMetadata);
    }

    /// <summary>
    /// Enriches a registered source layer with attributes drawn from an enrichment
    /// dataset (<c>POST /api/enrich</c>, #2282). Thin adapter over the shared
    /// spatial-join pipeline; resolves the dataset through the managed catalog and
    /// maps the enrichment <c>method</c> vocabulary to canonical spatial predicates.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The enriched GeoJSON FeatureCollection, or an error result.</returns>
    public static async Task<IResult> HandleEnrichPost(HttpContext context)
    {
        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);

        var editionError = LicenseGate.RequireEntitlement(
            context, EnrichmentEntitlementKey, "Data enrichment", logger);
        if (editionError != null)
        {
            return editionError;
        }

        var cancellationToken = context.RequestAborted;

        EnrichmentRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                EnrichmentJsonContext.Default.EnrichmentRequest, cancellationToken);
        }
        catch (JsonException)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Invalid request body", ["The request body must be a JSON object."]);
        }

        if (request is null)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Invalid request body", ["A JSON request body is required."]);
        }

        // Inline GeoJSON source is deferred to the async/managed batch path: the only
        // inline spatial-join engine (analytics.spatial-join-managed) is job-dispatched.
        if (request.Features is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "Inline GeoJSON source not supported",
                ["Inline GeoJSON enrichment is not available on the synchronous endpoint. "
                    + "Use a registered sourceLayerId, or submit an async batch enrichment job via OGC API Processes."]);
        }

        // ----- Resolve the enrichment dataset from the managed/config catalog -----
        var catalog = context.RequestServices.GetRequiredService<EnrichmentDatasetCatalogService>();
        var idOrKey = !string.IsNullOrWhiteSpace(request.DatasetId) ? request.DatasetId : request.DatasetKey;
        var dataset = await catalog.ResolveAsync(idOrKey, cancellationToken).ConfigureAwait(false);
        if (dataset is null)
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                "Unknown enrichment dataset",
                [string.IsNullOrWhiteSpace(idOrKey)
                    ? "datasetId is required."
                    : $"No enrichment dataset is registered with id '{idOrKey}'."]);
        }

        // ----- Enforce the dataset's minimum edition tier -----
        var callerEdition = ResolveEdition(context);
        if (callerEdition < dataset.MinimumEdition)
        {
            return StandardErrorHelpers.CreatePaymentRequired(
                context,
                $"Enrichment dataset '{dataset.Id}' requires the {dataset.MinimumEdition} edition.",
                [$"Current edition is {callerEdition}."]);
        }

        if (request.SourceLayerId is not { } sourceLayerId || sourceLayerId < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Invalid sourceLayerId", ["sourceLayerId must be a non-negative layer identifier."]);
        }

        if (sourceLayerId == dataset.LayerId)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid sourceLayerId",
                ["sourceLayerId must differ from the enrichment dataset's layer."]);
        }

        // ----- Resolve the effective predicate (method → predicate → default) -----
        if (!TryResolvePredicate(request, dataset, out var predicate, out var predicateError))
        {
            return predicateError!.Status == StatusCodes.Status501NotImplemented
                ? StandardErrorHelpers.CreateNotImplemented(context, predicateError.Title, predicateError.Details)
                : StandardErrorHelpers.CreateBadRequest(context, predicateError.Title, predicateError.Details);
        }

        var analyticsLimits = context.RequestServices
            .GetRequiredService<IOptions<LimitsOptions>>().Value.Analytics;

        // ----- Resolve the dwithin distance (caller override → dataset default) -----
        double? distanceMeters = request.DistanceMeters ?? dataset.DistanceMeters;
        if (predicate == SpatialJoinPredicate.DWithin)
        {
            if (distanceMeters is not { } distance || double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0d)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    "distance is required",
                    ["distanceMeters (a positive number) is required for the within-distance method."]);
            }

            if (distance > analyticsLimits.MaxDWithinDistanceMeters)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid distance",
                    [$"distanceMeters must not exceed {analyticsLimits.MaxDWithinDistanceMeters} meters."]);
            }
        }
        else
        {
            distanceMeters = null;
        }

        // ----- Resolve carried output fields (outputFields → attributes → dataset) -----
        var outputFields = request.OutputFields is { Length: > 0 }
            ? request.OutputFields
            : request.Attributes is { Length: > 0 }
                ? request.Attributes
                : dataset.Attributes.ToArray();
        ImmutableArray<string>? carryFields = outputFields.Length > 0 ? [.. outputFields] : null;

        // ----- Resolve aggregates (count/sum/avg/min/max) -----
        if (!TryBuildStatistics(request.Aggregates, out var outStatistics, out var statsError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid aggregates", [statsError!]);
        }

        // ----- Resolve and authorize the source layer (read access) -----
        var sourceValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context, sourceLayerId, scope: AccessScope.Read, requiredProtocol: null, cancellationToken: cancellationToken);
        if (!sourceValidation.IsValid)
        {
            return sourceValidation.ErrorResult!;
        }

        // ----- Resolve and authorize the enrichment (join) layer (read access) -----
        var datasetValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context, dataset.LayerId, scope: AccessScope.Read, requiredProtocol: null, cancellationToken: cancellationToken);
        if (!datasetValidation.IsValid)
        {
            return datasetValidation.ErrorResult!;
        }

        var joinLayerSrid = datasetValidation.Resource!.ReadSrid() ?? SpatialReference.WGS84.ToSrid();

        // ----- Build the source-layer FeatureQuery (where filter only) -----
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.Where))
        {
            values[SpatialAnalyticsParameters.Where] = request.Where;
        }

        var (featureQuery, filterError) = await AnalyticsFeatureQueryFactory.TryBuildAsync(
            context, values, sourceValidation.Resource!, cancellationToken);
        if (featureQuery is null)
        {
            return filterError!;
        }

        var joinQuery = new SpatialJoinQuery
        {
            JoinLayerId = dataset.LayerId,
            JoinLayerSrid = joinLayerSrid,
            Predicate = predicate,
            DistanceMeters = distanceMeters,
            CarryFields = carryFields,
            OutStatistics = outStatistics,
            MaxInputFeatures = analyticsLimits.MaxInputFeatures
        };

        using var activity = StartEnrichActivity(dataset.Id, predicate, sourceLayerId);

        var reader = context.RequestServices.GetService<ISpatialAnalyticsReader>();
        if (reader is null)
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "Spatial analytics backend not available",
                ["Data enrichment is not supported by the active feature-store provider."]);
        }

        ImmutableArray<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = await reader.QuerySpatialJoinAsync(sourceLayerId, featureQuery.Value, joinQuery, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: this is the enrichment
            // reader call boundary; the failure is logged and mapped to a generic error
            // response below.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (logger is not null)
            {
                LogEnrichFailed(logger, dataset.Id, sourceLayerId, ex);
            }

            var message = ex is ArgumentException ? ex.Message : "Data enrichment request failed.";
            return StandardErrorHelpers.CreateBadRequest(context, "Data enrichment failed", [message]);
        }

        // ----- Over-limit guard: point large inputs at the async batch path (#2282) -----
        if (rows.Length > analyticsLimits.MaxInputFeatures)
        {
            return StandardErrorHelpers.CreatePayloadTooLarge(
                context,
                "Enrichment input too large for the synchronous endpoint",
                [$"The source selection exceeds the synchronous enrichment limit of {analyticsLimits.MaxInputFeatures} features. "
                    + "Submit an async batch enrichment job via OGC API Processes for larger inputs."]);
        }

        var features = new SpatialAnalyticsFeature[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            features[i] = MapRowToFeature(rows[i]);
        }

        activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, features.Length);

        var response = new SpatialAnalyticsFeatureCollection
        {
            Features = features,
            NumberReturned = features.Length,
            Metadata = new SpatialAnalyticsMetadata
            {
                Operation = "enrich",
                InputTruncated = false,
                ResultTruncated = false,
                MaxInputFeatures = analyticsLimits.MaxInputFeatures,
                MaxOutputRows = null
            }
        };

        // Echo the dataset attribution so downstream consumers can comply with the
        // provider's terms (#2282 / #2280).
        if (!string.IsNullOrWhiteSpace(dataset.Attribution))
        {
            context.Response.Headers["X-Honua-Data-Attribution"] = dataset.Attribution;
        }

        return Results.Json(
            response,
            SpatialAnalyticsJsonContext.Default.SpatialAnalyticsFeatureCollection,
            contentType: "application/geo+json");
    }

    private static HonuaEdition ResolveEdition(HttpContext context)
    {
        var status = context.RequestServices.GetService<ILicenseStatusProvider>()?.GetCurrentStatus();
        return status?.Edition ?? HonuaEdition.Community;
    }

    private static Activity? StartEnrichActivity(string datasetId, SpatialJoinPredicate predicate, int sourceLayerId)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.enrich.enrich");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, ProtocolName);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "enrich");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, sourceLayerId);
        activity?.SetTag("honua.enrich.dataset", datasetId);
        activity?.SetTag("honua.enrich.method", predicate.ToString());
        return activity;
    }

    // Resolves the effective spatial predicate from the enrichment method vocabulary,
    // a direct predicate override, or the dataset default.
    //
    // The enrichment vocabulary is DATASET-SUBJECT (honua-server#3069): the caller
    // asks what the enrichment dataset does to the source features being enriched,
    // so `point-in-polygon`/`contains` means "the dataset polygon contains my source
    // feature" (canonical JoinContainsTarget — the dataset layer is the join side)
    // and `within` means "the dataset feature sits inside my source feature"
    // (canonical TargetContainsJoin). Mapping `contains` onto the target-subject
    // direction is what made synchronous point-in-polygon evaluate
    // ST_Contains(point, polygon) and always return zero matches.
    private static bool TryResolvePredicate(
        EnrichmentRequest request,
        ResolvedEnrichmentDataset dataset,
        out SpatialJoinPredicate predicate,
        out EnrichmentError? error)
    {
        predicate = SpatialJoinPredicate.Intersects;
        error = null;

        if (!string.IsNullOrWhiteSpace(request.Method))
        {
            switch (request.Method.Trim().ToLowerInvariant())
            {
                case "intersects":
                    predicate = SpatialJoinPredicate.Intersects;
                    return true;
                case "point-in-polygon" or "point_in_polygon" or "pip" or "contains":
                    predicate = SpatialJoinPredicate.JoinContainsTarget;
                    return true;
                case "within":
                    predicate = SpatialJoinPredicate.TargetContainsJoin;
                    return true;
                case "within-distance" or "within_distance" or "dwithin":
                    predicate = SpatialJoinPredicate.DWithin;
                    return true;
                case "nearest-neighbor" or "nearest_neighbor" or "nearest":
                    error = new EnrichmentError(
                        StatusCodes.Status501NotImplemented,
                        "Method not supported",
                        ["The nearest-neighbor method is not available on the synchronous layer-backed endpoint. "
                            + "Submit an async batch enrichment job via OGC API Processes."]);
                    return false;
                default:
                    error = new EnrichmentError(
                        StatusCodes.Status400BadRequest,
                        "Invalid method",
                        ["method must be one of: intersects, point-in-polygon, within, within-distance, nearest-neighbor."]);
                    return false;
            }
        }

        var predicateStr = string.IsNullOrWhiteSpace(request.Predicate) ? dataset.DefaultPredicate : request.Predicate;
        switch (predicateStr?.Trim().ToLowerInvariant())
        {
            case null or "" or "intersects":
                predicate = SpatialJoinPredicate.Intersects;
                return true;
            case "contains":
                predicate = SpatialJoinPredicate.JoinContainsTarget;
                return true;
            case "within":
                predicate = SpatialJoinPredicate.TargetContainsJoin;
                return true;
            case "dwithin":
                predicate = SpatialJoinPredicate.DWithin;
                return true;
            default:
                error = new EnrichmentError(
                    StatusCodes.Status400BadRequest,
                    "Invalid predicate",
                    ["predicate must be one of: intersects, contains, within, dwithin."]);
                return false;
        }
    }

    // Maps the enrichment aggregate vocabulary (count/sum/avg/min/max) onto the
    // canonical statistic definitions consumed by the spatial-join pipeline.
    private static bool TryBuildStatistics(
        EnrichmentAggregate[]? aggregates,
        out ImmutableArray<StatisticDefinition>? outStatistics,
        out string? error)
    {
        outStatistics = null;
        error = null;

        if (aggregates is not { Length: > 0 })
        {
            return true;
        }

        var builder = ImmutableArray.CreateBuilder<StatisticDefinition>(aggregates.Length);
        foreach (var aggregate in aggregates)
        {
            if (!TryParseStatisticType(aggregate.StatisticType, out var statisticType))
            {
                error = "statisticType must be one of: count, sum, avg, min, max.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(aggregate.OnField))
            {
                error = "onField is required for each aggregate.";
                return false;
            }

            var onField = aggregate.OnField.Trim();
            var outName = string.IsNullOrWhiteSpace(aggregate.OutName)
                ? $"{statisticType.ToString().ToLowerInvariant()}_{onField}"
                : aggregate.OutName.Trim();

            builder.Add(new StatisticDefinition
            {
                StatisticType = statisticType,
                OnStatisticField = onField,
                OutStatisticFieldName = outName
            });
        }

        outStatistics = builder.ToImmutable();
        return true;
    }

    private static bool TryParseStatisticType(string? value, out StatisticType statisticType)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "count":
                statisticType = StatisticType.Count;
                return true;
            case "sum":
                statisticType = StatisticType.Sum;
                return true;
            case "avg" or "mean" or "average":
                statisticType = StatisticType.Avg;
                return true;
            case "min":
                statisticType = StatisticType.Min;
                return true;
            case "max":
                statisticType = StatisticType.Max;
                return true;
            default:
                statisticType = StatisticType.Count;
                return false;
        }
    }

    // Shapes an enrichment row into a GeoJSON feature: extracts the source geometry
    // column and leaves the remaining columns (matchCount, carried attributes,
    // aggregates) as feature properties.
    private static SpatialAnalyticsFeature MapRowToFeature(IReadOnlyDictionary<string, object?> row)
    {
        var properties = new Dictionary<string, object?>(row);
        SpatialAnalyticsGeometry? geometry = null;

        if (TryExtractGeometry(properties, "geometry", out var extracted))
        {
            geometry = extracted;
        }

        return new SpatialAnalyticsFeature
        {
            Geometry = geometry,
            Properties = properties
        };
    }

    private static bool TryExtractGeometry(
        Dictionary<string, object?> properties, string key, out SpatialAnalyticsGeometry? geometry)
    {
        geometry = null;
        if (!properties.TryGetValue(key, out var raw) || raw is not string geoJsonStr)
        {
            return false;
        }

        properties.Remove(key);

        try
        {
            using var doc = JsonDocument.Parse(geoJsonStr);
            var root = doc.RootElement;
            var geoType = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString() ?? "Point"
                : "Point";

            var coordsJson = root.TryGetProperty("coordinates", out var coords) ? coords.GetRawText() : null;

            geometry = new SpatialAnalyticsGeometry
            {
                Type = geoType,
                CoordinatesJson = coordsJson
            };
            return true;
        }
        catch (JsonException)
        {
            properties[key] = geoJsonStr;
            geometry = null;
            return false;
        }
    }

    private static readonly Action<ILogger, string, int, Exception?> _enrichFailed =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(1, "EnrichFailed"),
            "Data enrichment failed for dataset {DatasetId} on source layer {SourceLayerId}");

    private static void LogEnrichFailed(ILogger logger, string datasetId, int sourceLayerId, Exception ex)
        => _enrichFailed(logger, datasetId, sourceLayerId, ex);

    /// <summary>
    /// Lightweight internal carrier for a deferred enrichment validation error so
    /// the predicate resolver can signal both 400 and 501 outcomes.
    /// </summary>
    private sealed record EnrichmentError(int Status, string Title, IReadOnlyList<string> Details);
}
