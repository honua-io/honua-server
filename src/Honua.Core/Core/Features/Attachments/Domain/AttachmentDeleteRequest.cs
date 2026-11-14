// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Attachments.Domain;

public sealed record AttachmentDeleteRequest
{
    public required string OwnerId { get; init; }
    public required string ServiceId { get; init; }
    public required int LayerId { get; init; }
    public required long FeatureId { get; init; }
    public required long AttachmentId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required TimeSpan Lifetime { get; init; }
}
