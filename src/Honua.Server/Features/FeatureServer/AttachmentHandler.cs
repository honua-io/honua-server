// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;

namespace Honua.Server.Features.FeatureServer;

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
        CancellationToken cancellationToken)
    {
        try
        {
            LogQueryAttachments(logger, layerId, featureId);

            var attachments = await attachmentStore.ListAsync(layerId, featureId, cancellationToken);

            var attachmentInfos = attachments.Select(a => new AttachmentInfo
            {
                Id = a.Id,
                Name = a.Filename,
                ContentType = a.ContentType,
                Size = a.Size,
                Keywords = a.Keywords
            }).ToArray();

            var response = new AttachmentQueryResponse
            {
                AttachmentInfos = attachmentInfos
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            LogQueryAttachmentsError(logger, layerId, featureId, ex);
            return GeoServicesErrorHelpers.CreateInternalServerError("Failed to query attachments");
        }
    }

    /// <summary>
    /// Adds an attachment to a feature with comprehensive security validation.
    /// </summary>
    public static async Task<IResult> AddAttachmentAsync(
        int layerId,
        long featureId,
        IFormFile file,
        string? keywords,
        IAttachmentStore attachmentStore,
        AttachmentLimits limits,
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
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Invalid file name",
                    [fileNameValidation.ErrorMessage ?? "File name validation failed"]);
            }

            // Security Layer 2: Validate file size against configured limits
            if (file.Length > limits.MaxAttachmentSize)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    $"File size ({file.Length:N0} bytes) exceeds maximum allowed size ({limits.MaxAttachmentSize:N0} bytes)");
            }

            // Security Layer 3: Validate MIME type against allowed types
            if (!IsAllowedMimeType(file.ContentType, limits.AllowedMimeTypes))
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    $"File type '{file.ContentType}' is not allowed");
            }

            // Security Layer 4: Validate file content for malicious signatures
            var contentValidation = await FileUploadSecurity.ValidateFileContentAsync(file, cancellationToken);
            if (!contentValidation.IsValid)
            {
                LogSecurityValidationFailed(logger, layerId, featureId, "content", contentValidation.ErrorMessage);
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Invalid file content",
                    [contentValidation.ErrorMessage ?? "File content validation failed"]);
            }

            // Check if feature already has maximum number of attachments
            var existingAttachments = await attachmentStore.ListAsync(layerId, featureId, cancellationToken);
            if (existingAttachments.Length >= limits.MaxAttachmentsPerFeature)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    $"Feature already has the maximum number of attachments ({limits.MaxAttachmentsPerFeature})");
            }

            // Check total attachment size for the feature
            var totalExistingSize = existingAttachments.Sum(a => a.Size);
            if (totalExistingSize + file.Length > limits.MaxTotalAttachmentSize)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
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
                    ObjectId = featureId,
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
            return GeoServicesErrorHelpers.CreateInternalServerError("Failed to add attachment");
        }
    }

    /// <summary>
    /// Updates an attachment's metadata
    /// </summary>
    public static async Task<IResult> UpdateAttachmentAsync(
        int layerId,
        long featureId,
        long attachmentId,
        string? keywords,
        IAttachmentStore attachmentStore,
        ILogger<AttachmentOperations> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            LogUpdateAttachment(logger, layerId, featureId, attachmentId);

            var existingAttachment = await attachmentStore.GetAsync(layerId, featureId, attachmentId, cancellationToken);
            if (!existingAttachment.HasValue)
            {
                return GeoServicesErrorHelpers.CreateNotFoundError(
                    $"Attachment {attachmentId} not found for feature {featureId}");
            }

            // Create updated attachment with new keywords
            var attachment = existingAttachment.Value;
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

            var response = new UpdateAttachmentResponse
            {
                UpdateAttachmentResult = new UpdateAttachmentResult
                {
                    ObjectId = featureId,
                    Success = true
                }
            };

            LogUpdateAttachmentSuccess(logger, layerId, featureId, attachmentId);
            return Results.Ok(response);
        }
        catch (ResourceNotFoundException ex)
        {
            LogUpdateAttachmentError(logger, layerId, featureId, attachmentId, ex);
            return GeoServicesErrorHelpers.CreateNotFoundError(
                $"Attachment {attachmentId} not found for feature {featureId}");
        }
        catch (Exception ex)
        {
            LogUpdateAttachmentError(logger, layerId, featureId, attachmentId, ex);
            return GeoServicesErrorHelpers.CreateInternalServerError("Failed to update attachment");
        }
    }

    /// <summary>
    /// Deletes attachments from a feature
    /// </summary>
    public static async Task<IResult> DeleteAttachmentsAsync(
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
                    ObjectId = featureId,
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
            return GeoServicesErrorHelpers.CreateInternalServerError("Failed to delete attachments");
        }
    }

    /// <summary>
    /// Downloads attachment content
    /// </summary>
    public static async Task<IResult> DownloadAttachmentAsync(
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
                return GeoServicesErrorHelpers.CreateNotFoundError(
                    $"Attachment {attachmentId} not found for feature {featureId}");
            }

            var attachment = attachmentContent.Value.Attachment;
            var content = attachmentContent.Value.Content;

            LogDownloadAttachmentSuccess(logger, layerId, featureId, attachmentId);

            return Results.File(
                content,
                attachment.ContentType,
                attachment.Filename,
                enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            LogDownloadAttachmentError(logger, layerId, featureId, attachmentId, ex);
            return GeoServicesErrorHelpers.CreateInternalServerError("Failed to download attachment");
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
