// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Handler for FeatureServer attachment operations.
/// Provides static methods for attachment CRUD operations.
/// </summary>
internal static partial class AttachmentHandler
{
    /// <summary>
    /// Queries attachments for a specific feature
    /// </summary>
    public static async Task<IResult> QueryAttachmentsAsync(
        int layerId,
        long featureId,
        IAttachmentStore attachmentStore,
        ILogger<AttachmentOperations> logger,
        HttpContext context,
        CancellationToken cancellationToken)
        => await QueryAttachmentsAsync(
            layerId,
            [featureId],
            returnUrl: false,
            AttachmentQueryFilter.None,
            attachmentStore,
            logger,
            context,
            cancellationToken);

    /// <summary>
    /// Queries attachments for one or more features, optionally narrowing the
    /// per-feature attachment list with the Esri <c>attachmentTypes</c>,
    /// <c>keywords</c>, and <c>size</c> facets carried by <paramref name="filter"/>.
    /// </summary>
    public static async Task<IResult> QueryAttachmentsAsync(
        int layerId,
        IReadOnlyList<long> featureIds,
        bool returnUrl,
        AttachmentQueryFilter filter,
        IAttachmentStore attachmentStore,
        ILogger<AttachmentOperations> logger,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var uniqueFeatureIds = featureIds
                .Distinct()
                .ToArray();

            var groups = new List<AttachmentGroup>(uniqueFeatureIds.Length);
            foreach (var featureId in uniqueFeatureIds)
            {
                LogQueryAttachments(logger, layerId, featureId);

                var attachments = await attachmentStore.ListAsync(layerId, featureId, cancellationToken);
                if (filter.HasAnyFacet)
                {
                    attachments = Array.FindAll(attachments, filter.Matches);
                }

                var infos = MapAttachmentInfos(attachments, context, layerId, featureId, returnUrl);

                groups.Add(new AttachmentGroup
                {
                    ParentObjectId = featureId,
                    // Esri always emits parentGlobalId; Honua attachments have no
                    // global-id identity column, so emit an empty string (never omit
                    // the key) so AttachmentManager.search() does not KeyError.
                    ParentGlobalId = string.Empty,
                    AttachmentInfos = infos
                });
            }

            var response = new AttachmentQueryResponse
            {
                AttachmentGroups = groups.ToArray(),
                AttachmentInfos = groups.Count == 1 ? groups[0].AttachmentInfos : null
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            var featureId = featureIds.Count > 0 ? featureIds[0] : 0;
            LogQueryAttachmentsError(logger, layerId, featureId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to query attachments");
        }
    }

    /// <summary>
    /// Returns the canonical per-feature attachment infos resource.
    /// </summary>
    public static async Task<IResult> GetAttachmentInfosAsync(
        HttpContext context,
        int layerId,
        long featureId,
        IAttachmentStore attachmentStore,
        ILogger<AttachmentOperations> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            LogQueryAttachments(logger, layerId, featureId);
            var attachments = await attachmentStore.ListAsync(layerId, featureId, cancellationToken);
            var response = new AttachmentInfosResponse
            {
                AttachmentInfos = MapAttachmentInfos(attachments, context, layerId, featureId, returnUrl: false)
            };

            return Results.Json(
                response,
                FeatureServerJsonContext.Default.AttachmentInfosResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            LogQueryAttachmentsError(logger, layerId, featureId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to query attachments");
        }
    }

    /// <summary>
    /// Adds an attachment to a feature with comprehensive security validation.
    /// </summary>
    public static async Task<IResult> AddAttachmentAsync(
        HttpContext context,
        int layerId,
        long featureId,
        IFormFile file,
        string? keywords,
        IAttachmentStore attachmentStore,
        AttachmentLimits limits,
        FileUploadSecurityOptions securityOptions,
        ILogger<AttachmentOperations> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var safeFileName = FileUploadSecurity.SanitizeFileName(file.FileName);
            LogAddAttachment(logger, layerId, featureId, safeFileName);

            // Security Layer 1: Validate file name for path traversal and dangerous patterns
            var fileNameValidation = FileUploadSecurity.ValidateFileName(file.FileName);
            if (!fileNameValidation.IsValid)
            {
                LogSecurityValidationFailed(logger, layerId, featureId, "filename", fileNameValidation.ErrorMessage);
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid file name",
                    [fileNameValidation.ErrorMessage ?? "File name validation failed"]);
            }

            // Security Layer 2: Validate file size against configured limits
            if (file.Length > limits.MaxAttachmentSize)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"File size ({file.Length:N0} bytes) exceeds maximum allowed size ({limits.MaxAttachmentSize:N0} bytes)");
            }

            // Security Layer 3: Validate MIME type against allowed types
            if (!IsAllowedMimeType(file.ContentType, limits.AllowedMimeTypes))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"File type '{file.ContentType}' is not allowed");
            }

            // Security Layer 4: Validate file content for malicious signatures
            var contentValidation = await FileUploadSecurity.ValidateFileContentAsync(
                file,
                securityOptions.MaxSecurityScanSizeBytes,
                cancellationToken);
            if (!contentValidation.IsValid)
            {
                LogSecurityValidationFailed(logger, layerId, featureId, "content", contentValidation.ErrorMessage);
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid file content",
                    [contentValidation.ErrorMessage ?? "File content validation failed"]);
            }

            // Check if feature already has maximum number of attachments
            var existingAttachments = await attachmentStore.ListAsync(layerId, featureId, cancellationToken);
            if (existingAttachments.Length >= limits.MaxAttachmentsPerFeature)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Feature already has the maximum number of attachments ({limits.MaxAttachmentsPerFeature})");
            }

            // Check total attachment size for the feature
            var totalExistingSize = existingAttachments.Sum(a => a.Size);
            if (totalExistingSize + file.Length > limits.MaxTotalAttachmentSize)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Total attachment size would exceed maximum allowed ({limits.MaxTotalAttachmentSize:N0} bytes)");
            }

            // Upload the attachment
            await using var stream = file.OpenReadStream();
            var attachment = await attachmentStore.UploadAsync(
                layerId,
                featureId,
                safeFileName,
                file.ContentType,
                stream,
                keywords,
                cancellationToken);

            var response = new AddAttachmentResponse
            {
                AddAttachmentResult = new AddAttachmentResult
                {
                    ObjectId = attachment.Id,
                    Success = true
                }
            };

            LogAddAttachmentSuccess(logger, layerId, featureId, attachment.Id);
            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            var safeFileName = FileUploadSecurity.SanitizeFileName(file.FileName);
            LogAddAttachmentError(logger, layerId, featureId, safeFileName, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to add attachment");
        }
    }

    /// <summary>
    /// Updates an attachment's metadata and optionally replaces its file content.
    /// </summary>
    public static async Task<IResult> UpdateAttachmentAsync(
        HttpContext context,
        int layerId,
        long featureId,
        long attachmentId,
        IFormFile? file,
        string? keywords,
        IAttachmentStore attachmentStore,
        AttachmentLimits limits,
        FileUploadSecurityOptions securityOptions,
        ILogger<AttachmentOperations> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            LogUpdateAttachment(logger, layerId, featureId, attachmentId);

            var existingAttachment = await attachmentStore.GetAsync(layerId, featureId, attachmentId, cancellationToken);
            if (!existingAttachment.HasValue)
            {
                return StandardErrorHelpers.CreateNotFound(context,
                    $"Attachment {attachmentId} not found for feature {featureId}");
            }

            var attachment = existingAttachment.Value;
            if (file != null)
            {
                var fileNameValidation = FileUploadSecurity.ValidateFileName(file.FileName);
                if (!fileNameValidation.IsValid)
                {
                    LogSecurityValidationFailed(logger, layerId, featureId, "filename", fileNameValidation.ErrorMessage);
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid file name",
                        [fileNameValidation.ErrorMessage ?? "File name validation failed"]);
                }

                if (file.Length > limits.MaxAttachmentSize)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        $"File size ({file.Length:N0} bytes) exceeds maximum allowed size ({limits.MaxAttachmentSize:N0} bytes)");
                }

                if (!IsAllowedMimeType(file.ContentType, limits.AllowedMimeTypes))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        $"File type '{file.ContentType}' is not allowed");
                }

                var contentValidation = await FileUploadSecurity.ValidateFileContentAsync(
                    file,
                    securityOptions.MaxSecurityScanSizeBytes,
                    cancellationToken);
                if (!contentValidation.IsValid)
                {
                    LogSecurityValidationFailed(logger, layerId, featureId, "content", contentValidation.ErrorMessage);
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid file content",
                        [contentValidation.ErrorMessage ?? "File content validation failed"]);
                }

                var existingAttachments = await attachmentStore.ListAsync(layerId, featureId, cancellationToken);
                var totalOtherAttachmentSize = existingAttachments
                    .Where(existing => existing.Id != attachmentId)
                    .Sum(existing => existing.Size);
                if (totalOtherAttachmentSize + file.Length > limits.MaxTotalAttachmentSize)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        $"Total attachment size would exceed maximum allowed ({limits.MaxTotalAttachmentSize:N0} bytes)");
                }

                await using var stream = file.OpenReadStream();
                await attachmentStore.ReplaceAsync(
                    layerId,
                    featureId,
                    attachmentId,
                    FileUploadSecurity.SanitizeFileName(file.FileName),
                    file.ContentType,
                    stream,
                    keywords,
                    cancellationToken);
            }
            else
            {
                var updatedAttachment = Attachment.Create(
                    attachment.Id,
                    attachment.FeatureId,
                    attachment.LayerId,
                    attachment.Filename,
                    attachment.ContentType,
                    attachment.Size,
                    attachment.CreatedAt,
                    attachment.StoragePath,
                    keywords);

                await attachmentStore.UpdateAsync(layerId, featureId, updatedAttachment, cancellationToken);
            }

            var response = new UpdateAttachmentResponse
            {
                UpdateAttachmentResult = new UpdateAttachmentResult
                {
                    ObjectId = attachmentId,
                    Success = true
                }
            };

            LogUpdateAttachmentSuccess(logger, layerId, featureId, attachmentId);
            return Results.Ok(response);
        }
        catch (ResourceNotFoundException ex)
        {
            LogUpdateAttachmentError(logger, layerId, featureId, attachmentId, ex);
            return StandardErrorHelpers.CreateNotFound(context,
                $"Attachment {attachmentId} not found for feature {featureId}");
        }
        catch (Exception ex)
        {
            LogUpdateAttachmentError(logger, layerId, featureId, attachmentId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to update attachment");
        }
    }

    /// <summary>
    /// Deletes attachments from a feature
    /// </summary>
    public static async Task<IResult> DeleteAttachmentsAsync(
        HttpContext context,
        int layerId,
        long featureId,
        long[] attachmentIds,
        IAttachmentStore attachmentStore,
        ILogger<AttachmentOperations> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            LogDeleteAttachments(logger, layerId, featureId, attachmentIds.Length);

            var deleteResults = new List<DeleteAttachmentResult>();

            foreach (var attachmentId in attachmentIds)
            {
                var success = await attachmentStore.DeleteAsync(layerId, featureId, attachmentId, cancellationToken);
                deleteResults.Add(new DeleteAttachmentResult
                {
                    ObjectId = attachmentId,
                    Success = success
                });
            }

            var response = new DeleteAttachmentsResponse
            {
                DeleteAttachmentResults = deleteResults.ToArray()
            };

            var successCount = deleteResults.Count(r => r.Success);
            LogDeleteAttachmentsSuccess(logger, layerId, featureId, successCount, attachmentIds.Length);

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            LogDeleteAttachmentsError(logger, layerId, featureId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to delete attachments");
        }
    }

    /// <summary>
    /// Downloads attachment content
    /// </summary>
    public static async Task<IResult> DownloadAttachmentAsync(
        HttpContext context,
        int layerId,
        long featureId,
        long attachmentId,
        IAttachmentStore attachmentStore,
        ILogger<AttachmentOperations> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            LogDownloadAttachment(logger, layerId, featureId, attachmentId);

            var attachmentContent = await attachmentStore.DownloadAsync(layerId, featureId, attachmentId, cancellationToken);
            if (attachmentContent == null)
            {
                return StandardErrorHelpers.CreateNotFound(context,
                    $"Attachment {attachmentId} not found for feature {featureId}");
            }

            var attachment = attachmentContent.Value.Attachment;
            var content = attachmentContent.Value.Content;

            LogDownloadAttachmentSuccess(logger, layerId, featureId, attachmentId);

            // Block browser-side MIME sniffing — the stored ContentType is client-supplied
            // and, if it disagreed with the actual bytes, legacy browsers could still
            // execute the content inline. Results.File with a filename forces
            // Content-Disposition: attachment; X-Content-Type-Options locks the declared
            // MIME so intermediaries cannot override it.
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";

            return Results.File(
                content,
                attachment.ContentType,
                attachment.Filename,
                enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            LogDownloadAttachmentError(logger, layerId, featureId, attachmentId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to download attachment");
        }
    }

    /// <summary>
    /// Validates if a MIME type is allowed based on configured allowed types
    /// </summary>
    private static bool IsAllowedMimeType(string? contentType, string allowedMimeTypes)
    {
        if (string.IsNullOrEmpty(contentType) || string.IsNullOrEmpty(allowedMimeTypes))
            return false;

        var allowedTypes = allowedMimeTypes.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .ToArray();

        var normalizedContentType = contentType.ToLowerInvariant();

        foreach (var allowedType in allowedTypes)
        {
            if (allowedType.EndsWith("/*"))
            {
                // Wildcard type (e.g., "image/*")
                var prefix = allowedType[..^1]; // Remove the '*'
                if (normalizedContentType.StartsWith(prefix))
                    return true;
            }
            else if (allowedType == normalizedContentType)
            {
                // Exact match
                return true;
            }
        }

        return false;
    }

    private static AttachmentInfo[] MapAttachmentInfos(
        IReadOnlyList<Attachment> attachments,
        HttpContext context,
        int layerId,
        long featureId,
        bool returnUrl)
        => attachments.Select(attachment => new AttachmentInfo
        {
            Id = attachment.Id,
            Name = attachment.Filename,
            ContentType = attachment.ContentType,
            Size = attachment.Size,
            Keywords = attachment.Keywords,
            Url = returnUrl
                ? BuildAttachmentUrl(context, layerId, featureId, attachment.Id)
                : null
        }).ToArray();

    private static string? BuildAttachmentUrl(HttpContext context, int layerId, long featureId, long attachmentId)
    {
        if (!context.Request.RouteValues.TryGetValue("serviceId", out var serviceIdObj))
        {
            return null;
        }

        var serviceId = serviceIdObj?.ToString();
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return null;
        }

        var relativePath = $"{context.Request.PathBase}/rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}";
        if (BaseUrlResolver.TryGetConfiguredBaseUrl(context, out var configuredBaseUrl) &&
            !string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return $"{configuredBaseUrl}{relativePath}";
        }

        return relativePath;
    }

    #region Logging

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Querying attachments for layer {LayerId}, feature {FeatureId}")]
    private static partial void LogQueryAttachments(ILogger<AttachmentOperations> logger, int layerId, long featureId);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Failed to query attachments for layer {LayerId}, feature {FeatureId}")]
    private static partial void LogQueryAttachmentsError(ILogger<AttachmentOperations> logger, int layerId, long featureId, Exception ex);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Adding attachment '{FileName}' to layer {LayerId}, feature {FeatureId}")]
    private static partial void LogAddAttachment(ILogger<AttachmentOperations> logger, int layerId, long featureId, string fileName);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "Successfully added attachment {AttachmentId} to layer {LayerId}, feature {FeatureId}")]
    private static partial void LogAddAttachmentSuccess(ILogger<AttachmentOperations> logger, int layerId, long featureId, long attachmentId);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Error, Message = "Failed to add attachment '{FileName}' to layer {LayerId}, feature {FeatureId}")]
    private static partial void LogAddAttachmentError(ILogger<AttachmentOperations> logger, int layerId, long featureId, string fileName, Exception ex);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Information, Message = "Updating attachment {AttachmentId} for layer {LayerId}, feature {FeatureId}")]
    private static partial void LogUpdateAttachment(ILogger<AttachmentOperations> logger, int layerId, long featureId, long attachmentId);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Information, Message = "Successfully updated attachment {AttachmentId} for layer {LayerId}, feature {FeatureId}")]
    private static partial void LogUpdateAttachmentSuccess(ILogger<AttachmentOperations> logger, int layerId, long featureId, long attachmentId);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Error, Message = "Failed to update attachment {AttachmentId} for layer {LayerId}, feature {FeatureId}")]
    private static partial void LogUpdateAttachmentError(ILogger<AttachmentOperations> logger, int layerId, long featureId, long attachmentId, Exception ex);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Information, Message = "Deleting {AttachmentCount} attachments from layer {LayerId}, feature {FeatureId}")]
    private static partial void LogDeleteAttachments(ILogger<AttachmentOperations> logger, int layerId, long featureId, int attachmentCount);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Information, Message = "Successfully deleted {SuccessCount}/{TotalCount} attachments from layer {LayerId}, feature {FeatureId}")]
    private static partial void LogDeleteAttachmentsSuccess(ILogger<AttachmentOperations> logger, int layerId, long featureId, int successCount, int totalCount);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Error, Message = "Failed to delete attachments from layer {LayerId}, feature {FeatureId}")]
    private static partial void LogDeleteAttachmentsError(ILogger<AttachmentOperations> logger, int layerId, long featureId, Exception ex);

    [LoggerMessage(EventId = 2012, Level = LogLevel.Information, Message = "Downloading attachment {AttachmentId} from layer {LayerId}, feature {FeatureId}")]
    private static partial void LogDownloadAttachment(ILogger<AttachmentOperations> logger, int layerId, long featureId, long attachmentId);

    [LoggerMessage(EventId = 2013, Level = LogLevel.Information, Message = "Successfully downloaded attachment {AttachmentId} from layer {LayerId}, feature {FeatureId}")]
    private static partial void LogDownloadAttachmentSuccess(ILogger<AttachmentOperations> logger, int layerId, long featureId, long attachmentId);

    [LoggerMessage(EventId = 2014, Level = LogLevel.Error, Message = "Failed to download attachment {AttachmentId} from layer {LayerId}, feature {FeatureId}")]
    private static partial void LogDownloadAttachmentError(ILogger<AttachmentOperations> logger, int layerId, long featureId, long attachmentId, Exception ex);

    [LoggerMessage(EventId = 2015, Level = LogLevel.Warning, Message = "Security validation failed for {ValidationType} on layer {LayerId}, feature {FeatureId}: {Reason}")]
    private static partial void LogSecurityValidationFailed(ILogger<AttachmentOperations> logger, int layerId, long featureId, string validationType, string? reason);

    #endregion
}
