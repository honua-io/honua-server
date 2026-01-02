// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Server.Features.OgcFeatures.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Features/Items CRUD endpoints for OGC API Features
/// </summary>
internal static partial class FeaturesEndpoints
{
    private const string InvalidCqlFilterPrefix = "Invalid CQL filter";
    private const string FilterLangCql2Text = "cql2-text";
    private const string FilterLangCql2Json = "cql2-json";

    private static readonly Dictionary<string, OgcFeaturesUtilities.CrsDefinition> _supportedCrs
        = new Dictionary<string, OgcFeaturesUtilities.CrsDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [OgcFeaturesUtilities.Crs84Uri] = new OgcFeaturesUtilities.CrsDefinition(
                OgcFeaturesUtilities.Crs84Uri,
                4326,
                OgcFeaturesUtilities.AxisOrder.EastNorth),
            [OgcFeaturesUtilities.Epsg4326Uri] = new OgcFeaturesUtilities.CrsDefinition(
                OgcFeaturesUtilities.Epsg4326Uri,
                4326,
                OgcFeaturesUtilities.AxisOrder.NorthEast)
        };

    /// <summary>
    /// Maps features/items CRUD endpoints
    /// </summary>
    public static IEndpointRouteBuilder MapFeaturesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/features/collections/{collectionId}/items", HandleGetItems)
            .WithDisplayName("OGC API Features Items")
            .WithName("ItemsList")
            .WithSummary("Get features from a collection")
            .WithDescription("Get features from a collection with optional filtering using CQL2-Text")
            .WithTags("OGC API Features")
            .Produces<FeatureCollection>(200, MediaTypes.GeoJson)
            .Produces<FeatureCollection>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Gml)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}/items/{featureId}", HandleGetItem)
            .WithDisplayName("OGC API Features Item")
            .WithName("ItemById")
            .WithSummary("Get a feature by ID")
            .WithDescription("Get a specific feature by its ID from a collection")
            .WithTags("OGC API Features")
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Gml)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404)
            .Produces(400);

        endpoints.MapPost("/ogc/features/collections/{collectionId}/items", HandleCreateFeature)
            .WithDisplayName("OGC API Features Create Item")
            .WithName("CreateItem")
            .WithSummary("Create a new feature")
            .WithDescription("Add a new feature to the specified collection")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(201, MediaTypes.GeoJson)
            .Produces(400)
            .Produces(404)
            .Produces(409);

        endpoints.MapPut("/ogc/features/collections/{collectionId}/items/{featureId}", HandleUpdateFeature)
            .WithDisplayName("OGC API Features Update Item")
            .WithName("UpdateItem")
            .WithSummary("Update a feature")
            .WithDescription("Replace an existing feature with new data")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces(201) // If feature didn't exist (upsert behavior)
            .Produces(400)
            .Produces(404);

        endpoints.MapDelete("/ogc/features/collections/{collectionId}/items/{featureId}", HandleDeleteFeature)
            .WithDisplayName("OGC API Features Delete Item")
            .WithName("DeleteItem")
            .WithSummary("Delete a feature")
            .WithDescription("Remove a feature from the collection")
            .WithTags("OGC API Features", "Transactions")
            .Produces(204)
            .Produces(404);

        return endpoints;
    }

    private static async Task<IResult> HandleGetItems(
        string collectionId,
        HttpContext context,
        string? f,
        int? limit,
        int? offset,
        string? bbox,
        string? datetime,
        string? filter,
        string? crs,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        IOptions<LimitsOptions> limitsOptions,
        IResourceValidator resourceValidator,
        ICommonQueryValidator queryValidator,
        ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        var request = context.Request;

        try
        {
            var cancellationToken = GetTimeoutAwareCancellationToken(context);

            // Use centralized resource validation
            var collectionResult = await resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);
            if (!collectionResult.IsValid)
            {
                return OgcErrorHelpers.CreateNotFound(context, collectionResult.ErrorMessage ?? $"Collection '{collectionId}' not found.");
            }
            var layer = collectionResult.Resource!;
            var layerId = layer.Id;

            var validationError = OgcFeaturesUtilities.ValidateItemsQueryParameters(request, layer);
            if (validationError is not null)
            {
                return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return CreateFormatError(context, formatError);
            }

            var filterLang = GetQueryValue(request, "filter-lang");
            var filterCrs = GetQueryValue(request, "filter-crs");
            var bboxCrs = GetQueryValue(request, "bbox-crs");

            if (!TryResolveFilterLanguage(filterLang, out var resolvedFilterLang, out var filterLangError))
            {
                return OgcErrorHelpers.CreateBadRequest(context, filterLangError ?? "Invalid filter language.");
            }

            if (!TryResolveCrs(crs, out var crsDefinition, out var crsError))
            {
                return OgcErrorHelpers.CreateBadRequest(context, crsError ?? "Invalid CRS.");
            }

            if (!string.IsNullOrWhiteSpace(filterCrs) && string.IsNullOrWhiteSpace(filter))
            {
                return OgcErrorHelpers.CreateBadRequest(context, "filter-crs requires a filter parameter.");
            }

            if (!TryResolveCrs(filterCrs, out var filterCrsDefinition, out var filterCrsError))
            {
                return OgcErrorHelpers.CreateBadRequest(context, filterCrsError ?? "Invalid filter CRS.");
            }

            if (!TryResolveCrs(bboxCrs, out var bboxCrsDefinition, out var bboxCrsError))
            {
                return OgcErrorHelpers.CreateBadRequest(context, bboxCrsError ?? "Invalid bbox CRS.");
            }

            // Use centralized pagination validation
            var paginationResult = queryValidator.ValidateAndNormalizePagination(offset, limit);
            if (!paginationResult.IsValid)
            {
                return OgcErrorHelpers.CreateBadRequest(context, paginationResult.ErrorMessage ?? "Invalid paging parameters.");
            }
            var effectiveLimit = paginationResult.Value!.Limit;
            var effectiveOffset = paginationResult.Value.Offset;

            FilterExpression? filterExpression = null;
            string? combinedFilter = null;

            if (string.Equals(resolvedFilterLang, FilterLangCql2Json, StringComparison.OrdinalIgnoreCase))
            {
                FilterExpression? jsonFilterExpression = null;
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    try
                    {
                        var parser = new Cql2JsonParser();
                        jsonFilterExpression = parser.Parse(filter);
                    }
                    catch (ArgumentException ex)
                    {
                        return OgcErrorHelpers.CreateBadRequest(context, $"{InvalidCqlFilterPrefix}: {ex.Message}");
                    }
                }

                if (!TryBuildCombinedFilter(null, request, layer, out var queryableFilter, out var queryableError))
                {
                    return OgcErrorHelpers.CreateBadRequest(context, queryableError ?? "Invalid query parameters.");
                }

                FilterExpression? queryableExpression = null;
                if (!string.IsNullOrWhiteSpace(queryableFilter))
                {
                    try
                    {
                        var parser = new Cql2Parser();
                        queryableExpression = parser.Parse(queryableFilter);
                        combinedFilter = queryableFilter;
                    }
                    catch (ArgumentException ex)
                    {
                        return OgcErrorHelpers.CreateBadRequest(context, $"{InvalidCqlFilterPrefix}: {ex.Message}");
                    }
                }

                filterExpression = CombineFilters(jsonFilterExpression, queryableExpression);
            }
            else
            {
                if (!TryBuildCombinedFilter(filter, request, layer, out combinedFilter, out var filterError))
                {
                    return OgcErrorHelpers.CreateBadRequest(context, filterError ?? "Invalid query parameters.");
                }

                if (!string.IsNullOrWhiteSpace(combinedFilter))
                {
                    try
                    {
                        var parser = new Cql2Parser();
                        filterExpression = parser.Parse(combinedFilter);
                    }
                    catch (ArgumentException ex)
                    {
                        return OgcErrorHelpers.CreateBadRequest(context, $"{InvalidCqlFilterPrefix}: {ex.Message}");
                    }
                }
            }

            if (filterExpression != null)
            {
                filterExpression = NormalizeFilterAxisOrder(filterExpression, filterCrsDefinition.AxisOrder);
            }

            SqlFragment? sqlFilter = null;
            var sqlTranslator = context.RequestServices.GetService<ISqlFilterTranslator>();
            if (filterExpression != null && sqlTranslator != null)
            {
                try
                {
                    sqlFilter = sqlTranslator.Translate(filterExpression, layer);
                }
                catch (ArgumentException ex)
                {
                    return OgcErrorHelpers.CreateBadRequest(context, $"{InvalidCqlFilterPrefix}: {ex.Message}");
                }
            }

            if (!TryParseBbox(bbox, bboxCrsDefinition.AxisOrder, out var parsedBbox, out var bboxError))
            {
                return OgcErrorHelpers.CreateBadRequest(context, bboxError ?? "Invalid bbox parameter.");
            }

            SpatialFilter? spatialFilter = null;
            if (parsedBbox is not null)
            {
                spatialFilter = CreateBboxSpatialFilter(parsedBbox, bboxCrsDefinition.Srid);
            }

            if (!TryParseTemporalFilter(datetime, layer, out var temporalFilter, out var temporalError))
            {
                return OgcErrorHelpers.CreateBadRequest(context, temporalError ?? "Invalid datetime parameter.");
            }

            var query = new FeatureQuery
            {
                Where = combinedFilter,
                SqlFilter = sqlFilter,
                Offset = effectiveOffset,
                Limit = effectiveLimit,
                SpatialReferenceSrid = layer.SpatialReference.Srid,
                OutputSrid = crsDefinition.Srid,
                SpatialFilter = spatialFilter,
                TemporalFilter = temporalFilter
            };

            var result = await featureStore.QueryAsync(layerId, query, cancellationToken);
            var features = result.Items
                .Select(feature =>
                {
                    var links = BuildFeatureLinks(
                        request,
                        collectionId,
                        FormattableString.Invariant($"{feature.Id}"),
                        outputFormat);
                    return ToOgcFeature(feature, crsDefinition.AxisOrder, links);
                })
                .ToArray();

            var baseUrl = $"{request.Scheme}://{request.Host}";
            var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}/items";

            var links = BuildItemsLinks(request, basePath, outputFormat, effectiveLimit, effectiveOffset, result.HasMoreResults);

            var response = new FeatureCollection
            {
                Features = features,
                NumberMatched = result.TotalCount,
                NumberReturned = features.Length,
                Links = links,
                TimeStamp = DateTimeOffset.UtcNow
            };

            context.Response.Headers["Content-Crs"] = FormatContentCrs(crsDefinition.Uri);

            if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase))
            {
                var gml = BuildGmlFeatureCollection(features);
                return Results.Text(gml, MediaTypes.Gml);
            }

            return FormatFeatureResponse(response, OgcJsonContext.Default.FeatureCollection, outputFormat, "Features");
        }
        catch (Exception ex)
        {
            Log.ItemsQueryFailed(logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500, "Internal server error", "An error occurred while retrieving features.");
        }
    }

    private static async Task<IResult> HandleGetItem(
        string collectionId,
        string featureId,
        HttpContext context,
        string? f,
        string? crs,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        IResourceValidator resourceValidator,
        ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        var request = context.Request;

        try
        {
            var cancellationToken = GetTimeoutAwareCancellationToken(context);

            // Use centralized resource validation
            var collectionResult = await resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);
            if (!collectionResult.IsValid)
            {
                return OgcErrorHelpers.CreateNotFound(context, collectionResult.ErrorMessage ?? $"Collection '{collectionId}' not found.");
            }
            var layer = collectionResult.Resource!;
            var layerId = layer.Id;

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return OgcErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return CreateFormatError(context, formatError);
            }

            if (!TryResolveCrs(crs, out var crsDefinition, out var crsError))
            {
                return OgcErrorHelpers.CreateBadRequest(context, crsError ?? "Invalid CRS.");
            }

            var feature = await featureStore.GetAsync(layerId, objectId, cancellationToken);
            if (feature == null)
            {
                return OgcErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var featureLinks = BuildFeatureLinks(request, collectionId, featureId, outputFormat);
            var ogcFeature = ToOgcFeature(feature.Value, crsDefinition.AxisOrder, featureLinks);

            context.Response.Headers["Content-Crs"] = FormatContentCrs(crsDefinition.Uri);

            if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase))
            {
                var gml = BuildGmlSingleFeature(ogcFeature);
                return Results.Text(gml, MediaTypes.Gml);
            }

            return FormatFeatureResponse(ogcFeature, OgcJsonContext.Default.GeoJsonFeature, outputFormat, "Feature");
        }
        catch (Exception ex)
        {
            Log.ItemQueryFailed(logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500, "Internal server error", "An error occurred while retrieving the feature.");
        }
    }

    private static async Task<IResult> HandleCreateFeature(
        string collectionId,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        IResourceValidator resourceValidator,
        IOptions<LimitsOptions> limitsOptions,
        ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        try
        {
            var cancellationToken = GetTimeoutAwareCancellationToken(context);

            // Use centralized resource validation
            var collectionResult = await resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);
            if (!collectionResult.IsValid)
            {
                return OgcErrorHelpers.CreateNotFound(context, collectionResult.ErrorMessage ?? $"Collection '{collectionId}' not found.");
            }
            var layer = collectionResult.Resource!;
            var layerId = layer.Id;

            var requestFeature = await ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestFeature == null)
            {
                return OgcErrorHelpers.CreateBadRequest(context, "Invalid GeoJSON payload.");
            }

            byte[]? geometryWkb = null;
            if (requestFeature.Geometry != null)
            {
                if (!TryCreateWkbFromGeoJson(requestFeature.Geometry, 4326, out var wkb, out var error))
                {
                    return OgcErrorHelpers.CreateBadRequest(context, error ?? "Invalid geometry.");
                }
                geometryWkb = wkb;
            }

            if (geometryWkb != null)
            {
                // Use centralized geometry validation limits
                var validationResult = WkbValidation.Validate(geometryWkb, limitsOptions.Value.Validation);
                if (!validationResult.IsValid)
                {
                    return OgcErrorHelpers.CreateBadRequest(context, $"Invalid geometry: {validationResult.ErrorMessage}");
                }
            }

            var attributes = requestFeature.Properties ?? new Dictionary<string, object?>();
            var feature = Feature.Create(0, geometryWkb, attributes.ToImmutableDictionary());

            var created = await featureStore.CreateAsync(layerId, feature, cancellationToken);
            var createLinks = BuildFeatureLinks(
                context.Request,
                collectionId,
                FormattableString.Invariant($"{created.Id}"),
                MediaTypes.GeoJson);
            var response = ToOgcFeature(created, OgcFeaturesUtilities.AxisOrder.EastNorth, createLinks);

            return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson, statusCode: StatusCodes.Status201Created);
        }
        catch (ResourceConflictException ex)
        {
            return OgcErrorHelpers.CreateConflict(context, ex.Message);
        }
        catch (Exception ex)
        {
            Log.CreateFeatureFailed(logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500, "Internal server error", "An error occurred while creating the feature.");
        }
    }

    private static async Task<IResult> HandleUpdateFeature(
        string collectionId,
        string featureId,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        IResourceValidator resourceValidator,
        IOptions<LimitsOptions> limitsOptions,
        ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        try
        {
            var cancellationToken = GetTimeoutAwareCancellationToken(context);

            // Use centralized resource validation
            var collectionResult = await resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);
            if (!collectionResult.IsValid)
            {
                return OgcErrorHelpers.CreateNotFound(context, collectionResult.ErrorMessage ?? $"Collection '{collectionId}' not found.");
            }
            var layer = collectionResult.Resource!;
            var layerId = layer.Id;

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return OgcErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var requestFeature = await ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestFeature == null)
            {
                return OgcErrorHelpers.CreateBadRequest(context, "Invalid GeoJSON payload.");
            }

            byte[]? geometryWkb = null;
            if (requestFeature.Geometry != null)
            {
                if (!TryCreateWkbFromGeoJson(requestFeature.Geometry, 4326, out var wkb, out var error))
                {
                    return OgcErrorHelpers.CreateBadRequest(context, error ?? "Invalid geometry.");
                }
                geometryWkb = wkb;
            }

            if (geometryWkb != null)
            {
                // Use centralized geometry validation limits
                var validationResult = WkbValidation.Validate(geometryWkb, limitsOptions.Value.Validation);
                if (!validationResult.IsValid)
                {
                    return OgcErrorHelpers.CreateBadRequest(context, $"Invalid geometry: {validationResult.ErrorMessage}");
                }
            }

            var attributes = requestFeature.Properties ?? new Dictionary<string, object?>();
            var feature = Feature.Create(objectId, geometryWkb, attributes.ToImmutableDictionary());

            Feature updated;
            try
            {
                updated = await featureStore.UpdateAsync(layerId, feature, cancellationToken);
            }
            catch (ResourceConflictException ex)
            {
                return OgcErrorHelpers.CreateConflict(context, ex.Message);
            }
            catch (ResourceNotFoundException)
            {
                return OgcErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (InvalidOperationException)
            {
                return OgcErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var updateLinks = BuildFeatureLinks(
                context.Request,
                collectionId,
                FormattableString.Invariant($"{updated.Id}"),
                MediaTypes.GeoJson);
            var response = ToOgcFeature(updated, OgcFeaturesUtilities.AxisOrder.EastNorth, updateLinks);
            return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
        }
        catch (Exception ex)
        {
            Log.UpdateFeatureFailed(logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500, "Internal server error", "An error occurred while updating the feature.");
        }
    }

    private static async Task<IResult> HandleDeleteFeature(
        string collectionId,
        string featureId,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        IResourceValidator resourceValidator,
        ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        try
        {
            var cancellationToken = GetTimeoutAwareCancellationToken(context);

            // Use centralized resource validation
            var collectionResult = await resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);
            if (!collectionResult.IsValid)
            {
                return OgcErrorHelpers.CreateNotFound(context, collectionResult.ErrorMessage ?? $"Collection '{collectionId}' not found.");
            }
            var layer = collectionResult.Resource!;
            var layerId = layer.Id;

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return OgcErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var deleted = await featureStore.DeleteAsync(layerId, objectId, cancellationToken);
            if (!deleted)
            {
                return OgcErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            return Results.NoContent();
        }
        catch (Exception ex)
        {
            Log.DeleteFeatureFailed(logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500, "Internal server error", "An error occurred while deleting the feature.");
        }
    }

    private static async Task<GeoJsonFeature?> ReadGeoJsonFeatureAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("type", out var typeProperty) ||
                !string.Equals(typeProperty.GetString(), "Feature", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return JsonSerializer.Deserialize(body, OgcJsonContext.Default.GeoJsonFeature);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryResolveCrs(
        string? crs,
        out OgcFeaturesUtilities.CrsDefinition definition,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(crs))
        {
            definition = _supportedCrs[OgcFeaturesUtilities.Crs84Uri];
            error = null;
            return true;
        }

        if (_supportedCrs.TryGetValue(crs, out definition))
        {
            error = null;
            return true;
        }

        definition = default;
        error = $"Unsupported CRS '{crs}'.";
        return false;
    }

    private static bool TryResolveFilterLanguage(
        string? filterLang,
        out string resolved,
        out string? error)
    {
        resolved = FilterLangCql2Text;
        error = null;

        if (string.IsNullOrWhiteSpace(filterLang))
        {
            return true;
        }

        if (string.Equals(filterLang, FilterLangCql2Text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(filterLang, FilterLangCql2Json, StringComparison.OrdinalIgnoreCase))
        {
            resolved = filterLang.Trim();
            return true;
        }

        error = $"Unsupported filter language '{filterLang}'.";
        return false;
    }

    private static bool TryBuildCombinedFilter(
        string? filter,
        HttpRequest request,
        LayerDefinition layer,
        out string? combinedFilter,
        out string? error)
    {
        error = null;
        var fragments = new List<string>();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            fragments.Add(filter.Trim());
        }

        foreach (var (key, values) in request.Query)
        {
            if (OgcFeaturesUtilities.AllowedQueryParameters.Items.Contains(key))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(values))
            {
                continue;
            }

            var field = layer.AttributeFields.FirstOrDefault(f => f.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (field == null)
            {
                error = $"Unknown query parameter: {key}";
                combinedFilter = null;
                return false;
            }

            if (!OgcFeaturesUtilities.IsSimpleQueryableField(field))
            {
                error = $"Field '{field.Name}' is not queryable.";
                combinedFilter = null;
                return false;
            }

            if (!TryFormatQueryableValue(field, values.ToString(), out var literal, out var formatError))
            {
                error = formatError;
                combinedFilter = null;
                return false;
            }

            fragments.Add($"{field.Name} = {literal}");
        }

        combinedFilter = fragments.Count == 0 ? null : string.Join(" AND ", fragments);
        return true;
    }

    private static string? GetQueryValue(HttpRequest request, string key)
    {
        if (!request.Query.TryGetValue(key, out var values))
        {
            return null;
        }

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static FilterExpression? CombineFilters(FilterExpression? left, FilterExpression? right)
    {
        if (left == null)
        {
            return right;
        }

        if (right == null)
        {
            return left;
        }

        return new BinaryExpression(left, BinaryOperator.And, right);
    }

    private static FilterExpression NormalizeFilterAxisOrder(
        FilterExpression filterExpression,
        OgcFeaturesUtilities.AxisOrder axisOrder)
    {
        if (axisOrder == OgcFeaturesUtilities.AxisOrder.EastNorth)
        {
            return filterExpression;
        }

        return SwapAxisOrder(filterExpression);
    }

    private static FilterExpression SwapAxisOrder(FilterExpression filterExpression)
    {
        return filterExpression switch
        {
            GeometryLiteral geometry => SwapGeometryLiteral(geometry),
            BinaryExpression binary => binary with
            {
                Left = SwapAxisOrder(binary.Left),
                Right = SwapAxisOrder(binary.Right)
            },
            UnaryExpression unary => unary with { Operand = SwapAxisOrder(unary.Operand) },
            SpatialPredicate spatial => spatial with
            {
                Left = SwapAxisOrder(spatial.Left),
                Right = SwapAxisOrder(spatial.Right)
            },
            SpatialDistancePredicate spatialDistance => spatialDistance with
            {
                Left = SwapAxisOrder(spatialDistance.Left),
                Right = SwapAxisOrder(spatialDistance.Right),
                Distance = SwapAxisOrder(spatialDistance.Distance)
            },
            TemporalPredicate temporal => temporal with
            {
                Left = SwapAxisOrder(temporal.Left),
                Right = SwapAxisOrder(temporal.Right)
            },
            ArrayPredicate array => array with
            {
                Left = SwapAxisOrder(array.Left),
                Right = SwapAxisOrder(array.Right)
            },
            FunctionCall functionCall => functionCall with
            {
                Arguments = functionCall.Arguments.Select(SwapAxisOrder).ToArray()
            },
            ArrayLiteral arrayLiteral => arrayLiteral with
            {
                Elements = arrayLiteral.Elements.Select(SwapAxisOrder).ToArray()
            },
            ValueList valueList => valueList with
            {
                Values = valueList.Values.Select(SwapAxisOrder).ToArray()
            },
            _ => filterExpression
        };
    }

    private static GeometryLiteral SwapGeometryLiteral(GeometryLiteral geometry)
    {
        if (geometry.Wkb.Length == 0)
        {
            return geometry;
        }

        try
        {
            var reader = new WKBReader();
            var parsed = reader.Read(geometry.Wkb);
            if (parsed == null)
            {
                return geometry;
            }

            var clone = (Geometry)parsed.Copy();
            clone.Apply(new AxisSwapFilter());
            clone.GeometryChanged();

            var (hasZ, hasM) = GetHasZandM(clone);
            var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: hasZ, emitM: hasM);
            var wkb = writer.Write(clone);

            return geometry with { Wkb = wkb };
        }
        catch (Exception)
        {
            return geometry;
        }
    }

    private static bool TryFormatQueryableValue(
        FieldDefinition field,
        string value,
        out string literal,
        out string? error)
    {
        literal = string.Empty;
        error = null;

        switch (field.Type)
        {
            case FieldType.Integer:
            case FieldType.BigInteger:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    error = $"Invalid value for '{field.Name}'.";
                    return false;
                }
                literal = FormattableString.Invariant($"{longValue}");
                return true;
            case FieldType.Double:
            case FieldType.Float:
                if (!double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    error = $"Invalid value for '{field.Name}'.";
                    return false;
                }
                literal = FormattableString.Invariant($"{doubleValue}");
                return true;
            case FieldType.Boolean:
                if (!bool.TryParse(value, out var boolValue))
                {
                    error = $"Invalid value for '{field.Name}'.";
                    return false;
                }
                literal = boolValue ? "true" : "false";
                return true;
            default:
                var escaped = value.Replace("'", "''", StringComparison.Ordinal);
                literal = $"'{escaped}'";
                return true;
        }
    }

    private static bool TryParseBbox(
        string? bboxValue,
        OgcFeaturesUtilities.AxisOrder axisOrder,
        out BoundingBox? bbox,
        out string? error)
    {
        bbox = null;
        error = null;

        if (string.IsNullOrWhiteSpace(bboxValue))
        {
            return true;
        }

        var parts = bboxValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 && parts.Length != 6)
        {
            error = "Bounding box must contain 4 or 6 comma-separated values.";
            return false;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var first) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var second) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var third) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var fourth))
        {
            error = "Bounding box coordinates must be valid numbers.";
            return false;
        }

        var (minX, minY, maxX, maxY) = axisOrder == OgcFeaturesUtilities.AxisOrder.NorthEast
            ? (second, first, fourth, third)
            : (first, second, third, fourth);

        if (minY > maxY)
        {
            error = "Bounding box minimum latitude must be less than or equal to maximum latitude.";
            return false;
        }

        if (minX < -180 || maxX > 180 || minY < -90 || maxY > 90)
        {
            error = "Bounding box coordinates are out of valid range.";
            return false;
        }

        bbox = new BoundingBox(minX, minY, maxX, maxY);
        return true;
    }

    private static SpatialFilter CreateBboxSpatialFilter(BoundingBox bbox, int srid)
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
        Geometry geometry;
        if (bbox.MinX <= bbox.MaxX)
        {
            var envelope = new Envelope(bbox.MinX, bbox.MaxX, bbox.MinY, bbox.MaxY);
            geometry = factory.ToGeometry(envelope);
        }
        else
        {
            // Handle anti-meridian crossing by splitting into two envelopes.
            var leftEnvelope = new Envelope(bbox.MinX, 180, bbox.MinY, bbox.MaxY);
            var rightEnvelope = new Envelope(-180, bbox.MaxX, bbox.MinY, bbox.MaxY);
            var leftPolygon = (Polygon)factory.ToGeometry(leftEnvelope);
            var rightPolygon = (Polygon)factory.ToGeometry(rightEnvelope);
            geometry = factory.CreateMultiPolygon(new[] { leftPolygon, rightPolygon });
        }

        var (hasZ, hasM) = GetHasZandM(geometry);
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: srid > 0, emitZ: hasZ, emitM: hasM);
        var wkb = writer.Write(geometry);

        return new SpatialFilter
        {
            Geometry = wkb,
            Srid = srid,
            SpatialRelationship = SpatialRelationship.Intersects
        };
    }

    private static bool TryParseTemporalFilter(
        string? datetime,
        LayerDefinition layer,
        out TemporalFilter? temporalFilter,
        out string? error)
    {
        temporalFilter = null;
        error = null;

        if (string.IsNullOrWhiteSpace(datetime))
        {
            return true;
        }

        var temporalField = layer.AttributeFields.FirstOrDefault(field =>
            field.Type is FieldType.DateTime or FieldType.Date);

        if (temporalField == null)
        {
            error = "No temporal field is available for filtering.";
            return false;
        }

        var parts = datetime.Split('/', StringSplitOptions.TrimEntries);
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        if (parts.Length == 1)
        {
            if (!TryParseDateTimeOffset(parts[0], out var instant))
            {
                error = "Invalid datetime parameter.";
                return false;
            }
            start = instant;
            end = instant;
        }
        else if (parts.Length == 2)
        {
            if (!string.IsNullOrWhiteSpace(parts[0]) && parts[0] != "..")
            {
                if (!TryParseDateTimeOffset(parts[0], out var parsedStart))
                {
                    error = "Invalid datetime parameter.";
                    return false;
                }
                start = parsedStart;
            }

            if (!string.IsNullOrWhiteSpace(parts[1]) && parts[1] != "..")
            {
                if (!TryParseDateTimeOffset(parts[1], out var parsedEnd))
                {
                    error = "Invalid datetime parameter.";
                    return false;
                }
                end = parsedEnd;
            }
        }
        else
        {
            error = "Invalid datetime parameter.";
            return false;
        }

        temporalFilter = new TemporalFilter
        {
            PropertyName = temporalField.Name,
            PropertyType = temporalField.Type == FieldType.Date ? TemporalPropertyType.Date : TemporalPropertyType.DateTime,
            Start = start,
            End = end
        };

        return true;
    }

    private static bool TryParseDateTimeOffset(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);

    private static GeoJsonFeature ToOgcFeature(
        Feature feature,
        OgcFeaturesUtilities.AxisOrder axisOrder,
        ImmutableArray<Link>? links = null)
    {
        var geometry = ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
        return feature.ToGeoJsonBase().ToOgcGeoJsonFeature(geometry, links);
    }

    private static SimpleGeoJsonGeometry? ConvertWkbToSimpleGeometry(byte[]? wkb, OgcFeaturesUtilities.AxisOrder axisOrder)
    {
        if (wkb == null || wkb.Length == 0)
        {
            return null;
        }

        var reader = new WKBReader();
        var geometry = reader.Read(wkb);
        if (geometry == null)
        {
            return null;
        }

        if (axisOrder == OgcFeaturesUtilities.AxisOrder.NorthEast)
        {
            geometry = (Geometry)geometry.Copy();
            geometry.Apply(new AxisSwapFilter());
            geometry.GeometryChanged();
        }

        var writer = new GeoJsonWriter();
        var geoJson = writer.Write(geometry);

        using var document = JsonDocument.Parse(geoJson);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? "Geometry";

        string? coordinatesJson = null;
        string? geometriesJson = null;

        if (root.TryGetProperty("coordinates", out var coordinates))
        {
            coordinatesJson = coordinates.GetRawText();
        }

        if (root.TryGetProperty("geometries", out var geometries))
        {
            geometriesJson = geometries.GetRawText();
        }

        return new SimpleGeoJsonGeometry
        {
            Type = type,
            CoordinatesJson = coordinatesJson,
            GeometriesJson = geometriesJson
        };
    }

    private static bool TryCreateWkbFromGeoJson(
        SimpleGeoJsonGeometry geometry,
        int srid,
        out byte[] wkb,
        out string? error)
    {
        wkb = Array.Empty<byte>();
        error = null;

        var coordinatesJson = geometry.CoordinatesJson;
        var geometriesJson = geometry.GeometriesJson;

        if (string.IsNullOrWhiteSpace(coordinatesJson) && string.IsNullOrWhiteSpace(geometriesJson))
        {
            error = "Geometry coordinates are required.";
            return false;
        }

        var json = coordinatesJson is not null
            ? $"{{\"type\":\"{geometry.Type}\",\"coordinates\":{coordinatesJson}}}"
            : $"{{\"type\":\"{geometry.Type}\",\"geometries\":{geometriesJson}}}";

        try
        {
            var reader = new GeoJsonReader();
            var ntsGeometry = reader.Read<Geometry>(json);
            if (ntsGeometry == null)
            {
                error = "Invalid geometry.";
                return false;
            }

            if (srid > 0)
            {
                ntsGeometry.SRID = srid;
            }

            var (hasZ, hasM) = GetHasZandM(ntsGeometry);
            var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: srid > 0, emitZ: hasZ, emitM: hasM);
            wkb = writer.Write(ntsGeometry);
            return true;
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException)
        {
            error = "Invalid geometry.";
            return false;
        }
    }

    private static (bool hasZ, bool hasM) GetHasZandM(Geometry geometry)
    {
        if (geometry is GeometryCollection collection && collection.NumGeometries > 0)
        {
            return GetHasZandM(collection.GetGeometryN(0));
        }

        CoordinateSequence? sequence = geometry switch
        {
            Point point => point.CoordinateSequence,
            LineString lineString => lineString.CoordinateSequence,
            Polygon polygon => polygon.ExteriorRing.CoordinateSequence,
            MultiPoint multiPoint when multiPoint.NumGeometries > 0 => ((Point)multiPoint.GetGeometryN(0)).CoordinateSequence,
            MultiLineString multiLineString when multiLineString.NumGeometries > 0 => ((LineString)multiLineString.GetGeometryN(0)).CoordinateSequence,
            MultiPolygon multiPolygon when multiPolygon.NumGeometries > 0 => ((Polygon)multiPolygon.GetGeometryN(0)).ExteriorRing.CoordinateSequence,
            _ => null
        };

        if (sequence == null)
        {
            return (false, false);
        }

        var hasZ = !double.IsNaN(sequence.GetZ(0));
        var hasM = !double.IsNaN(sequence.GetM(0));
        return (hasZ, hasM);
    }

    private sealed class AxisSwapFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq, int i)
        {
            var x = seq.GetX(i);
            var y = seq.GetY(i);
            seq.SetX(i, y);
            seq.SetY(i, x);
        }
    }

    private static ImmutableArray<Link> BuildItemsLinks(
        HttpRequest request,
        string basePath,
        string outputFormat,
        int limit,
        int? offset,
        bool hasMoreResults)
    {
        var links = OgcFeaturesUtilities.BuildFormatLinks(
            request,
            basePath,
            outputFormat,
            OgcFeaturesUtilities.FeatureFormats,
            "Items").ToBuilder();

        if (offset.HasValue && offset.Value > 0)
        {
            var prevOffset = Math.Max(0, offset.Value - limit);
            links.Add(Link.Create(
                href: BuildPagedUrl(request, basePath, outputFormat, limit, prevOffset),
                rel: RelationTypes.Prev,
                type: outputFormat,
                title: "Previous page"));
        }

        if (hasMoreResults)
        {
            var nextOffset = (offset ?? 0) + limit;
            links.Add(Link.Create(
                href: BuildPagedUrl(request, basePath, outputFormat, limit, nextOffset),
                rel: RelationTypes.Next,
                type: outputFormat,
                title: "Next page"));
        }

        return links.ToImmutable();
    }

    private static ImmutableArray<Link> BuildFeatureLinks(
        HttpRequest request,
        string collectionId,
        string featureId,
        string outputFormat)
    {
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{featureId}";

        var links = new List<Link>
        {
            Link.Create(
                href: basePath,
                rel: RelationTypes.Self,
                type: outputFormat,
                title: "Feature")
        };

        foreach (var format in OgcFeaturesUtilities.FeatureFormats)
        {
            if (string.Equals(format.MediaType, outputFormat, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            links.Add(Link.Create(
                href: $"{basePath}?f={Uri.EscapeDataString(format.QueryValue)}",
                rel: RelationTypes.Alternate,
                type: format.MediaType,
                title: format.Title));
        }

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionId}",
            rel: RelationTypes.Collection,
            type: MediaTypes.Json,
            title: "Collection"));

        return links.ToImmutableArray();
    }

    private static string BuildPagedUrl(
        HttpRequest request,
        string basePath,
        string outputFormat,
        int limit,
        int offset)
    {
        var queryParts = new List<string>();

        foreach (var (key, value) in request.Query)
        {
            if (string.Equals(key, "offset", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "limit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "f", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                queryParts.Add($"{key}={Uri.EscapeDataString(value.ToString())}");
            }
        }

        queryParts.Add(FormattableString.Invariant($"limit={limit}"));
        queryParts.Add(FormattableString.Invariant($"offset={offset}"));

        var formatValue = outputFormat switch
        {
            var format when string.Equals(format, MediaTypes.Json, StringComparison.OrdinalIgnoreCase) => "json",
            var format when string.Equals(format, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase) => "geojson",
            var format when string.Equals(format, MediaTypes.Html, StringComparison.OrdinalIgnoreCase) => "html",
            var format when string.Equals(format, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase) => "gml",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(formatValue))
        {
            queryParts.Add($"f={formatValue}");
        }

        return queryParts.Count > 0
            ? $"{basePath}?{string.Join("&", queryParts)}"
            : basePath;
    }

    private static IResult FormatFeatureResponse<T>(
        T payload,
        JsonTypeInfo<T> typeInfo,
        string outputFormat,
        string title)
    {
        if (string.Equals(outputFormat, MediaTypes.Html, StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(payload, typeInfo);
            var html = BuildHtmlDocument(title, json);
            return Results.Text(html, MediaTypes.Html);
        }

        var contentType = string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase)
            ? MediaTypes.GeoJson
            : MediaTypes.Json;

        return Results.Json(payload, typeInfo, contentType: contentType);
    }

    private static string FormatContentCrs(string crsUri)
        => $"<{crsUri}>";

    private static string BuildHtmlDocument(string title, string json)
    {
        return $@"<!DOCTYPE html>
<html>
<head>
    <title>{title}</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 40px; }}
        pre {{ background: #f5f5f5; padding: 20px; border-radius: 5px; overflow: auto; }}
        .title {{ color: #333; margin-bottom: 20px; }}
    </style>
</head>
<body>
    <h1 class=""title"">{title}</h1>
    <pre><code>{json}</code></pre>
</body>
</html>";
    }

    private static IResult CreateFormatError(HttpContext context, IResult? formatError)
    {
        if (formatError is BadRequest<string> badRequest)
        {
            return OgcErrorHelpers.CreateBadRequest(context, badRequest.Value ?? "Invalid format.");
        }

        if (formatError is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode.HasValue)
        {
            return ProtocolErrorWriter.CreateErrorResult(
                context,
                statusCodeResult.StatusCode.Value,
                "Not Acceptable",
                "Requested format is not acceptable.");
        }

        return OgcErrorHelpers.CreateBadRequest(context, "Invalid format.");
    }

    private static string BuildGmlFeatureCollection(IEnumerable<GeoJsonFeature> features)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"<wfs:FeatureCollection xmlns:wfs=\"{OgcFeaturesUtilities.WfsNamespace}\" xmlns:gml=\"{OgcFeaturesUtilities.GmlNamespace}\">");

        foreach (var feature in features)
        {
            builder.AppendLine("  <wfs:member>");
            builder.AppendLine($"    <gml:Feature gml:id=\"{feature.Id}\">");
            builder.AppendLine(BuildGmlGeometry(feature.Geometry));
            builder.AppendLine("    </gml:Feature>");
            builder.AppendLine("  </wfs:member>");
        }

        builder.AppendLine("</wfs:FeatureCollection>");
        return builder.ToString();
    }

    private static string BuildGmlSingleFeature(GeoJsonFeature feature)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"<wfs:FeatureCollection xmlns:wfs=\"{OgcFeaturesUtilities.WfsNamespace}\" xmlns:gml=\"{OgcFeaturesUtilities.GmlNamespace}\">");
        builder.AppendLine("  <wfs:member>");
        builder.AppendLine($"    <gml:Feature gml:id=\"{feature.Id}\">");
        builder.AppendLine(BuildGmlGeometry(feature.Geometry));
        builder.AppendLine("    </gml:Feature>");
        builder.AppendLine("  </wfs:member>");
        builder.AppendLine("</wfs:FeatureCollection>");
        return builder.ToString();
    }

    private static string BuildGmlGeometry(SimpleGeoJsonGeometry? geometry)
    {
        if (geometry?.CoordinatesJson == null)
        {
            return "      <gml:Point />";
        }

        var coordinates = geometry.CoordinatesJson.Trim();
        return $"      <gml:Point><gml:pos>{coordinates}</gml:pos></gml:Point>";
    }

    private static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            return timeoutToken;
        }

        return context.RequestAborted;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5210, Level = LogLevel.Error, Message = "OGC items query failed for collection {CollectionId}")]
        public static partial void ItemsQueryFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5211, Level = LogLevel.Error, Message = "OGC item query failed for collection {CollectionId}")]
        public static partial void ItemQueryFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5212, Level = LogLevel.Error, Message = "OGC create feature failed for collection {CollectionId}")]
        public static partial void CreateFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5213, Level = LogLevel.Error, Message = "OGC update feature failed for collection {CollectionId}")]
        public static partial void UpdateFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5214, Level = LogLevel.Error, Message = "OGC delete feature failed for collection {CollectionId}")]
        public static partial void DeleteFeatureFailed(ILogger logger, string collectionId, Exception exception);
    }
}
