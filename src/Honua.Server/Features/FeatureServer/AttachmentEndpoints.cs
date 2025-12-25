// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
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
        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryAttachments", HandleQueryAttachments)
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
            .DisableAntiforgery();

        // Update attachment
        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/updateAttachment", HandleUpdateAttachment)
            .WithDisplayName("Update Feature Attachment")
            .WithName("UpdateAttachment")
            .WithSummary("Update an attachment's metadata")
            .WithDescription("Update keywords and other metadata for an existing attachment")
            .WithTags("FeatureServer", "Attachments")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        // Delete attachments
        endpoints.Map("/rest/services/{serviceId}/FeatureServer/{layerId:int}/deleteAttachments", HandleDeleteAttachments)
            .WithDisplayName("Delete Feature Attachments")
            .WithName("DeleteAttachments")
            .WithSummary("Delete attachments from a feature")
            .WithDescription("Delete one or more attachments from a specific feature")
            .WithTags("FeatureServer", "Attachments")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

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
        if (!RouteValidationHelpers.TryValidateLayerId(context, out var layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID must be a valid integer");
            return;
        }

        if (!context.Request.Query.TryGetValue("objectId", out var objectIdValue) ||
            !long.TryParse(objectIdValue, out var featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId parameter is required");
            return;
        }

        var attachmentStore = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();

        var result = await QueryAttachmentsAsync(
            layerId,
            featureId,
            attachmentStore,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles adding an attachment to a feature
    /// </summary>
    private static async Task HandleAddAttachment(HttpContext context)
    {
        if (!RouteValidationHelpers.TryValidateLayerId(context, out var layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID must be a valid integer");
            return;
        }

        if (!context.Request.Form.TryGetValue("objectId", out var objectIdValue) ||
            !long.TryParse(objectIdValue, out var featureId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "objectId parameter is required");
            return;
        }

        if (!context.Request.Form.Files.Any())
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "At least one file must be uploaded");
            return;
        }

        var file = context.Request.Form.Files[0];
        var keywords = context.Request.Form.TryGetValue("keywords", out var keywordsValue) ? keywordsValue.ToString() : null;

        var attachmentStore = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        var logger = context.RequestServices.GetRequiredService<ILogger<AttachmentOperations>>();

        var result = await AddAttachmentAsync(
            layerId,
            featureId,
            file,
            keywords,
            attachmentStore,
            limitsOptions.Value.Attachments,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles updating an attachment's metadata
    /// </summary>
    private static async Task HandleUpdateAttachment(HttpContext context)
    {
        if (!RouteValidationHelpers.TryValidateLayerId(context, out var layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID must be a valid integer");
            return;
        }

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

        var result = await UpdateAttachmentAsync(
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
        if (!RouteValidationHelpers.TryValidateLayerId(context, out var layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID must be a valid integer");
            return;
        }

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

        var result = await DeleteAttachmentsAsync(
            layerId,
            featureId,
            attachmentIds.ToArray(),
            attachmentStore,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Handles downloading attachment content
    /// </summary>
    private static async Task HandleDownloadAttachment(HttpContext context)
    {
        if (!RouteValidationHelpers.TryValidateLayerId(context, out var layerId))
        {
            await RouteValidationHelpers.WriteValidationErrorAsync(context, "Layer ID must be a valid integer");
            return;
        }

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

        var result = await DownloadAttachmentAsync(
            layerId,
            featureId,
            attachmentId,
            attachmentStore,
            logger,
            context.RequestAborted);

        await result.ExecuteAsync(context);
    }

    // Handler methods would go here - delegating to AttachmentHandler
    // For now, creating placeholder methods to be implemented in the handler

    private static Task<IResult> QueryAttachmentsAsync(int layerId, long featureId, IAttachmentStore attachmentStore, ILogger<AttachmentOperations> logger, CancellationToken cancellationToken)
        => AttachmentHandler.QueryAttachmentsAsync(layerId, featureId, attachmentStore, logger, cancellationToken);

    private static Task<IResult> AddAttachmentAsync(int layerId, long featureId, IFormFile file, string? keywords, IAttachmentStore attachmentStore, AttachmentLimits limits, ILogger<AttachmentOperations> logger, CancellationToken cancellationToken)
        => AttachmentHandler.AddAttachmentAsync(layerId, featureId, file, keywords, attachmentStore, limits, logger, cancellationToken);

    private static Task<IResult> UpdateAttachmentAsync(int layerId, long featureId, long attachmentId, string? keywords, IAttachmentStore attachmentStore, ILogger<AttachmentOperations> logger, CancellationToken cancellationToken)
        => AttachmentHandler.UpdateAttachmentAsync(layerId, featureId, attachmentId, keywords, attachmentStore, logger, cancellationToken);

    private static Task<IResult> DeleteAttachmentsAsync(int layerId, long featureId, long[] attachmentIds, IAttachmentStore attachmentStore, ILogger<AttachmentOperations> logger, CancellationToken cancellationToken)
        => AttachmentHandler.DeleteAttachmentsAsync(layerId, featureId, attachmentIds, attachmentStore, logger, cancellationToken);

    private static Task<IResult> DownloadAttachmentAsync(int layerId, long featureId, long attachmentId, IAttachmentStore attachmentStore, ILogger<AttachmentOperations> logger, CancellationToken cancellationToken)
        => AttachmentHandler.DownloadAttachmentAsync(layerId, featureId, attachmentId, attachmentStore, logger, cancellationToken);
}
