// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record CleanupResult
{
    public int WorkspacesExpired { get; init; }

    public int WorkspacesDeleted { get; init; }

    public int ArtifactsDeleted { get; init; }

    public long BytesReclaimed { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static CleanupResult None { get; } = new();
}
