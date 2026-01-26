// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Result of updating an attachment
/// </summary>
public sealed class UpdateAttachmentResult
{
    /// <summary>
    /// Feature ID that owns the attachment
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public required bool Success { get; init; }
}

/// <summary>
/// Response for updating an attachment
/// </summary>
public sealed class UpdateAttachmentResponse
{
    /// <summary>
    /// Update attachment result
    /// </summary>
    public required UpdateAttachmentResult UpdateAttachmentResult { get; init; }
}
