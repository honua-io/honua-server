// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OData;

/// <summary>
/// Handler for OData advanced query operations including $apply aggregation and $search full-text search.
/// Provides complex analytical and search operations with proper validation and error handling.
/// </summary>
internal sealed partial class ODataAdvancedQueryHandler(
    ODataQuerySearchService querySearchService,
    ODataValidationService validationService,
    ILogger<ODataAdvancedQueryHandler> logger)
{
    private readonly ODataQuerySearchService _querySearchService = querySearchService ?? throw new ArgumentNullException(nameof(querySearchService));
    private readonly ODataValidationService _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
    private readonly ILogger<ODataAdvancedQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles OData $apply aggregation request.
    /// </summary>
    public async Task<IResult> HandleApplyAsync(
        HttpContext context,
        int layerId,
        [FromQuery(Name = "$apply")] string? apply = null,
        [FromQuery(Name = "$filter")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ODataLog.ApplyRequested(_logger, layerId);

            var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
                context,
                _validationService,
                AllowedQueryParameters.Apply);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            if (string.IsNullOrWhiteSpace(apply))
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", "$apply parameter is required.");
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
                context,
                layerId,
                LayerValidationHelpers.ValidationProtocol.OData,
                cancellationToken: effectiveToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

            var result = await _querySearchService.HandleApplyAsync(layerId, apply, filter, baseUrl, effectiveToken);

            ODataUtilityService.SetODataHeaders(context);
            return Results.Json(result, ODataJsonContext.Default.ODataAggregationResult,
                contentType: ODataUtilityService.GetODataContentType(context.Request, format: null));
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (Honua.Core.Exceptions.ResourceNotFoundException ex)
        {
            Log.InvalidApplyExpression(_logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "ResourceNotFound", safeDetail, 404);
        }
        catch (ArgumentException ex)
        {
            Log.InvalidApplyExpression(_logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", safeDetail);
        }
        catch (Exception ex)
        {
            Log.ApplyFailed(_logger, layerId, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the aggregation request", 500);
        }
    }

    /// <summary>
    /// Handles OData $search full-text search request.
    /// </summary>
    public async Task<IResult> HandleSearchAsync(
        HttpContext context,
        int layerId,
        [FromQuery(Name = "$search")] string? search = null,
        [FromQuery(Name = "$top")] string? top = null,
        [FromQuery(Name = "$skip")] string? skip = null,
        [FromQuery(Name = "$count")] string? count = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ODataLog.SearchRequested(_logger, layerId);

            var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
                context,
                _validationService,
                AllowedQueryParameters.Search);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            if (string.IsNullOrWhiteSpace(search))
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", "$search parameter is required.");
            }

            var pagingError = ODataRequestValidation.TryGetPagingValues(
                context,
                _validationService,
                top,
                skip,
                skiptoken: null,
                count,
                out var pagination,
                out _,
                out _,
                out var countValue,
                out _);
            if (pagingError != null)
            {
                return pagingError;
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
                context,
                layerId,
                LayerValidationHelpers.ValidationProtocol.OData,
                cancellationToken: effectiveToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

            var result = await _querySearchService.HandleSearchAsync(layerId, search, baseUrl, pagination.Limit, pagination.Offset, countValue, effectiveToken);

            ODataUtilityService.SetODataHeaders(context);
            return Results.Json(result, ODataJsonContext.Default.ODataSearchResult,
                contentType: ODataUtilityService.GetODataContentType(context.Request, format: null));
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (Honua.Core.Exceptions.ResourceNotFoundException ex)
        {
            Log.SearchFailed(_logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "ResourceNotFound", safeDetail, 404);
        }
        catch (ArgumentException ex)
        {
            Log.SearchFailed(_logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", safeDetail);
        }
        catch (Exception ex)
        {
            Log.SearchFailed(_logger, layerId, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the search request", 500);
        }
    }

    /// <summary>
    /// Logging methods for OData advanced query operations.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 3017, Level = LogLevel.Warning, Message = "Invalid OData $apply expression for layer {LayerId}.")]
        public static partial void InvalidApplyExpression(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(EventId = 3018, Level = LogLevel.Error, Message = "OData $apply aggregation failed for layer {LayerId}.")]
        public static partial void ApplyFailed(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(EventId = 3019, Level = LogLevel.Error, Message = "OData $search failed for layer {LayerId}.")]
        public static partial void SearchFailed(ILogger logger, int layerId, Exception exception);
    }
}
