// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Result of deleting an attachment
/// </summary>
public sealed class DeleteAttachmentResult
{
    /// <summary>
    /// Attachment object ID.
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Global ID of the attachment when available.
    /// </summary>
    public string? GlobalId { get; init; }
}

/// <summary>
/// Response for deleting attachments
/// </summary>
public sealed class DeleteAttachmentsResponse
{
    /// <summary>
    /// Delete attachment results
    /// </summary>
    public required DeleteAttachmentResult[] DeleteAttachmentResults { get; init; }
}
