// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Response for querying feature attachments
/// </summary>
public sealed class AttachmentQueryResponse
{
    /// <summary>
    /// Attachment groups keyed by parent feature.
    /// </summary>
    public AttachmentGroup[] AttachmentGroups { get; init; } = [];

    /// <summary>
    /// Legacy flattened attachment list for single-feature queries.
    /// </summary>
    public AttachmentInfo[]? AttachmentInfos { get; init; }
}

/// <summary>
/// Group of attachments associated with a feature object ID.
/// </summary>
public sealed class AttachmentGroup
{
    /// <summary>
    /// Parent feature object ID.
    /// </summary>
    public required long ParentObjectId { get; init; }

    /// <summary>
    /// Attachment infos associated with the parent feature.
    /// </summary>
    public AttachmentInfo[] AttachmentInfos { get; init; } = [];
}
