// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ObjectStorage;

// Detects a browse entry's kind from the first bytes of the object, never from its name.
public static class ObjectStorageObjectKinds
{
    public const string Cog = "cog";

    public const string GeoTiff = "geotiff";

    public const string PMTiles = "pmtiles";

    public const string GeoParquet = "geoparquet";

    public const string Other = "other";

    // Enough for the TIFF header plus the GDAL ghost area that marks a cloud-optimised layout.
    public const int SniffBytes = 1024;

    private static readonly byte[] PMTilesMagic = "PMTiles"u8.ToArray();

    private static readonly byte[] ParquetMagic = "PAR1"u8.ToArray();

    private static readonly byte[] CogLayout = "LAYOUT=COG"u8.ToArray();

    public static string Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(PMTilesMagic))
        {
            return PMTiles;
        }

        if (header.StartsWith(ParquetMagic))
        {
            return GeoParquet;
        }

        if (!IsTiff(header))
        {
            return Other;
        }

        var window = header[..Math.Min(header.Length, SniffBytes)];
        return window.IndexOf(CogLayout) >= 0 ? Cog : GeoTiff;
    }

    private static bool IsTiff(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return false;
        }

        var littleEndian = header[0] == (byte)'I' && header[1] == (byte)'I';
        var bigEndian = header[0] == (byte)'M' && header[1] == (byte)'M';
        if (littleEndian)
        {
            return (header[2] == 42 || header[2] == 43) && header[3] == 0;
        }

        return bigEndian && header[2] == 0 && (header[3] == 42 || header[3] == 43);
    }
}
