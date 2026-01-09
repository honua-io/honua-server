// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OData;

/// <summary>
/// Handler for OData CRUD operations (Create, Read, Update, Delete) on features.
/// Provides feature manipulation with validation, caching invalidation, and proper error handling.
/// </summary>
internal sealed class ODataCrudHandler(
    ODataCrudService crudService,
    ODataValidationService validationService,
    ILogger<ODataCrudHandler> logger)
{
    private readonly ODataCrudService _crudService = crudService ?? throw new ArgumentNullException(nameof(crudService));
    private readonly ODataValidationService _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
    private readonly ILogger<ODataCrudHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles getting a single feature by ID
    /// </summary>
    public async Task<IResult> HandleGetSingleFeatureAsync(
        HttpContext context,
        int layerId,
        long objectId,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ValidateAllowedParameters(context, AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context, layerId, LayerValidationHelpers.ValidationProtocol.OData, cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var result = await _crudService.GetFeatureAsync(layerId, objectId, baseUrl, effectiveToken);
        return ODataUtilityService.CreateResultFromCrudResult(context, result);
    }

    /// <summary>
    /// Handles creating a new feature
    /// </summary>
    public async Task<IResult> HandleCreateFeatureAsync(
        HttpContext context,
        int layerId,
        [FromBody] ODataFeatureRequest request,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ValidateAllowedParameters(context, AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var (isValid, errorMessage) = ODataUtilityService.ValidateFeatureRequest(request, "POST");
        if (!isValid)
        {
            return ODataUtilityService.CreateODataError(context, "InvalidRequest", errorMessage!);
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context, layerId, LayerValidationHelpers.ValidationProtocol.OData, cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var result = await _crudService.CreateFeatureAsync(layerId, request, baseUrl, effectiveToken);
        if (result.IsSuccess)
        {
            await InvalidateCacheAsync(context, layerId, effectiveToken);
        }

        return ODataUtilityService.CreateResultFromCrudResult(context, result);
    }

    /// <summary>
    /// Handles updating an existing feature
    /// </summary>
    public async Task<IResult> HandleUpdateFeatureAsync(
        HttpContext context,
        int layerId,
        long objectId,
        [FromBody] ODataFeatureRequest request,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ValidateAllowedParameters(context, AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var (isValid, errorMessage) = ODataUtilityService.ValidateFeatureRequest(request, "PATCH");
        if (!isValid)
        {
            return ODataUtilityService.CreateODataError(context, "InvalidRequest", errorMessage!);
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context, layerId, LayerValidationHelpers.ValidationProtocol.OData, cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var result = await _crudService.UpdateFeatureAsync(layerId, objectId, request, baseUrl, effectiveToken);
        if (result.IsSuccess)
        {
            await InvalidateCacheAsync(context, layerId, effectiveToken);
        }

        return ODataUtilityService.CreateResultFromCrudResult(context, result);
    }

    /// <summary>
    /// Handles deleting a feature
    /// </summary>
    public async Task<IResult> HandleDeleteFeatureAsync(
        HttpContext context,
        int layerId,
        long objectId,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ValidateAllowedParameters(context, AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context, layerId, LayerValidationHelpers.ValidationProtocol.OData, cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var result = await _crudService.DeleteFeatureAsync(layerId, objectId, effectiveToken);
        if (result.IsSuccess)
        {
            await InvalidateCacheAsync(context, layerId, effectiveToken);
        }

        return ODataUtilityService.CreateResultFromCrudResult(context, result);
    }

    /// <summary>
    /// Invalidates cache entries for the modified layer
    /// </summary>
    private static async Task InvalidateCacheAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (cacheInvalidator != null)
        {
            await cacheInvalidator.InvalidateLayerAsync(null, layerId, cancellationToken);
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
}
