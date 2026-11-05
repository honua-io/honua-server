// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.TileCachePackage.Domain;

public static class TileCachePackageTileEncoding
{
    public static bool IsGzipCompressed(ReadOnlySpan<byte> content)
        => content.Length >= 2 && content[0] == 0x1F && content[1] == 0x8B;

    public static string? ContentEncoding(ReadOnlySpan<byte> content)
        => IsGzipCompressed(content) ? "gzip" : null;
}
