// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class AttachmentInfo
{
    public required long Id { get; init; }

    public required string Name { get; init; }

    public required string ContentType { get; init; }

    public required long Size { get; init; }

    public string? Keywords { get; init; }

    public string? Url { get; init; }
}
