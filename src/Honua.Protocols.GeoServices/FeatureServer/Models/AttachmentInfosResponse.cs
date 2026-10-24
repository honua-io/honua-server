// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
