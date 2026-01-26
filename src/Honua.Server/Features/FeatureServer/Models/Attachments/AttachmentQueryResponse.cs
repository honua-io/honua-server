// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Response for querying feature attachments
/// </summary>
public sealed class AttachmentQueryResponse
{
    /// <summary>
    /// Array of attachment information
    /// </summary>
    public required AttachmentInfo[] AttachmentInfos { get; init; }
}
