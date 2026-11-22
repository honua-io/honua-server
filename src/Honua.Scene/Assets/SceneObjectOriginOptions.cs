// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Scene;

public sealed class SceneObjectOriginOptions
{
    public const string SectionName = "Scenes:ObjectOrigin";

    public const long DefaultMaxSingleAssetBytes = 64L * 1024 * 1024;

    public const long DefaultMaxRangeReadBytes = 16L * 1024 * 1024;

    public const long DefaultMaxCopcFullResponseBytes = 4L * 1024 * 1024;

    public long MaxSingleAssetBytes { get; set; } = DefaultMaxSingleAssetBytes;

    public long MaxRangeReadBytes { get; set; } = DefaultMaxRangeReadBytes;

    public long MaxCopcFullResponseBytes { get; set; } = DefaultMaxCopcFullResponseBytes;
}
