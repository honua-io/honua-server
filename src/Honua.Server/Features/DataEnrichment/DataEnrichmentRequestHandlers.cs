// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.DataEnrichment.Domain;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.DataEnrichment;

/// <summary>
/// Request handlers for the data-enrichment API (#374). Pro-tier gated (reuses the
/// <c>analytics.spatial-join</c> entitlement, since enrichment is a curated facade
/// over the same join primitive). The enrichment join itself runs through the
/// shared <see cref="ISpatialAnalyticsReader"/>.
/// </summary>
internal static class DataEnrichmentRequestHandlers
{
    private const string LoggerCategory = "Honua.Server.Features.DataEnrichment";

    // Enrichment is a curated facade over spatial join, so it shares the
    // spatial-join entitlement rather than introducing a separate SKU line.
    private const string EnrichmentEntitlementKey = "analytics.spatial-join";

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

        // ----- Resolve the enrichment dataset from the catalog -----
        var catalog = context.RequestServices.GetRequiredService<EnrichmentCatalog>();
        var dataset = catalog.Resolve(request.DatasetKey);
        if (dataset is null)
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                "Unknown enrichment dataset",
                [string.IsNullOrWhiteSpace(request.DatasetKey)
                    ? "datasetKey is required."
                    : $"No enrichment dataset is registered with key '{request.DatasetKey}'."]);
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

        // ----- Resolve the effective predicate (caller override → dataset default) -----
        var predicateStr = string.IsNullOrWhiteSpace(request.Predicate) ? dataset.Predicate : request.Predicate;
        if (!TryParsePredicate(predicateStr, out var predicate))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid predicate",
                ["predicate must be one of: intersects, contains, within, dwithin."]);
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
                    ["distanceMeters (a positive number) is required when predicate is 'dwithin'."]);
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

        // ----- Resolve carried attributes (caller override → dataset default) -----
        var attributes = request.Attributes is { Length: > 0 }
            ? request.Attributes
            : dataset.Attributes.ToArray();
        ImmutableArray<string>? carryFields = attributes.Length > 0
            ? [.. attributes]
            : null;

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
            OutStatistics = null,
            MaxInputFeatures = analyticsLimits.MaxInputFeatures
        };

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
        catch (Exception ex)
        {
            if (logger is not null)
            {
                LogEnrichFailed(logger, dataset.Key, sourceLayerId, ex);
            }

            var message = ex is ArgumentException ? ex.Message : "Data enrichment request failed.";
            return StandardErrorHelpers.CreateBadRequest(context, "Data enrichment failed", [message]);
        }

        var inputTruncated = rows.Length > analyticsLimits.MaxInputFeatures;
        if (inputTruncated)
        {
            rows = [.. rows.Take(analyticsLimits.MaxInputFeatures)];
        }

        var features = new SpatialAnalyticsFeature[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            features[i] = MapRowToFeature(rows[i]);
        }

        var response = new SpatialAnalyticsFeatureCollection
        {
            Features = features,
            NumberReturned = features.Length,
            Metadata = new SpatialAnalyticsMetadata
            {
                Operation = "enrich",
                InputTruncated = inputTruncated,
                ResultTruncated = false,
                MaxInputFeatures = analyticsLimits.MaxInputFeatures,
                MaxOutputRows = null
            }
        };

        return Results.Json(
            response,
            SpatialAnalyticsJsonContext.Default.SpatialAnalyticsFeatureCollection,
            contentType: "application/geo+json");
    }

    private static bool TryParsePredicate(string? value, out SpatialJoinPredicate predicate)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "intersects":
                predicate = SpatialJoinPredicate.Intersects;
                return true;
            case "contains":
                predicate = SpatialJoinPredicate.Contains;
                return true;
            case "within":
                predicate = SpatialJoinPredicate.Within;
                return true;
            case "dwithin":
                predicate = SpatialJoinPredicate.DWithin;
                return true;
            default:
                predicate = SpatialJoinPredicate.Intersects;
                return false;
        }
    }

    // Shapes an enrichment row into a GeoJSON feature: extracts the source geometry
    // column and leaves the remaining columns (matchCount, carried attributes) as
    // feature properties. Mirrors the spatial-join handler's mapping but is kept
    // local so the slice does not depend on a private SpatialAnalytics helper.
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
            "Data enrichment failed for dataset {DatasetKey} on source layer {SourceLayerId}");

    private static void LogEnrichFailed(ILogger logger, string datasetKey, int sourceLayerId, Exception ex)
        => _enrichFailed(logger, datasetKey, sourceLayerId, ex);
}
