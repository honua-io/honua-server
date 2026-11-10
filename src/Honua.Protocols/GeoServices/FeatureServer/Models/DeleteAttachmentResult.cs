// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class DeleteAttachmentResult
{
    public required long ObjectId { get; init; }

    public required bool Success { get; init; }

    public string? GlobalId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditError? Error { get; init; }
}

public sealed class DeleteAttachmentsResponse
{
    public required DeleteAttachmentResult[] DeleteAttachmentResults { get; init; }
}
