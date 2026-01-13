// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
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
    Honua.Server.Features.Infrastructure.Caching.IETagService etagService,
    ILogger<ODataCrudHandler> logger)
{
    private readonly ODataCrudService _crudService = crudService ?? throw new ArgumentNullException(nameof(crudService));
    private readonly ODataValidationService _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
    private readonly Honua.Server.Features.Infrastructure.Caching.IETagService _etagService = etagService ?? throw new ArgumentNullException(nameof(etagService));
    private readonly ILogger<ODataCrudHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles getting a single feature by ID
    /// </summary>
    public async Task<IResult> HandleGetSingleFeatureAsync(
        HttpContext context,
        int layerId,
        long objectId,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$format")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
            context,
            _validationService,
            AllowedQueryParameters.Feature);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var formatValidation = ODataRequestValidation.ValidateFormat(context, _validationService, format);
        if (formatValidation != null)
        {
            return formatValidation;
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context, layerId, LayerValidationHelpers.ValidationProtocol.OData, cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var result = await _crudService.GetFeatureAsync(layerId, objectId, baseUrl, effectiveToken);
        if (result.IsSuccess && result.Data is Dictionary<string, object?> payload)
        {
            if (!string.IsNullOrWhiteSpace(result.ETag))
            {
                var ifMatch = context.Request.Headers.IfMatch.ToString();
                if (!string.IsNullOrWhiteSpace(ifMatch) &&
                    !_etagService.MatchesPrecondition(ifMatch, result.ETag))
                {
                    return ODataUtilityService.CreateODataError(
                        context,
                        "PreconditionFailed",
                        "ETag does not match the current resource.",
                        StatusCodes.Status412PreconditionFailed);
                }

                var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
                if (!string.IsNullOrWhiteSpace(ifNoneMatch) &&
                    !_etagService.IsModified(ifNoneMatch, result.ETag))
                {
                    ODataUtilityService.SetODataHeaders(context, result.ETag);
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                payload["@odata.etag"] = result.ETag;
            }

            if (!string.IsNullOrWhiteSpace(select))
            {
                payload = ODataUtilityService.ApplySelect(payload, select);
                result = result with { Data = payload };
            }

            payload["@odata.context"] = ODataUtilityService.BuildContextUrl(
                baseUrl,
                "Features",
                isSingle: true,
                select: select);
        }

        return ODataUtilityService.CreateResultFromCrudResult(context, result);
    }

    /// <summary>
    /// Handles creating a new feature
    /// </summary>
    public async Task<IResult> HandleCreateFeatureAsync(
        HttpContext context,
        int? layerId,
        [FromBody] ODataFeatureRequest request,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
            context,
            _validationService,
            AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var (isValid, errorMessage) = ODataUtilityService.ValidateFeatureRequest(request, "POST");
        if (!isValid)
        {
            return ODataUtilityService.CreateODataError(context, "InvalidRequest", errorMessage!);
        }

        if (!ODataFeaturePayloadParser.TryParse(request, out var payload, out var payloadError))
        {
            return ODataUtilityService.CreateODataError(context, "InvalidRequest", payloadError ?? "Invalid request body.");
        }

        if (payload.LayerId.HasValue && layerId.HasValue && payload.LayerId.Value != layerId.Value)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "InvalidRequest",
                "LayerId in payload does not match route.");
        }

        var resolvedLayerId = layerId ?? payload.LayerId;
        if (!resolvedLayerId.HasValue)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "InvalidRequest",
                "LayerId is required when creating a feature.");
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context, resolvedLayerId.Value, LayerValidationHelpers.ValidationProtocol.OData, scope: AccessScope.Write, cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var result = await _crudService.CreateFeatureAsync(resolvedLayerId.Value, payload, baseUrl, effectiveToken);
        if (result.IsSuccess)
        {
            var preferMinimal = ODataUtilityService.ShouldReturnMinimal(context.Request.Headers["Prefer"].ToString());

            if (!preferMinimal && result.Data is Dictionary<string, object?> createdPayload)
            {
                if (!string.IsNullOrWhiteSpace(result.ETag))
                {
                    createdPayload["@odata.etag"] = result.ETag;
                }

                createdPayload["@odata.context"] = ODataUtilityService.BuildContextUrl(
                    baseUrl,
                    "Features",
                    isSingle: true);
            }

            if (preferMinimal)
            {
                context.Response.Headers["Preference-Applied"] = "return=minimal";
                result = result with { StatusCode = StatusCodes.Status204NoContent, Data = null };
            }

            await InvalidateCacheAsync(context, resolvedLayerId.Value, effectiveToken);
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
        var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
            context,
            _validationService,
            AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var (isValid, errorMessage) = ODataUtilityService.ValidateFeatureRequest(request, "PATCH");
        if (!isValid)
        {
            return ODataUtilityService.CreateODataError(context, "InvalidRequest", errorMessage!);
        }

        if (!ODataFeaturePayloadParser.TryParse(request, out var payload, out var payloadError))
        {
            return ODataUtilityService.CreateODataError(context, "InvalidRequest", payloadError ?? "Invalid request body.");
        }

        if (payload.LayerId.HasValue && payload.LayerId.Value != layerId)
        {
            return ODataUtilityService.CreateODataError(context, "InvalidRequest", "LayerId in payload does not match route.");
        }

        if (payload.ObjectId.HasValue && payload.ObjectId.Value != objectId)
        {
            return ODataUtilityService.CreateODataError(context, "InvalidRequest", "ObjectId in payload does not match route.");
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context, layerId, LayerValidationHelpers.ValidationProtocol.OData, scope: AccessScope.Write, cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var ifMatch = context.Request.Headers.IfMatch.ToString();
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        var result = await _crudService.UpdateFeatureAsync(
            layerId,
            objectId,
            payload,
            baseUrl,
            ifMatch,
            ifNoneMatch,
            effectiveToken);
        if (result.IsSuccess)
        {
            var preferMinimal = ODataUtilityService.ShouldReturnMinimal(context.Request.Headers["Prefer"].ToString());

            if (!preferMinimal && result.Data is Dictionary<string, object?> updatedPayload)
            {
                if (!string.IsNullOrWhiteSpace(result.ETag))
                {
                    updatedPayload["@odata.etag"] = result.ETag;
                }

                updatedPayload["@odata.context"] = ODataUtilityService.BuildContextUrl(
                    baseUrl,
                    "Features",
                    isSingle: true);
            }

            if (preferMinimal)
            {
                context.Response.Headers["Preference-Applied"] = "return=minimal";
                result = result with { StatusCode = StatusCodes.Status204NoContent, Data = null };
            }

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
        var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
            context,
            _validationService,
            AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context, layerId, LayerValidationHelpers.ValidationProtocol.OData, scope: AccessScope.Write, cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var ifMatch = context.Request.Headers.IfMatch.ToString();
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        var result = await _crudService.DeleteFeatureAsync(layerId, objectId, ifMatch, ifNoneMatch, effectiveToken);
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

}
