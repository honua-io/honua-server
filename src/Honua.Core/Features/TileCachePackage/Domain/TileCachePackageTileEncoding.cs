// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.TileCachePackage.Domain;

/// <summary>
/// Content-Encoding of a stored tile. Esri Compact Cache V2 vector tiles are gzip
/// members (magic <c>1F 8B</c>) stored verbatim; the serve path declares that encoding
/// instead of decompressing during import.
/// </summary>
public static class TileCachePackageTileEncoding
{
    public static bool IsGzipCompressed(ReadOnlySpan<byte> content)
        => content.Length >= 2 && content[0] == 0x1F && content[1] == 0x8B;

    /// <summary>
    /// <c>gzip</c> when <paramref name="content"/> starts with the gzip magic number;
    /// otherwise <c>null</c> so the response is served as stored bytes.
    /// </summary>
    public static string? ContentEncoding(ReadOnlySpan<byte> content)
        => IsGzipCompressed(content) ? "gzip" : null;
}
