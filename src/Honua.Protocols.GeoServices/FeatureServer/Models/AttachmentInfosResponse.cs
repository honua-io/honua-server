// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response for the canonical per-feature attachment infos resource.
/// </summary>
public sealed class AttachmentInfosResponse
{
    /// <summary>
    /// Attachment infos associated with the requested feature.
    /// </summary>
    public AttachmentInfo[] AttachmentInfos { get; init; } = [];
}
