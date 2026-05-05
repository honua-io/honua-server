// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.Server.Features.Protocols.OData.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.OData;

/// <summary>
/// Handler for OData CRUD operations (Create, Read, Update, Delete) on features.
/// Provides feature manipulation with validation, caching invalidation, and proper error handling.
/// </summary>
internal sealed class ODataCrudHandler(
    ODataCrudService crudService,
    ODataValidationService validationService,
    Honua.Server.Features.Infrastructure.Caching.IETagService etagService,
    FeatureMutationEventService mutationEventService)
{
    private static readonly HashSet<string> FeatureRequestContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json"
    };

    private readonly ODataCrudService _crudService = crudService ?? throw new ArgumentNullException(nameof(crudService));
    private readonly ODataValidationService _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
    private readonly Honua.Server.Features.Infrastructure.Caching.IETagService _etagService = etagService ?? throw new ArgumentNullException(nameof(etagService));
    private readonly FeatureMutationEventService _mutationEventService = mutationEventService ?? throw new ArgumentNullException(nameof(mutationEventService));

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

        // Emit both @odata.id and @odata.editLink so clients can auto-discover the
        // canonical URL for PATCH/PUT/DELETE per OData-JSON-Format §3.1.2. The two
        // links coincide for a standalone entity reference, but absent editLink
        // compliant clients skip optimistic-write paths.
        var selfLink = ODataUtilityService.CreateLocationHeader(baseUrl, layerId, objectId);
        var reference = new Dictionary<string, object?>
        {
            ["@odata.id"] = selfLink,
            ["@odata.editLink"] = selfLink
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

        return ODataUtilityService.CreateODataError(
            context,
            "ResourceNotFound",
            "The $value path is only supported for primitive properties or media streams.",
            StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Handles creating a new feature
    /// </summary>
    public async Task<IResult> HandleCreateFeatureAsync(
        HttpContext context,
        int? layerId,
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

        if (layerId.HasValue)
        {
            var layerValidation = await LayerValidationHelpers.ValidateODataWriteAccessAsync(
                context, layerId.Value, effectiveToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
        }
        else
        {
            var authorizationError = await RequireAnyODataWriteAccessBeforeBodyAsync(context, effectiveToken);
            if (authorizationError != null)
            {
                return authorizationError;
            }
        }

        var (request, readError) = await ReadFeatureRequestAsync(context, effectiveToken);
        return readError ?? await HandleCreateFeatureAsync(context, layerId, request!, cancellationToken);
    }

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

        var createOutboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            context,
            resolvedLayerId.Value,
            HonuaTelemetry.Protocols.OData,
            serviceProtocol: ServiceProtocols.OData,
            // GeometrySpecified tracks whether the request body explicitly carried a
            // Geometry property; without this hint the outbox would default to false
            // even when the create request set geometry. The inline publish path on
            // non-outbox deployments doesn't pass geometryChanged for OData CRUD
            // either, so this also closes that legacy gap on the dispatcher side.
            geometryChanged: payload.GeometrySpecified,
            cancellationToken: effectiveToken).ConfigureAwait(false);
        ODataCrudResult<Dictionary<string, object?>> result;
        using (Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(createOutboxScopeData))
        {
            result = await _crudService.CreateFeatureAsync(resolvedLayerId.Value, payload, baseUrl, effectiveToken);
        }
        if (result.IsSuccess)
        {
            var preferMinimal = ODataUtilityService.ShouldReturnMinimal(context.Request.Headers["Prefer"].ToString());
            var hasCreatedObjectId = TryExtractObjectId(result.Data, out var createdObjectId);

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

            if (!ODataBatchContext.ShouldSuppressMutationSideEffects(context))
            {
                await _mutationEventService.InvalidateLayerAsync(null, resolvedLayerId.Value, CancellationToken.None);
                if (hasCreatedObjectId)
                {
                    await _mutationEventService.PublishAsync(
                        context,
                        resolvedLayerId.Value,
                        createdObjectId,
                        "create",
                        HonuaTelemetry.Protocols.OData,
                        CancellationToken.None,
                        mutationFeature: result.MutationFeature,
                        serviceProtocol: ServiceProtocols.OData).ConfigureAwait(false);
                }
            }
            HonuaTelemetry.SetSuccess(activity);
        }
        else
        {
            RecordCrudFailure(activity, result.ErrorMessage);
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

        var (request, readError) = await ReadFeatureRequestAsync(context, effectiveToken);
        return readError ?? await HandleUpdateFeatureAsync(context, layerId, objectId, request!, cancellationToken);
    }

    public async Task<IResult> HandleUpdateFeatureAsync(
        HttpContext context,
        int layerId,
        long objectId,
        [FromBody] ODataFeatureRequest request,
        CancellationToken cancellationToken = default)
        => await HandleUpdateFeatureAsync(
            context,
            layerId,
            objectId,
            request,
            replace: false,
            cancellationToken).ConfigureAwait(false);

    public async Task<IResult> HandleReplaceFeatureAsync(
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

        var (request, readError) = await ReadFeatureRequestAsync(context, effectiveToken);
        return readError ?? await HandleReplaceFeatureAsync(context, layerId, objectId, request!, cancellationToken);
    }

    public async Task<IResult> HandleReplaceFeatureAsync(
        HttpContext context,
        int layerId,
        long objectId,
        [FromBody] ODataFeatureRequest request,
        CancellationToken cancellationToken = default)
        => await HandleUpdateFeatureAsync(
            context,
            layerId,
            objectId,
            request,
            replace: true,
            cancellationToken).ConfigureAwait(false);

    private async Task<IResult> HandleUpdateFeatureAsync(
        HttpContext context,
        int layerId,
        long objectId,
        ODataFeatureRequest request,
        bool replace,
        CancellationToken cancellationToken)
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

        var method = replace ? "PUT" : "PATCH";
        var (isValid, errorMessage) = ODataUtilityService.ValidateFeatureRequest(request, method);
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
        activity?.SetTag(HonuaTelemetry.Tags.Operation, replace ? "replace" : "update");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

        var ifMatch = context.Request.Headers.IfMatch.ToString();
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        // GeometrySpecified is the OData PATCH/PUT contract for "the request body
        // explicitly carried a Geometry property"; relying on the post-mutation
        // snapshot would over-report PATCH attribute-only updates as geometry
        // changes because the merged feature still carries the prior WKB.
        var updateOutboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            context,
            layerId,
            HonuaTelemetry.Protocols.OData,
            serviceProtocol: ServiceProtocols.OData,
            geometryChanged: payload.GeometrySpecified,
            cancellationToken: effectiveToken).ConfigureAwait(false);
        ODataCrudResult<Dictionary<string, object?>> result;
        using (Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(updateOutboxScopeData))
        {
            result = await _crudService.UpdateFeatureAsync(
                layerId,
                objectId,
                payload,
                baseUrl,
                ifMatch,
                ifNoneMatch,
                replace,
                effectiveToken);
        }
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

            if (!ODataBatchContext.ShouldSuppressMutationSideEffects(context))
            {
                await _mutationEventService.InvalidateLayerAsync(null, layerId, CancellationToken.None);
                await _mutationEventService.PublishAsync(
                    context,
                    layerId,
                    objectId,
                    replace ? "replace" : "update",
                    HonuaTelemetry.Protocols.OData,
                    CancellationToken.None,
                    mutationFeature: result.MutationFeature,
                    serviceProtocol: ServiceProtocols.OData).ConfigureAwait(false);
            }
            HonuaTelemetry.SetSuccess(activity);
        }
        else
        {
            RecordCrudFailure(activity, result.ErrorMessage);
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
        var deleteOutboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            context,
            layerId,
            HonuaTelemetry.Protocols.OData,
            serviceProtocol: ServiceProtocols.OData,
            cancellationToken: effectiveToken).ConfigureAwait(false);
        ODataCrudResult<object> result;
        using (Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(deleteOutboxScopeData))
        {
            result = await _crudService.DeleteFeatureAsync(layerId, objectId, ifMatch, ifNoneMatch, effectiveToken);
        }
        if (result.IsSuccess)
        {
            if (!ODataBatchContext.ShouldSuppressMutationSideEffects(context))
            {
                await _mutationEventService.InvalidateLayerAsync(null, layerId, CancellationToken.None);
                await _mutationEventService.PublishAsync(
                    context,
                    layerId,
                    objectId,
                    "delete",
                    HonuaTelemetry.Protocols.OData,
                    CancellationToken.None,
                    mutationFeature: result.MutationFeature,
                    serviceProtocol: ServiceProtocols.OData).ConfigureAwait(false);
            }
            HonuaTelemetry.SetSuccess(activity);
        }
        else
        {
            RecordCrudFailure(activity, result.ErrorMessage);
        }

        return ODataUtilityService.CreateResultFromCrudResult(context, result);
    }

    private static async Task<(ODataFeatureRequest? Request, IResult? Error)> ReadFeatureRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var contentTypeError = ValidateFeatureRequestContentType(context);
            if (contentTypeError is not null)
            {
                return (null, contentTypeError);
            }

            var request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                ODataJsonContext.Default.ODataFeatureRequest,
                cancellationToken);
            if (request == null)
            {
                return (null, ODataUtilityService.CreateODataError(
                    context,
                    "InvalidRequest",
                    "Invalid request body."));
            }

            return (request, null);
        }
        catch (JsonException)
        {
            return (null, ODataUtilityService.CreateODataError(
                context,
                "InvalidRequest",
                "Request body must be valid JSON."));
        }
        catch (NotSupportedException)
        {
            return (null, ODataUtilityService.CreateODataError(
                context,
                "InvalidRequest",
                "Request body must be valid JSON."));
        }
    }

    private static IResult? ValidateFeatureRequestContentType(HttpContext context)
    {
        if (context.Request.ContentLength is 0)
        {
            return null;
        }

        var contentType = context.Request.ContentType;
        var mediaType = string.IsNullOrWhiteSpace(contentType)
            ? null
            : contentType.Split(';', 2)[0].Trim();

        if (!string.IsNullOrWhiteSpace(mediaType) &&
            FeatureRequestContentTypes.Contains(mediaType))
        {
            return null;
        }

        return ValidationErrorHelpers.CreateUnsupportedMediaType(
            context,
            string.IsNullOrWhiteSpace(mediaType) ? "(missing)" : mediaType,
            FeatureRequestContentTypes);
    }

    private static async Task<IResult?> RequireAnyODataWriteAccessBeforeBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var layerCatalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var services = await layerCatalog.ListServicesAsync(cancellationToken);
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMap(services, ServiceProtocols.OData);
        IResult? firstError = null;

        foreach (var layer in services
                     .SelectMany(static service => service.Layers)
                     .DistinctBy(static layer => layer.Id)
                     .OrderBy(static layer => layer.Id))
        {
            if (!primaryServices.TryGetValue(layer.Id, out var service) ||
                !ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.OData))
            {
                continue;
            }

            var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
                context,
                layer,
                service,
                cancellationToken);
            if (rbacError == null)
            {
                return null;
            }

            firstError ??= rbacError;
        }

        return firstError ?? ODataUtilityService.CreateODataError(
            context,
            "ResourceNotFound",
            "No OData collections are available.",
            StatusCodes.Status404NotFound);
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

    private static void RecordCrudFailure(Activity? activity, string? message)
    {
        var detail = string.IsNullOrWhiteSpace(message)
            ? "OData write request failed."
            : message;
        HonuaTelemetry.RecordException(activity, new InvalidOperationException(detail));
    }
}
