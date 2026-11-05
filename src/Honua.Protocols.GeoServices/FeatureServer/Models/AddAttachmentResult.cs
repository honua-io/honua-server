// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class AddAttachmentResult
{
    public required long ObjectId { get; init; }

    public required bool Success { get; init; }

    public string? GlobalId { get; init; }
}

public sealed class AddAttachmentResponse
{
    public required AddAttachmentResult AddAttachmentResult { get; init; }
}
