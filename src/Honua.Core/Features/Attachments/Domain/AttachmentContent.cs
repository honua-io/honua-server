// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Attachments.Domain;

/// <summary>
/// Represents the content and metadata of an attachment file
/// </summary>
public readonly record struct AttachmentContent
{
    /// <summary>
    /// Attachment metadata
    /// </summary>
    public required Attachment Attachment { get; init; }

    /// <summary>
    /// File content stream
    /// </summary>
    public required Stream Content { get; init; }

    /// <summary>
    /// Creates a new attachment content instance
    /// </summary>
    /// <param name="attachment">Attachment metadata</param>
    /// <param name="content">File content stream</param>
    /// <returns>New attachment content instance</returns>
    public static AttachmentContent Create(Attachment attachment, Stream content)
        => new()
        {
            Attachment = attachment,
            Content = content
        };
}
