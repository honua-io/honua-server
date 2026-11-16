// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record WorkspaceRef
{
    public required string WorkspaceId { get; init; }

    public required WorkspaceKind Kind { get; init; }

    public required string Label { get; init; }

    public string? Uri { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}
