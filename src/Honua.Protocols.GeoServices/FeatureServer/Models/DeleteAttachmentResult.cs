// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

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

    /// <summary>
    /// Error information when the attachment could not be deleted.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditError? Error { get; init; }
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
