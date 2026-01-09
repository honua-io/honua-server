// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OData;

/// <summary>
/// Handler for OData query operations on layers and features.
/// Handles standard OData query parameters ($filter, $select, $orderby, $top, $skip, $count, $expand).
/// </summary>
internal sealed partial class ODataQueryHandler(
    ODataQueryDependencies dependencies,
    ILogger<ODataQueryHandler> logger)
{
    private readonly ILayerCatalog _layerCatalog = dependencies?.LayerCatalog
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IResourceValidator _resourceValidator = dependencies.ResourceValidator;
    private readonly IFeatureReader _featureReader = dependencies.FeatureReader;
    private readonly ODataValidationService _validationService = dependencies.ValidationService;
    private readonly ODataQuerySearchService _querySearchService = dependencies.QuerySearchService;
    private readonly ILogger<ODataQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles OData layers collection request
    /// </summary>
    public async Task<IResult> HandleGetLayersAsync(
        HttpContext context,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$top")] string? top = null,
        [FromQuery(Name = "$skip")] string? skip = null,
        [FromQuery(Name = "$count")] string? count = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ValidateAllowedParameters(context, AllowedQueryParameters.Layers);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            if (!ODataParsingUtilities.TryParseOptionalInt(top, "$top", out var topValue, out var parseError))
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            }

            if (!ODataParsingUtilities.TryParseOptionalInt(skip, "$skip", out var skipValue, out parseError))
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            }

            if (!ODataParsingUtilities.TryParseOptionalBool(count, "$count", out var countValue, out parseError))
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            }

            var paginationResult = _validationService.ValidateAndNormalizePagination(skipValue, topValue);
            if (!paginationResult.IsValid)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption",
                    paginationResult.ErrorMessage ?? "Invalid OData query.");
            }

            var pagination = paginationResult.Value!;
            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var layers = await _layerCatalog.ListLayersAsync(effectiveToken);
            var visibleLayers = layers
                .Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer))
                .ToArray();

            // Apply filtering and processing
            IEnumerable<LayerDefinition> layerQuery = visibleLayers;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                layerQuery = _querySearchService.ApplyBasicFilter((IEnumerable<Core.Features.Catalog.Domain.LayerDefinition>)layerQuery, filter);
            }

            // Apply pagination and counting
            long? totalCount = null;
            if (countValue == true)
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
                : _querySearchService.ApplyFieldSelection(layerData, select);

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
    /// Handles OData features collection request with non-streaming implementation for small result sets
    /// </summary>
    public async Task<IResult> HandleGetFeaturesNonStreamingAsync(
        HttpContext context,
        int layerId,
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
            var queryValidation = ValidateAllowedParameters(context, AllowedQueryParameters.Features);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            var paginationResult = _validationService.ValidateAndNormalizePagination(skip, top);
            if (!paginationResult.IsValid)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption",
                    paginationResult.ErrorMessage ?? "Invalid OData query.");
            }

            var pagination = paginationResult.Value!;
            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

            // Verify layer exists
            var layerResult = await _resourceValidator.ValidateLayerAsync(layerId, effectiveToken);
            if (!layerResult.IsValid)
            {
                var errorMessage = layerResult.ErrorMessage ?? $"Layer {layerId} not found";
                var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier ? 400 : 404;
                var errorCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier ? "InvalidRequest" : "ResourceNotFound";
                return ODataUtilityService.CreateODataError(context, errorCode, errorMessage, statusCode);
            }

            var layer = layerResult.Resource!;
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
            if (accessError != null)
            {
                return accessError;
            }

            // Build feature query using query service
            var featureQuery = _querySearchService.BuildFeatureQuery(
                filter, orderby, pagination.Limit,
                pagination.Offset, layer, out var queryError);

            if (queryError != null)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQuery", queryError);
            }

            // Execute query
            var queryResult = await _featureReader.QueryAsync(layerId, featureQuery, effectiveToken);

            // Process $expand for related entities
            Dictionary<long, Dictionary<string, object?[]>>? expandedRelations = null;
            if (!string.IsNullOrWhiteSpace(expand) && layer.HasRelationships)
            {
                expandedRelations = await _querySearchService.ProcessExpandAsync(
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
                    ["Attributes"] = ODataAttributeSerializer.Serialize(f.Attributes)
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
                : _querySearchService.ApplyFieldSelection(featuresData, select);

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

    private IResult? ValidateAllowedParameters(
        HttpContext context,
        IReadOnlySet<string> allowedParameters)
    {
        var validationResult = _validationService.ValidateAllowedParameters(context.Request.Query.Keys.ToArray(), allowedParameters);
        if (!validationResult.IsValid)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "InvalidQueryOption",
                validationResult.ErrorMessage ?? "Invalid query parameter.");
        }

        return null;
    }


    /// <summary>
    /// Logging methods for OData query operations.
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
    }
}
