// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Result of adding an attachment
/// </summary>
public sealed class AddAttachmentResult
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
/// Response for adding an attachment
/// </summary>
public sealed class AddAttachmentResponse
{
    /// <summary>
    /// Add attachment result
    /// </summary>
    public required AddAttachmentResult AddAttachmentResult { get; init; }
}
