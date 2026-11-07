// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Domain;

public sealed record StudioPublicationLineage
{
    public required Guid ItemId { get; init; }
    public required Guid VersionId { get; init; }
    public required string ServiceId { get; init; }
    public string? TenantId { get; init; }
}

public sealed record StudioUnpublishCommand
{
    public required Guid RequestId { get; init; }
    public required Guid ItemId { get; init; }
    public required Guid ExpectedPublishedVersionId { get; init; }
    public string? ActorId { get; init; }
    public string? Reason { get; init; }
    public string? TenantId { get; init; }
}
