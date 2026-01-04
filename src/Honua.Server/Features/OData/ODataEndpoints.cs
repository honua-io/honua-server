// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.OData;

/// <summary>
/// OData v4 endpoints providing intermediate conformance level.
/// Supports $filter, $select, $orderby, $top, $skip, $count, and CRUD operations.
/// </summary>
internal static partial class ODataEndpoints
{
    internal sealed class ODataEndpointsLog
    {
    }

    /// <summary>
    /// Maps OData v4 endpoints
    /// </summary>
    public static IEndpointRouteBuilder MapODataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var requireAuth = ShouldRequireODataAuth(endpoints);

        // OData service document
        var serviceDocument = endpoints.MapGet("/odata", HandleGetServiceDocument)
            .WithDisplayName("OData Service Document")
            .WithName("ODataServiceDocument")
            .WithSummary("Get OData service document")
            .WithTags("OData")
            .Produces<ServiceDocument>(200, "application/json")
            .Produces(404);
        if (requireAuth)
        {
            serviceDocument.RequireAuthorization();
        }

        // OData metadata document
        var metadata = endpoints.MapGet("/odata/$metadata", HandleGetMetadata)
            .WithDisplayName("OData Metadata Document")
            .WithName("ODataMetadata")
            .WithSummary("Get OData metadata document")
            .WithTags("OData")
            .Produces<string>(200, "application/xml")
            .Produces(404);
        if (requireAuth)
        {
            metadata.RequireAuthorization();
        }

        // OData entity sets (layers as collections)
        var layers = endpoints.MapGet("/odata/Layers", HandleGetLayers)
            .WithDisplayName("OData Layers Collection")
            .WithName("ODataLayers")
            .WithSummary("Get layers collection with OData query parameters")
            .WithTags("OData")
            .Produces<ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);
        if (requireAuth)
        {
            layers.RequireAuthorization();
        }

        // OData features for a specific layer
        var features = endpoints.MapGet("/odata/Features({layerId:int})", HandleGetFeatures)
            .WithDisplayName("OData Features Collection")
            .WithName("ODataFeatures")
            .WithSummary("Get features with OData query parameters ($filter, $select, $orderby, $top, $skip, $count)")
            .WithTags("OData")
            .Produces<ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);
        if (requireAuth)
        {
            features.RequireAuthorization();
        }

        // POST - Create a new feature
        var createFeature = endpoints.MapPost("/odata/Features({layerId:int})", HandleCreateFeature)
            .WithDisplayName("OData Create Feature")
            .WithName("ODataCreateFeature")
            .WithSummary("Create a new feature in the specified layer")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(201, "application/json")
            .Produces(400)
            .Produces(404);
        if (requireAuth)
        {
            createFeature.RequireAuthorization();
        }

        // GET - Get a single feature
        var getFeature = endpoints.MapGet("/odata/Features({layerId:int},{objectId:long})", HandleGetSingleFeature)
            .WithDisplayName("OData Get Feature")
            .WithName("ODataGetFeature")
            .WithSummary("Get a single feature by ID")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(404);
        if (requireAuth)
        {
            getFeature.RequireAuthorization();
        }

        // PATCH - Update an existing feature
        var updateFeature = endpoints.MapPatch("/odata/Features({layerId:int},{objectId:long})", HandleUpdateFeature)
            .WithDisplayName("OData Update Feature")
            .WithName("ODataUpdateFeature")
            .WithSummary("Update an existing feature")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(400)
            .Produces(404);
        if (requireAuth)
        {
            updateFeature.RequireAuthorization();
        }

        // DELETE - Delete a feature
        var deleteFeature = endpoints.MapDelete("/odata/Features({layerId:int},{objectId:long})", HandleDeleteFeature)
            .WithDisplayName("OData Delete Feature")
            .WithName("ODataDeleteFeature")
            .WithSummary("Delete a feature")
            .WithTags("OData")
            .Produces(204)
            .Produces(404);
        if (requireAuth)
        {
            deleteFeature.RequireAuthorization();
        }

        // POST - Batch operations
        var batch = endpoints.MapPost("/odata/$batch", HandleBatch)
            .WithDisplayName("OData Batch Operations")
            .WithName("ODataBatch")
            .WithSummary("Execute multiple operations in a single request with optional atomicity groups")
            .WithTags("OData")
            .Produces<ODataBatchResponse>(200, "application/json")
            .Produces(400);
        if (requireAuth)
        {
            batch.RequireAuthorization();
        }

        // GET - Aggregation with $apply
        var apply = endpoints.MapGet("/odata/Features({layerId:int})/$apply", HandleApply)
            .WithDisplayName("OData Aggregation")
            .WithName("ODataApply")
            .WithSummary("Aggregate features using $apply transformations (aggregate, groupby, filter, compute)")
            .WithTags("OData")
            .Produces<ODataAggregationResult>(200, "application/json")
            .Produces(400)
            .Produces(404);
        if (requireAuth)
        {
            apply.RequireAuthorization();
        }

        // GET - Full-text search with $search
        var search = endpoints.MapGet("/odata/Features({layerId:int})/$search", HandleSearch)
            .WithDisplayName("OData Search")
            .WithName("ODataSearch")
            .WithSummary("Full-text search across feature attributes using PostgreSQL text search")
            .WithTags("OData")
            .Produces<ODataSearchResult>(200, "application/json")
            .Produces(400)
            .Produces(404);
        if (requireAuth)
        {
            search.RequireAuthorization();
        }

        return endpoints;
    }

    private static bool ShouldRequireODataAuth(IEndpointRouteBuilder endpoints)
    {
        var oidcOptions = endpoints.ServiceProvider
            .GetRequiredService<IOptions<OidcAuthenticationOptions>>()
            .Value;

        if (!oidcOptions.Enabled)
        {
            return false;
        }

        return oidcOptions.AzureAd?.IsValid == true ||
               oidcOptions.Google?.IsValid == true ||
               oidcOptions.Generic?.IsValid == true;
    }

    /// <summary>
    /// Handles OData service document request
    /// </summary>
    private static IResult HandleGetServiceDocument(
        HttpContext context,
        ODataMetadataService metadataService,
        ODataValidationService validationService)
    {
        var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var serviceDocument = metadataService.GenerateServiceDocument(baseUrl);

        ODataUtilityService.SetODataHeaders(context);
        return Results.Json(serviceDocument, ODataJsonContext.Default.ServiceDocument,
            contentType: ODataUtilityService.GetODataContentType());
    }

    /// <summary>
    /// Handles OData metadata document request
    /// </summary>
    private static async Task<IResult> HandleGetMetadata(
        HttpContext context,
        ODataMetadataService metadataService,
        ODataValidationService validationService,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        ODataUtilityService.SetODataHeaders(context);
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
        var metadata = await metadataService.GenerateMetadataDocumentAsync(effectiveToken);
        return TypedResults.Content(metadata, "application/xml");
    }

    /// <summary>
    /// Handles OData layers collection request
    /// </summary>
    private static async Task<IResult> HandleGetLayers(
        HttpContext context,
        ILayerCatalog layerCatalog,
        ODataValidationService validationService,
        ODataQuerySearchService querySearchService,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$top")] int? top = null,
        [FromQuery(Name = "$skip")] int? skip = null,
        [FromQuery(Name = "$count")] bool? count = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.Layers);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            var paginationResult = validationService.ValidateAndNormalizePagination(skip, top);
            if (!paginationResult.IsValid)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption",
                    paginationResult.ErrorMessage ?? "Invalid OData query.");
            }

            var pagination = paginationResult.Value!;
            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var layers = await layerCatalog.ListLayersAsync(effectiveToken);

            // Apply filtering and processing
            IEnumerable<LayerDefinition> layerQuery = layers;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                layerQuery = querySearchService.ApplyBasicFilter((IEnumerable<Core.Features.Catalog.Domain.LayerDefinition>)layerQuery, filter);
            }

            // Apply pagination and counting
            long? totalCount = null;
            if (count == true)
            {
                var layersForCount = layerQuery.ToArray();
                totalCount = layersForCount.Length;
                layerQuery = layersForCount;
            }

            if (pagination.Offset > 0)
            {
                layerQuery = layerQuery.Skip(pagination.Offset);
            }

            layerQuery = layerQuery.Take(pagination.Limit);

            // Convert to response format
            var layerData = layerQuery.Select(l => new Dictionary<string, object?>
            {
                ["Id"] = l.Id,
                ["Name"] = l.Name,
                ["Description"] = l.Description
            }).ToArray();

            // Apply field selection if specified
            object[] result = string.IsNullOrWhiteSpace(select)
                ? layerData.Cast<object>().ToArray()
                : querySearchService.ApplyFieldSelection(layerData, select);

            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
            var response = ODataUtilityService.CreateODataResponse(baseUrl, "Layers", result, totalCount);

            ODataUtilityService.SetODataHeaders(context);
            return Results.Json(response, ODataJsonContext.Default.ODataResponse,
                contentType: ODataUtilityService.GetODataContentType());
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            Log.InvalidLayersQuery(_logger, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "InvalidQuery", safeDetail);
        }
        catch (Exception ex)
        {
            Log.LayersQueryFailed(_logger, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the OData request", 500);
        }
    }

    /// <summary>
    /// Handles OData features collection request with full query parameter support
    /// </summary>
    private static async Task<IResult> HandleGetFeatures(
        HttpContext context,
        int layerId,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ODataValidationService validationService,
        ODataQuerySearchService querySearchService,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$orderby")] string? orderby = null,
        [FromQuery(Name = "$top")] int? top = null,
        [FromQuery(Name = "$skip")] int? skip = null,
        [FromQuery(Name = "$count")] bool? count = null,
        [FromQuery(Name = "$expand")] string? expand = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.Features);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            var paginationResult = validationService.ValidateAndNormalizePagination(skip, top);
            if (!paginationResult.IsValid)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption",
                    paginationResult.ErrorMessage ?? "Invalid OData query.");
            }

            var pagination = paginationResult.Value!;
            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

            // Verify layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return ODataUtilityService.CreateODataError(context, "ResourceNotFound",
                    $"Layer {layerId} not found", 404);
            }

            // Build feature query using query service
            var featureQuery = querySearchService.BuildFeatureQuery(
                filter, orderby, pagination.Limit,
                pagination.Offset, layer, out var spatialFilter, out var queryError);

            if (queryError != null)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQuery", queryError);
            }

            // Execute query
            var queryResult = await featureStore.QueryAsync(layerId, featureQuery, effectiveToken);

            // Process $expand for related entities
            Dictionary<long, Dictionary<string, object?[]>>? expandedRelations = null;
            if (!string.IsNullOrWhiteSpace(expand) && layer.HasRelationships)
            {
                expandedRelations = await querySearchService.ProcessExpandAsync(
                    expand, layer, queryResult.Items.Select(f => f.Id).ToArray(), effectiveToken);
            }

            // Convert features to OData format
            var featuresData = queryResult.Items.Select(f =>
            {
                var dict = new Dictionary<string, object?>
                {
                    ["ObjectId"] = f.Id,
                    ["LayerId"] = layerId,
                    ["Geometry"] = f.Geometry != null ? Convert.ToBase64String(f.Geometry) : null,
                    ["Attributes"] = SerializeAttributes(f.Attributes)
                };

                // Add expanded relations if available
                if (expandedRelations != null && expandedRelations.TryGetValue(f.Id, out var relations))
                {
                    foreach (var kvp in relations)
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                }

                return dict;
            }).ToArray();

            // Apply field selection if specified
            object[] result = string.IsNullOrWhiteSpace(select)
                ? featuresData.Cast<object>().ToArray()
                : querySearchService.ApplyFieldSelection(featuresData, select);

            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

            // Calculate @odata.nextLink if there are more results
            string? nextLink = null;
            if (ODataUtilityService.ShouldPaginate(result.Length, pagination.Offset, (int)queryResult.TotalCount, pagination.Limit))
            {
                var nextSkip = ODataUtilityService.CalculateNextSkip(pagination.Offset, pagination.Limit);
                nextLink = ODataUtilityService.GenerateNextLink(context.Request, layerId, nextSkip, pagination.Limit,
                    filter, select, orderby, count);
            }

            var response = new ODataResponse
            {
                Context = ODataUtilityService.BuildContextUrl(baseUrl, "Features"),
                Count = count == true ? queryResult.TotalCount : null,
                NextLink = nextLink,
                Value = result
            };

            ODataUtilityService.SetODataHeaders(context);
            return Results.Json(response, ODataJsonContext.Default.ODataResponse,
                contentType: ODataUtilityService.GetODataContentType());
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            Log.InvalidFeaturesQuery(_logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "InvalidQuery", safeDetail);
        }
        catch (Exception ex)
        {
            Log.FeaturesQueryFailed(_logger, layerId, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the OData request", 500);
        }
    }

    /// <summary>
    /// Handles getting a single feature by ID
    /// </summary>
    private static async Task<IResult> HandleGetSingleFeature(
        HttpContext context,
        int layerId,
        long objectId,
        ODataCrudService crudService,
        ODataValidationService validationService,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

        var result = await crudService.GetFeatureAsync(layerId, objectId, baseUrl, effectiveToken);
        return ODataUtilityService.CreateResultFromCrudResult(context, result);
    }

    /// <summary>
    /// Handles creating a new feature
    /// </summary>
    private static async Task<IResult> HandleCreateFeature(
        HttpContext context,
        int layerId,
        ODataCrudService crudService,
        ODataValidationService validationService,
        [FromBody] ODataFeatureRequest request,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var (isValid, errorMessage) = ODataUtilityService.ValidateFeatureRequest(request, "POST");
        if (!isValid)
        {
            return ODataUtilityService.CreateODataError(context, "InvalidRequest", errorMessage!);
        }

        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

        var result = await crudService.CreateFeatureAsync(layerId, request, baseUrl, effectiveToken);
        return ODataUtilityService.CreateResultFromCrudResult(context, result);
    }

    /// <summary>
    /// Handles updating an existing feature
    /// </summary>
    private static async Task<IResult> HandleUpdateFeature(
        HttpContext context,
        int layerId,
        long objectId,
        ODataCrudService crudService,
        ODataValidationService validationService,
        [FromBody] ODataFeatureRequest request,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var (isValid, errorMessage) = ODataUtilityService.ValidateFeatureRequest(request, "PATCH");
        if (!isValid)
        {
            return ODataUtilityService.CreateODataError(context, "InvalidRequest", errorMessage!);
        }

        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

        var result = await crudService.UpdateFeatureAsync(layerId, objectId, request, baseUrl, effectiveToken);
        return ODataUtilityService.CreateResultFromCrudResult(context, result);
    }

    /// <summary>
    /// Handles deleting a feature
    /// </summary>
    private static async Task<IResult> HandleDeleteFeature(
        HttpContext context,
        int layerId,
        long objectId,
        ODataCrudService crudService,
        ODataValidationService validationService,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var result = await crudService.DeleteFeatureAsync(layerId, objectId, effectiveToken);
        return ODataUtilityService.CreateResultFromCrudResult(context, result);
    }

    /// <summary>
    /// Handles OData $batch request for executing multiple operations.
    /// </summary>
    private static async Task<IResult> HandleBatch(
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ODataValidationService validationService,
        IOptions<LimitsOptions> limitsOptions,
        ILogger<ODataEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.None);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

            // Read and parse the batch request
            ODataBatchRequest? batchRequest;
            try
            {
                batchRequest = await context.Request.ReadFromJsonAsync<ODataBatchRequest>(
                    ODataJsonContext.Default.ODataBatchRequest, effectiveToken);
            }
            catch (System.Text.Json.JsonException ex)
            {
                Log.BatchParseFailed(logger, ex);
                return ODataUtilityService.CreateODataError(context, "InvalidRequest", "Failed to parse batch request body.");
            }

            if (batchRequest == null || batchRequest.Requests.IsDefaultOrEmpty)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidRequest",
                    "Batch request must contain at least one request.");
            }

            // Process the batch
            var handler = new ODataBatchHandler(layerCatalog, featureStore, limitsOptions.Value.Edits, logger);
            var response = await handler.ProcessBatchAsync(batchRequest, baseUrl, effectiveToken);

            ODataUtilityService.SetODataHeaders(context);
            return Results.Json(response, ODataJsonContext.Default.ODataBatchResponse,
                contentType: ODataUtilityService.GetODataContentType());
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.BatchFailed(logger, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the batch request", 500);
        }
    }

    /// <summary>
    /// Handles OData $apply aggregation request.
    /// </summary>
    private static async Task<IResult> HandleApply(
        HttpContext context,
        int layerId,
        ODataQuerySearchService querySearchService,
        ODataValidationService validationService,
        ILogger<ODataEndpointsLog> logger,
        [FromQuery(Name = "$apply")] string? apply = null,
        [FromQuery(Name = "$filter")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.Apply);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            if (string.IsNullOrWhiteSpace(apply))
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", "$apply parameter is required.");
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

            var result = await querySearchService.HandleApplyAsync(layerId, apply, filter, baseUrl, effectiveToken);

            ODataUtilityService.SetODataHeaders(context);
            return Results.Json(result, ODataJsonContext.Default.ODataAggregationResult,
                contentType: ODataUtilityService.GetODataContentType());
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (Honua.Core.Exceptions.ResourceNotFoundException ex)
        {
            Log.InvalidApplyExpression(logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "ResourceNotFound", safeDetail, 404);
        }
        catch (ArgumentException ex)
        {
            Log.InvalidApplyExpression(logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", safeDetail);
        }
        catch (Exception ex)
        {
            Log.ApplyFailed(logger, layerId, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the aggregation request", 500);
        }
    }

    /// <summary>
    /// Handles OData $search full-text search request.
    /// </summary>
    private static async Task<IResult> HandleSearch(
        HttpContext context,
        int layerId,
        ODataQuerySearchService querySearchService,
        ODataValidationService validationService,
        ILogger<ODataEndpointsLog> logger,
        [FromQuery(Name = "$search")] string? search = null,
        [FromQuery(Name = "$top")] int? top = null,
        [FromQuery(Name = "$skip")] int? skip = null,
        [FromQuery(Name = "$count")] bool? count = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ValidateAllowedParameters(context, validationService, AllowedQueryParameters.Search);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            if (string.IsNullOrWhiteSpace(search))
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", "$search parameter is required.");
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

            var result = await querySearchService.HandleSearchAsync(layerId, search, baseUrl, top, skip, count, effectiveToken);

            ODataUtilityService.SetODataHeaders(context);
            return Results.Json(result, ODataJsonContext.Default.ODataSearchResult,
                contentType: ODataUtilityService.GetODataContentType());
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (Honua.Core.Exceptions.ResourceNotFoundException ex)
        {
            Log.SearchFailed(logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "ResourceNotFound", safeDetail, 404);
        }
        catch (ArgumentException ex)
        {
            Log.SearchFailed(logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", safeDetail);
        }
        catch (Exception ex)
        {
            Log.SearchFailed(logger, layerId, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the search request", 500);
        }
    }

    private static IResult? ValidateAllowedParameters(
        HttpContext context,
        ODataValidationService validationService,
        IReadOnlySet<string> allowedParameters)
    {
        var validationResult = validationService.ValidateAllowedParameters(context.Request.Query, allowedParameters);
        if (!validationResult.IsValid)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "InvalidQueryOption",
                validationResult.ErrorMessage ?? "Invalid query parameter.");
        }

        return null;
    }

    private static class AllowedQueryParameters
    {
        public static readonly FrozenSet<string> None =
            Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Layers = new[]
            {
                "$filter",
                "$select",
                "$top",
                "$skip",
                "$count"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Features = new[]
            {
                "$filter",
                "$select",
                "$orderby",
                "$top",
                "$skip",
                "$count",
                "$expand"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Apply = new[]
            {
                "$apply",
                "$filter"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Search = new[]
            {
                "$search",
                "$top",
                "$skip",
                "$count"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Serializes feature attributes to JSON string format.
    /// TODO: Move this to a utility service when available
    /// </summary>
    private static string SerializeAttributes(IReadOnlyDictionary<string, object?> attributes)
    {
        var normalized = attributes.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        return System.Text.Json.JsonSerializer.Serialize(normalized, ODataJsonContext.Default.DictionaryStringObject);
    }

    /// <summary>
    /// Normalizes values for OData serialization.
    /// TODO: Move this to a utility service when available
    /// </summary>
    private static object? NormalizeODataValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is System.Text.Json.JsonElement element)
        {
            return ConvertJsonElement(element);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            return readOnlyDict.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        }

        if (value is IDictionary<string, object?> dict)
        {
            return dict.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeODataValue(item));
            }
            return list.ToArray();
        }

        return value;
    }

    /// <summary>
    /// Converts JsonElement to appropriate .NET type.
    /// TODO: Move this to a utility service when available
    /// </summary>
    private static object? ConvertJsonElement(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal :
                element.TryGetDouble(out var doubleVal) ? doubleVal :
                element.GetDecimal(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(prop => prop.Name, prop => ConvertJsonElement(prop.Value)),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            _ => element.GetRawText()
        };
    }

    /// <summary>
    /// Logging methods for OData operations.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Invalid OData layers query.")]
        public static partial void InvalidLayersQuery(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "OData layers query failed.")]
        public static partial void LayersQueryFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Invalid OData features query for layer {LayerId}.")]
        public static partial void InvalidFeaturesQuery(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(EventId = 3004, Level = LogLevel.Error, Message = "OData features query failed for layer {LayerId}.")]
        public static partial void FeaturesQueryFailed(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(EventId = 3010, Level = LogLevel.Warning, Message = "OData batch request parse failed.")]
        public static partial void BatchParseFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3011, Level = LogLevel.Error, Message = "OData batch request failed.")]
        public static partial void BatchFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3012, Level = LogLevel.Warning, Message = "Invalid OData $apply expression for layer {LayerId}.")]
        public static partial void InvalidApplyExpression(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(EventId = 3013, Level = LogLevel.Error, Message = "OData $apply aggregation failed for layer {LayerId}.")]
        public static partial void ApplyFailed(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(EventId = 3014, Level = LogLevel.Error, Message = "OData $search failed for layer {LayerId}.")]
        public static partial void SearchFailed(ILogger logger, int layerId, Exception exception);
    }

    // We need to inject a logger for this class
    private static readonly ILogger _logger =
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}
