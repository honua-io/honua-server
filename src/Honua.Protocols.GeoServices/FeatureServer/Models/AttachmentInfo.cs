// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Attachment information for feature attachments
/// </summary>
public sealed class AttachmentInfo
{
    /// <summary>
    /// Attachment identifier
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Original filename
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// MIME content type
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// File size in bytes
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Optional keywords for the attachment
    /// </summary>
    public string? Keywords { get; init; }

    /// <summary>
    /// Optional absolute attachment download URL when requested.
    /// </summary>
    public string? Url { get; init; }
}
