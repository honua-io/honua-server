// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Logger category for attachment operations
/// </summary>
internal sealed class AttachmentOperations
{
}

/// <summary>
/// Extension methods to register FeatureServer attachment endpoints
/// </summary>
internal static class AttachmentEndpoints
{
    private static readonly ISet<string> SupportedAttachmentContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-www-form-urlencoded",
        "multipart/form-data"
    };

    /// <summary>
    /// Maps FeatureServer attachment REST API endpoints using AOT-compatible routing.
    /// </summary>
    /// <remarks>
    /// Mutation endpoints use <see cref="AuthorizationEndpointConventionBuilderExtensions.AllowAnonymous"/>
    /// to bypass the application-wide authorization policy so per-layer access checks can run.
    /// Every handler invokes <c>AccessPolicyHelpers.RequireLayerAccess(... Read|Write)</c>
    /// (via <c>TryValidateLayerAccessAsync</c>) before touching storage, which is what returns
    /// 401/403 for unauthenticated or unauthorized callers. Removing <c>AllowAnonymous()</c>
    /// without first moving the per-layer gate into the pipeline would lock out every caller
    /// whose permissions live on the layer rather than on a global role.
    /// </remarks>
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Query attachments for a feature
        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryAttachments", HandleQueryAttachments)
            .WithDisplayName("Query Feature Attachments")
            .WithName("QueryAttachments")
            .WithSummary("Query attachments for a feature")
            .WithDescription("Returns all attachments for a specific feature")
            .WithTags("FeatureServer", "Attachments")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Post }));

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/{featureId:long}/attachments", HandleAttachmentInfos)
            .WithDisplayName("Get Feature Attachment Infos")
            .WithName("AttachmentInfos")
            .WithSummary("Get attachment infos for a specific feature")
            .WithDescription("Returns the canonical attachment infos resource for a feature")
            .WithTags("FeatureServer", "Attachments");

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/{featureId:long}/addAttachment", HandleAddAttachment)
            .WithDisplayName("Add Feature Attachment")
            .WithName("AddAttachment")
            .WithSummary("Add an attachment to a feature")
            .WithDescription("Upload a file attachment to a specific feature")
            .WithTags("FeatureServer", "Attachments")
            .AllowAnonymous()
            .DisableAntiforgery();

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/{featureId:long}/updateAttachment", HandleUpdateAttachment)
            .WithDisplayName("Update Feature Attachment")
            .WithName("UpdateAttachment")
            .WithSummary("Update an attachment's metadata")
            .WithDescription("Update keywords and other metadata for an existing attachment")
            .WithTags("FeatureServer", "Attachments")
            .AllowAnonymous();

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/{featureId:long}/deleteAttachments", HandleDeleteAttachments)
            .WithDisplayName("Delete Feature Attachments")
            .WithName("DeleteAttachments")
            .WithSummary("Delete attachments from a feature")
            .WithDescription("Delete one or more attachments from a specific feature")
            .WithTags("FeatureServer", "Attachments")
            .AllowAnonymous();

        // Download attachment content
        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/{featureId:long}/attachments/{attachmentId:long}", HandleDownloadAttachment)
            .WithDisplayName("Download Feature Attachment")
            .WithName("DownloadAttachment")
            .WithSummary("Download attachment content")
            .WithDescription("Download the binary content of a specific attachment")
            .WithTags("FeatureServer", "Attachments")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        return endpoints;
    }

    /// <summary>
    /// Handles querying attachments for a feature
    /// </summary>
    private static async Task HandleQueryAttachments(HttpContext context)
    {
        var resource = await TryValidateLayerAccessAsync(context);
        if (resource == null)
            return;

        var layerId = resource.Value.Layer.Id;
        var cancellationToken = context.RequestAborted;

        IReadOnlyDictionary<string, StringValues> values;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var (bodyValues, readError) = await FeatureServerEndpoints.TryReadRequestValuesAsync(context.Request, cancellationToken);
            var queryValues = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);

            if (bodyValues == null)
            {
                if (FeatureServerEndpoints.TryGetUnsupportedMediaType(readError, out var receivedContentType))
                {
                    await FeatureServerEndpoints.CreateUnsupportedRequestContentTypeResult(context, receivedContentType).ExecuteAsync(context);
                    return;
                }

                if (!string.Equals(readError, "Request body is required.", StringComparison.OrdinalIgnoreCase))
                {
                    await RouteValidationHelpers.WriteValidationErrorAsync(context, readError ?? "Invalid request body.");
                    return;
                }

                values = queryValues;
            }
            else
            {
                foreach (var pair in bodyValues)
                {
                    queryValues[pair.Key] = pair.Value;
                }

                values = queryValues;
            }
        }
        else
        {
            values = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);
        }

        if (!TryParseObjectIds(values, out var featureIds, out var objectIdsError))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, objectIdsError ?? "objectIds parameter is required");
            return;
        }

        if (!TryParseReturnUrl(values, out var returnUrl, out var returnUrlError))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, returnUrlError ?? "returnUrl must be a boolean value");
            return;
        }

        var attachmentStore = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();

        var result = await AttachmentHandler.QueryAttachmentsAsync(
            layerId,
            featureIds,
            returnUrl,
            attachmentStore,
            logger,
            context,
            cancellationToken);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles adding an attachment to a feature
    /// </summary>
    private static async Task HandleAddAttachment(HttpContext context)
    {
        var resource = await TryValidateLayerAccessAsync(context, AccessScope.Write);
        if (resource == null)
            return;

        var layerId = resource.Value.Layer.Id;
        var form = await TryReadAttachmentFormAsync(context);
        if (form == null)
        {
            return;
        }

        var objectIdValue = context.Request.Query.TryGetValue("objectId", out var queryObjectId)
            ? queryObjectId.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(objectIdValue) &&
            form.TryGetValue("objectId", out var formObjectId))
        {
            objectIdValue = formObjectId.ToString();
        }

        var routeFeatureId = TryGetRouteLongValue(context, "featureId", out var routeFeatureIdError);
        if (routeFeatureIdError != null)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, routeFeatureIdError);
            return;
        }

        long featureId;
        if (routeFeatureId.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(objectIdValue) &&
                long.TryParse(objectIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectIdFromBody) &&
                objectIdFromBody != routeFeatureId.Value)
            {
                await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId does not match featureId route parameter");
                return;
            }

            featureId = routeFeatureId.Value;
        }
        else if (!long.TryParse(objectIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId parameter is required");
            return;
        }

        if (form.Files.Count == 0)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "At least one file must be uploaded");
            return;
        }

        var file = form.Files[0];
        var keywords = form.TryGetValue("keywords", out var keywordsValue) ? keywordsValue.ToString() : null;

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var feature = await featureReader.GetAsync(layerId, featureId, context.RequestAborted);
        if (feature == null)
        {
            await StandardErrorHelpers.CreateNotFound(context, $"Feature {featureId} not found").ExecuteAsync(context);
            return;
        }

        var attachmentStore = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        var securityOptions = context.RequestServices.GetRequiredService<IOptions<FileUploadSecurityOptions>>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();

        var result = await AttachmentHandler.AddAttachmentAsync(
            context,
            layerId,
            featureId,
            file,
            keywords,
            attachmentStore,
            limitsOptions.Value.Attachments,
            securityOptions.Value,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles updating an attachment's metadata
    /// </summary>
    private static async Task HandleUpdateAttachment(HttpContext context)
    {
        var resource = await TryValidateLayerAccessAsync(context, AccessScope.Write);
        if (resource == null)
            return;

        var layerId = resource.Value.Layer.Id;
        var form = await TryReadAttachmentFormAsync(context);
        if (form == null)
        {
            return;
        }

        var routeFeatureId = TryGetRouteLongValue(context, "featureId", out var routeFeatureIdError);
        if (routeFeatureIdError != null)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, routeFeatureIdError);
            return;
        }

        var objectIdValue = form.TryGetValue("objectId", out var formObjectId)
            ? formObjectId.ToString()
            : context.Request.Query.TryGetValue("objectId", out var queryObjectId)
                ? queryObjectId.ToString()
                : null;

        long featureId;
        if (routeFeatureId.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(objectIdValue) &&
                long.TryParse(objectIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectIdFromBody) &&
                objectIdFromBody != routeFeatureId.Value)
            {
                await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId does not match featureId route parameter");
                return;
            }

            featureId = routeFeatureId.Value;
        }
        else if (!long.TryParse(objectIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId parameter is required");
            return;
        }

        var attachmentIdRaw = form.TryGetValue("attachmentId", out var attachmentIdValue)
            ? attachmentIdValue.ToString()
            : context.Request.Query.TryGetValue("attachmentId", out var queryAttachmentId)
                ? queryAttachmentId.ToString()
                : null;

        if (!long.TryParse(attachmentIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attachmentId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "attachmentId parameter is required");
            return;
        }

        var keywords = form.TryGetValue("keywords", out var keywordsValue) ? keywordsValue.ToString() : null;
        var file = form.Files.Count > 0 ? form.Files[0] : null;

        var attachmentStore = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        var securityOptions = context.RequestServices.GetRequiredService<IOptions<FileUploadSecurityOptions>>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();

        var result = await AttachmentHandler.UpdateAttachmentAsync(
            context,
            layerId,
            featureId,
            attachmentId,
            file,
            keywords,
            attachmentStore,
            limitsOptions.Value.Attachments,
            securityOptions.Value,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles deleting attachments from a feature
    /// </summary>
    private static async Task HandleDeleteAttachments(HttpContext context)
    {
        var resource = await TryValidateLayerAccessAsync(context, AccessScope.Write);
        if (resource == null)
            return;

        var layerId = resource.Value.Layer.Id;
        var routeFeatureId = TryGetRouteLongValue(context, "featureId", out var routeFeatureIdError);
        if (routeFeatureIdError != null)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, routeFeatureIdError);
            return;
        }

        var form = await TryReadAttachmentFormAsync(context);
        if (form == null)
        {
            return;
        }

        var objectIdValue = form.TryGetValue("objectId", out var formObjectId)
            ? formObjectId.ToString()
            : context.Request.Query.TryGetValue("objectId", out var queryObjectId)
                ? queryObjectId.ToString()
                : null;

        long featureId;
        if (routeFeatureId.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(objectIdValue) &&
                long.TryParse(objectIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectIdFromBody) &&
                objectIdFromBody != routeFeatureId.Value)
            {
                await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId does not match featureId route parameter");
                return;
            }

            featureId = routeFeatureId.Value;
        }
        else if (!long.TryParse(objectIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId parameter is required");
            return;
        }

        var attachmentIdsValue = form.TryGetValue("attachmentIds", out var formAttachmentIds)
            ? formAttachmentIds.ToString()
            : context.Request.Query.TryGetValue("attachmentIds", out var queryAttachmentIds)
                ? queryAttachmentIds.ToString()
                : null;

        if (string.IsNullOrWhiteSpace(attachmentIdsValue))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "attachmentIds parameter is required");
            return;
        }

        // Parse comma-separated attachment IDs
        var attachmentIdStrings = attachmentIdsValue.Split(',', StringSplitOptions.TrimEntries);
        var attachmentIds = new List<long>();

        foreach (var idString in attachmentIdStrings)
        {
            if (idString.Length == 0 ||
                !long.TryParse(idString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                await RouteValidationHelpers.WriteValidationErrorAsync(context, "attachmentIds parameter must contain only numeric values");
                return;
            }

            attachmentIds.Add(id);
        }

        if (attachmentIds.Count == 0)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "At least one valid attachment ID is required");
            return;
        }

        var attachmentStore = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();

        var result = await AttachmentHandler.DeleteAttachmentsAsync(
            context,
            layerId,
            featureId,
            attachmentIds.ToArray(),
            attachmentStore,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    private static async Task HandleAttachmentInfos(HttpContext context)
    {
        var resource = await TryValidateLayerAccessAsync(context);
        if (resource == null)
            return;

        var requestedFormat = context.Request.Query.TryGetValue("f", out var formatValue)
            ? formatValue.ToString()
            : null;
        if (!FeatureServerEndpoints.TryValidateOutputFormat(
                requestedFormat,
                FeatureServerEndpoints.JsonOnlyFormats,
                out _,
                out var formatError))
        {
            await StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid query parameters",
                    [formatError ?? "Output format is not supported."])
                .ExecuteAsync(context);
            return;
        }

        if (!context.Request.RouteValues.TryGetValue("featureId", out var featureIdObj) ||
            !long.TryParse(featureIdObj?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Feature ID must be a valid long integer");
            return;
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var feature = await featureReader.GetAsync(resource.Value.Layer.Id, featureId, context.RequestAborted);
        if (feature == null)
        {
            await StandardErrorHelpers.CreateNotFound(context, $"Feature {featureId} not found").ExecuteAsync(context);
            return;
        }

        var attachmentStore = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();

        var result = await AttachmentHandler.GetAttachmentInfosAsync(
            context,
            resource.Value.Layer.Id,
            featureId,
            attachmentStore,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles downloading attachment content with authorization check
    /// </summary>
    private static async Task HandleDownloadAttachment(HttpContext context)
    {
        var resource = await TryValidateLayerAccessAsync(context);
        if (resource == null)
            return;

        var layerId = resource.Value.Layer.Id;

        if (!context.Request.RouteValues.TryGetValue("featureId", out var featureIdObj) ||
            !long.TryParse(featureIdObj?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Feature ID must be a valid long integer");
            return;
        }

        if (!context.Request.RouteValues.TryGetValue("attachmentId", out var attachmentIdObj) ||
            !long.TryParse(attachmentIdObj?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attachmentId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Attachment ID must be a valid long integer");
            return;
        }

        var attachmentStore = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();

        var result = await AttachmentHandler.DownloadAttachmentAsync(
            context,
            layerId,
            featureId,
            attachmentId,
            attachmentStore,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    private static async Task<IFormCollection?> TryReadAttachmentFormAsync(HttpContext context)
    {
        if (context.Request.HasFormContentType)
        {
            return await context.Request.ReadFormAsync(context.RequestAborted);
        }

        var receivedContentType = string.IsNullOrWhiteSpace(context.Request.ContentType)
            ? "(missing)"
            : context.Request.ContentType.Split(';', 2)[0].Trim();

        await ValidationErrorHelpers.CreateUnsupportedMediaType(
            context,
            receivedContentType,
            SupportedAttachmentContentTypes).ExecuteAsync(context);
        return null;
    }

    private static async Task<(ServiceDefinition Service, LayerDefinition Layer)?> TryValidateLayerAccessAsync(
        HttpContext context,
        AccessScope scope = AccessScope.Read)
    {
        var routeValidator = context.RequestServices.GetRequiredService<IRouteParameterValidator>();
        var serviceResult = routeValidator.ValidateServiceId(context);
        if (!serviceResult.IsValid || string.IsNullOrWhiteSpace(serviceResult.Value))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(
                context,
                serviceResult.ErrorMessage ?? "Service ID is required");
            return null;
        }

        var layerResult = routeValidator.ValidateLayerId(context);
        if (!layerResult.IsValid)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(
                context,
                layerResult.ErrorMessage ?? "Layer ID must be a valid integer");
            return null;
        }

        var serviceId = serviceResult.Value!;
        var layerId = layerResult.Value;

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger,
            context.RequestAborted);
        if (!validationResult.IsValid)
        {
            await validationResult.ErrorResult!.ExecuteAsync(context);
            return null;
        }

        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, validationResult.Layer!, validationResult.Service!, scope);
        if (accessError != null)
        {
            await accessError.ExecuteAsync(context);
            return null;
        }

        if (scope == AccessScope.Write)
        {
            var rbacError = await ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(
                context,
                validationResult.Service!,
                validationResult.Layer!,
                context.RequestAborted);
            if (rbacError != null)
            {
                await rbacError.ExecuteAsync(context);
                return null;
            }
        }

        return (validationResult.Service!, validationResult.Layer!);
    }

    private static bool TryParseObjectIds(
        IReadOnlyDictionary<string, StringValues> values,
        out long[] objectIds,
        out string? error)
    {
        objectIds = [];
        error = null;

        if (!TryGetObjectIdValues(values, out var rawValues))
        {
            error = "objectIds parameter is required";
            return false;
        }

        var tokens = new List<string>();
        foreach (var raw in rawValues)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var segments = raw.Split(',', StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                if (segment.Length == 0)
                {
                    error = "objectIds parameter must contain only numeric values";
                    return false;
                }

                tokens.Add(segment);
            }
        }

        if (tokens.Count == 0)
        {
            error = "objectIds parameter is required";
            return false;
        }

        var parsed = new List<long>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                error = "objectIds parameter must contain only numeric values";
                return false;
            }

            parsed.Add(objectId);
        }

        objectIds = parsed.Distinct().ToArray();
        return true;
    }

    private static bool TryParseReturnUrl(
        IReadOnlyDictionary<string, StringValues> values,
        out bool returnUrl,
        out string? error)
    {
        returnUrl = false;
        error = null;

        if (!values.TryGetValue("returnUrl", out var raw) &&
            !values.TryGetValue("returnurl", out raw))
        {
            return true;
        }

        var value = raw.ToString();
        if (bool.TryParse(value, out var parsedBool))
        {
            returnUrl = parsedBool;
            return true;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
        {
            returnUrl = parsedInt != 0;
            return true;
        }

        error = "returnUrl must be a boolean value";
        return false;
    }

    private static bool TryGetObjectIdValues(
        IReadOnlyDictionary<string, StringValues> values,
        out StringValues rawValues)
    {
        if (values.TryGetValue("objectIds", out rawValues) ||
            values.TryGetValue("objectids", out rawValues))
        {
            return true;
        }

        if (values.TryGetValue("objectId", out rawValues) ||
            values.TryGetValue("objectid", out rawValues))
        {
            return true;
        }

        rawValues = default;
        return false;
    }

    private static long? TryGetRouteLongValue(HttpContext context, string key, out string? error)
    {
        error = null;

        if (!context.Request.RouteValues.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        if (long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        error = $"{key} route parameter must be a valid long integer";
        return null;
    }

    // Handlers delegate directly to AttachmentHandler methods for implementation.
}
