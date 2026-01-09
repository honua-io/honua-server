// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;
using Microsoft.Extensions.Options;

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
    /// <summary>
    /// Maps FeatureServer attachment REST API endpoints using AOT-compatible routing
    /// </summary>
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Query attachments for a feature
        var queryAttachments = endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryAttachments", HandleQueryAttachments)
            .WithDisplayName("Query Feature Attachments")
            .WithName("QueryAttachments")
            .WithSummary("Query attachments for a feature")
            .WithDescription("Returns all attachments for a specific feature")
            .WithTags("FeatureServer", "Attachments")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Post }));

        // Add attachment to a feature
        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/addAttachment", HandleAddAttachment)
            .WithDisplayName("Add Feature Attachment")
            .WithName("AddAttachment")
            .WithSummary("Add an attachment to a feature")
            .WithDescription("Upload a file attachment to a specific feature")
            .WithTags("FeatureServer", "Attachments")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .RequireAuthorization()
            .DisableAntiforgery();

        // Update attachment
        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/updateAttachment", HandleUpdateAttachment)
            .WithDisplayName("Update Feature Attachment")
            .WithName("UpdateAttachment")
            .WithSummary("Update an attachment's metadata")
            .WithDescription("Update keywords and other metadata for an existing attachment")
            .WithTags("FeatureServer", "Attachments")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .RequireAuthorization();

        // Delete attachments
        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/deleteAttachments", HandleDeleteAttachments)
            .WithDisplayName("Delete Feature Attachments")
            .WithName("DeleteAttachments")
            .WithSummary("Delete attachments from a feature")
            .WithDescription("Delete one or more attachments from a specific feature")
            .WithTags("FeatureServer", "Attachments")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .RequireAuthorization();

        // Download attachment content
        var downloadAttachment = endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/{featureId:long}/attachments/{attachmentId:long}", HandleDownloadAttachment)
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

        if (!context.Request.Query.TryGetValue("objectId", out var objectIdValue) ||
            !long.TryParse(objectIdValue, out var featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId parameter is required");
            return;
        }

        var attachmentStore = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();

        var result = await AttachmentHandler.QueryAttachmentsAsync(
            layerId,
            featureId,
            attachmentStore,
            logger,
            context,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles adding an attachment to a feature
    /// </summary>
    private static async Task HandleAddAttachment(HttpContext context)
    {
        var resource = await TryValidateLayerAccessAsync(context);
        if (resource == null)
            return;

        var layerId = resource.Value.Layer.Id;

        var objectIdValue = context.Request.Query.TryGetValue("objectId", out var queryObjectId)
            ? queryObjectId.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(objectIdValue) &&
            context.Request.Form.TryGetValue("objectId", out var formObjectId))
        {
            objectIdValue = formObjectId.ToString();
        }

        if (string.IsNullOrWhiteSpace(objectIdValue) ||
            !long.TryParse(objectIdValue, out var featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId parameter is required");
            return;
        }

        if (context.Request.Form.Files.Count == 0)
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "At least one file must be uploaded");
            return;
        }

        var file = context.Request.Form.Files[0];
        var keywords = context.Request.Form.TryGetValue("keywords", out var keywordsValue) ? keywordsValue.ToString() : null;

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
        var resource = await TryValidateLayerAccessAsync(context);
        if (resource == null)
            return;

        var layerId = resource.Value.Layer.Id;

        if (!context.Request.Form.TryGetValue("objectId", out var objectIdValue) ||
            !long.TryParse(objectIdValue, out var featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId parameter is required");
            return;
        }

        if (!context.Request.Form.TryGetValue("attachmentId", out var attachmentIdValue) ||
            !long.TryParse(attachmentIdValue, out var attachmentId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "attachmentId parameter is required");
            return;
        }

        var keywords = context.Request.Form.TryGetValue("keywords", out var keywordsValue) ? keywordsValue.ToString() : null;

        var attachmentStore = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();

        var result = await AttachmentHandler.UpdateAttachmentAsync(
            context,
            layerId,
            featureId,
            attachmentId,
            keywords,
            attachmentStore,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles deleting attachments from a feature
    /// </summary>
    private static async Task HandleDeleteAttachments(HttpContext context)
    {
        var resource = await TryValidateLayerAccessAsync(context);
        if (resource == null)
            return;

        var layerId = resource.Value.Layer.Id;

        if (!context.Request.Form.TryGetValue("objectId", out var objectIdValue) ||
            !long.TryParse(objectIdValue, out var featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId parameter is required");
            return;
        }

        if (!context.Request.Form.TryGetValue("attachmentIds", out var attachmentIdsValue))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "attachmentIds parameter is required");
            return;
        }

        // Parse comma-separated attachment IDs
        var attachmentIdStrings = attachmentIdsValue.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        var attachmentIds = new List<long>();

        foreach (var idString in attachmentIdStrings)
        {
            if (long.TryParse(idString.Trim(), out var id))
            {
                attachmentIds.Add(id);
            }
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
            !long.TryParse(featureIdObj?.ToString(), out var featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Feature ID must be a valid long integer");
            return;
        }

        if (!context.Request.RouteValues.TryGetValue("attachmentId", out var attachmentIdObj) ||
            !long.TryParse(attachmentIdObj?.ToString(), out var attachmentId))
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

    private static async Task<(ServiceDefinition Service, LayerDefinition Layer)?> TryValidateLayerAccessAsync(HttpContext context)
    {
        if (!RouteValidationHelpers.TryValidateServiceId(context, out var serviceId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Service ID is required");
            return null;
        }

        if (!RouteValidationHelpers.TryValidateLayerId(context, out var layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID must be a valid integer");
            return null;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var resourceResult = await resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, context.RequestAborted);
        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";
            if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                await StandardErrorHelpers.CreateBadRequest(context, errorMessage).ExecuteAsync(context);
            }
            else
            {
                await StandardErrorHelpers.CreateNotFound(context, errorMessage).ExecuteAsync(context);
            }

            return null;
        }

        var resource = resourceResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, resource.Layer, resource.Service);
        if (accessError != null)
        {
            await accessError.ExecuteAsync(context);
            return null;
        }

        return resource;
    }

    // Handlers delegate directly to AttachmentHandler methods for implementation.
}
