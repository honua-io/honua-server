// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Scene.Domain;

public sealed record SceneGenerationOptions
{
    public int MaxFeatureCount { get; init; } = 1_000_000;

    public IReadOnlyList<string> IncludeAttributes { get; init; } = [];

    public MetadataV2ExtrusionInfo? ExtrusionOverride { get; init; }

    public int CacheMaxAgeSeconds { get; init; } = 3600;
}
