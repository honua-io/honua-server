// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class AttachmentQueryResponse
{
    public AttachmentGroup[] AttachmentGroups { get; init; } = [];

    public AttachmentInfo[]? AttachmentInfos { get; init; }
}

public sealed class AttachmentGroup
{
    public required long ParentObjectId { get; init; }

    public string ParentGlobalId { get; init; } = string.Empty;

    public AttachmentInfo[] AttachmentInfos { get; init; } = [];
}
