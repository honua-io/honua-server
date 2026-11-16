// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.PointCloud;

public static class PointCloudCompressionDetector
{
    public static bool IsCompressed(ReadOnlySpan<byte> source)
        => LasPublicHeader.TryRead(source, out var header) && header.IsCompressed;

    public static bool HasLasfSignature(ReadOnlySpan<byte> source)
        => LasPublicHeader.HasSignature(source);
}
