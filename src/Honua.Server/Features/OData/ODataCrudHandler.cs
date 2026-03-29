// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Honua.ServiceDefaults;
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
    IFeatureChangeEventPublisher featureChangeEventPublisher)
{
    private readonly ODataCrudService _crudService = crudService ?? throw new ArgumentNullException(nameof(crudService));
    private readonly ODataValidationService _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
    private readonly Honua.Server.Features.Infrastructure.Caching.IETagService _etagService = etagService ?? throw new ArgumentNullException(nameof(etagService));
    private readonly IFeatureChangeEventPublisher _featureChangeEventPublisher = featureChangeEventPublisher ?? throw new ArgumentNullException(nameof(featureChangeEventPublisher));

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
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
            context,
            _validationService,
            AllowedQueryParameters.Feature);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        if (ODataParsingUtilities.HasEmptyCommaSeparatedToken(select))
        {
            return ODataUtilityService.CreateODataError(
                context,
                "InvalidQueryOption",
                "$select contains an empty field expression.");
        }

        var formatValidation = ODataRequestValidation.ValidateFormat(context, _validationService, format);
        if (formatValidation != null)
        {
            return formatValidation;
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context, layerId, LayerValidationHelpers.ValidationProtocol.OData, cancellationToken: effectiveToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.FeatureQuery, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "query");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

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

            if (ODataUtilityService.ShouldIncludeContext(context.Request, format))
            {
                payload["@odata.context"] = ODataUtilityService.BuildContextUrl(
                    baseUrl,
                    "Features",
                    isSingle: true,
                    select: select);
            }

            HonuaTelemetry.SetSuccess(activity);
        }

        return ODataUtilityService.CreateResultFromCrudResult(context, result, format);
    }

    /// <summary>
    /// Handles getting a canonical OData entity reference for a feature.
    /// </summary>
    public async Task<IResult> HandleGetFeatureReferenceAsync(
        HttpContext context,
        int layerId,
        long objectId,
        CancellationToken cancellationToken = default)
    {
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
            context,
            _validationService,
            AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context, layerId, LayerValidationHelpers.ValidationProtocol.OData, cancellationToken: effectiveToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var result = await _crudService.GetFeatureAsync(layerId, objectId, baseUrl, effectiveToken);
        if (!result.IsSuccess)
        {
            return ODataUtilityService.CreateResultFromCrudResult(context, result);
        }

        var reference = new Dictionary<string, object?>
        {
            ["@odata.id"] = ODataUtilityService.CreateLocationHeader(baseUrl, layerId, objectId)
        };

        ODataUtilityService.SetODataHeaders(context);
        return Results.Json(
            reference,
            ODataJsonContext.Default.DictionaryStringObject,
            contentType: ODataUtilityService.GetODataContentType(context.Request, format: null));
    }

    /// <summary>
    /// Handles getting the raw value representation for a feature.
    /// </summary>
    public async Task<IResult> HandleGetFeatureValueAsync(
        HttpContext context,
        int layerId,
        long objectId,
        [FromQuery(Name = "$format")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
            context,
            _validationService,
            AllowedQueryParameters.FeatureValue);
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
            context, layerId, LayerValidationHelpers.ValidationProtocol.OData, cancellationToken: effectiveToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var result = await _crudService.GetFeatureAsync(layerId, objectId, baseUrl, effectiveToken);
        if (!result.IsSuccess)
        {
            return ODataUtilityService.CreateResultFromCrudResult(context, result, format);
        }

        ODataUtilityService.SetODataHeaders(context, result.ETag);
        return Results.Json(
            result.Data,
            ODataJsonContext.Default.DictionaryStringObject,
            contentType: ODataUtilityService.GetODataContentType(context.Request, format));
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
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

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

        var layerValidation = await LayerValidationHelpers.ValidateODataWriteAccessAsync(
            context, resolvedLayerId.Value, effectiveToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "create");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, resolvedLayerId.Value);

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

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

                if (ODataUtilityService.ShouldIncludeContext(context.Request, format: null))
                {
                    createdPayload["@odata.context"] = ODataUtilityService.BuildContextUrl(
                        baseUrl,
                        "Features",
                        isSingle: true);
                }
            }

            if (preferMinimal)
            {
                context.Response.Headers["Preference-Applied"] = "return=minimal";
                result = result with { StatusCode = StatusCodes.Status204NoContent, Data = null };
            }

            await InvalidateCacheAsync(context, resolvedLayerId.Value, effectiveToken);
            if (TryExtractObjectId(result.Data, out var createdObjectId))
            {
                var serviceId = await ResolveServiceIdAsync(context, resolvedLayerId.Value, effectiveToken);
                await _featureChangeEventPublisher.PublishAsync(
                    new FeatureChangeEventRequest
                    {
                        ServiceId = serviceId,
                        LayerId = resolvedLayerId.Value,
                        ObjectId = createdObjectId,
                        Operation = "create",
                        Protocol = HonuaTelemetry.Protocols.OData,
                        RequestId = context.TraceIdentifier
                    },
                    effectiveToken).ConfigureAwait(false);
            }
            HonuaTelemetry.SetSuccess(activity);
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
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

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

        var layerValidation = await LayerValidationHelpers.ValidateODataWriteAccessAsync(
            context, layerId, effectiveToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "update");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

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

                if (ODataUtilityService.ShouldIncludeContext(context.Request, format: null))
                {
                    updatedPayload["@odata.context"] = ODataUtilityService.BuildContextUrl(
                        baseUrl,
                        "Features",
                        isSingle: true);
                }
            }

            if (preferMinimal)
            {
                context.Response.Headers["Preference-Applied"] = "return=minimal";
                result = result with { StatusCode = StatusCodes.Status204NoContent, Data = null };
            }

            await InvalidateCacheAsync(context, layerId, effectiveToken);
            var serviceId = await ResolveServiceIdAsync(context, layerId, effectiveToken);
            await _featureChangeEventPublisher.PublishAsync(
                new FeatureChangeEventRequest
                {
                    ServiceId = serviceId,
                    LayerId = layerId,
                    ObjectId = objectId,
                    Operation = "update",
                    Protocol = HonuaTelemetry.Protocols.OData,
                    RequestId = context.TraceIdentifier
                },
                effectiveToken).ConfigureAwait(false);
            HonuaTelemetry.SetSuccess(activity);
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
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
            context,
            _validationService,
            AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var layerValidation = await LayerValidationHelpers.ValidateODataWriteAccessAsync(
            context, layerId, effectiveToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "delete");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var ifMatch = context.Request.Headers.IfMatch.ToString();
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        var result = await _crudService.DeleteFeatureAsync(layerId, objectId, ifMatch, ifNoneMatch, effectiveToken);
        if (result.IsSuccess)
        {
            await InvalidateCacheAsync(context, layerId, effectiveToken);
            var serviceId = await ResolveServiceIdAsync(context, layerId, effectiveToken);
            await _featureChangeEventPublisher.PublishAsync(
                new FeatureChangeEventRequest
                {
                    ServiceId = serviceId,
                    LayerId = layerId,
                    ObjectId = objectId,
                    Operation = "delete",
                    Protocol = HonuaTelemetry.Protocols.OData,
                    RequestId = context.TraceIdentifier
                },
                effectiveToken).ConfigureAwait(false);
            HonuaTelemetry.SetSuccess(activity);
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

    private static async Task<string> ResolveServiceIdAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        var serviceId = await LayerValidationHelpers.ResolvePrimaryServiceNameAsync(
            context,
            layerId,
            ServiceProtocols.OData,
            cancellationToken);

        return serviceId ?? layerId.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryExtractObjectId(object? payload, out long objectId)
    {
        objectId = 0;
        if (payload is not Dictionary<string, object?> dictionary)
        {
            return false;
        }

        if (!dictionary.TryGetValue("ObjectId", out var value) ||
            value == null)
        {
            return false;
        }

        return value switch
        {
            long l => (objectId = l) >= 0,
            int i => (objectId = i) >= 0,
            string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out objectId),
            _ => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out objectId)
        };
    }

}
