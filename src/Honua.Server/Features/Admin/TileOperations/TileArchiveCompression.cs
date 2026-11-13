// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Tiles.PMTiles;

namespace Honua.Server.Features.Admin.TileOperations;

// Tile payload compression for archive and publish operations. Omitted means none, which keeps
// the archive byte-identical to earlier releases.
internal static class TileArchiveCompression
{
    public const string None = "none";
    public const string Gzip = "gzip";
    public const string SupportedValues = "none, gzip";

    public static bool IsSupported(string? value)
        => Normalize(value) is null or None or Gzip;

    public static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    public static PMTilesCompression Parse(string? value)
        => Normalize(value) switch
        {
            null or None => PMTilesCompression.None,
            Gzip => PMTilesCompression.Gzip,
            _ => throw new ArgumentException(
                $"Tile compression must be one of: {SupportedValues}.", nameof(value)),
        };

    public static string ToWireValue(PMTilesCompression compression)
        => compression == PMTilesCompression.Gzip ? Gzip : None;
}
