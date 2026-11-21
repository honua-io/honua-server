// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.ImageServer;

// Length limits for ImageServer request lists, shared by the REST and SOAP surfaces.
internal static class ImageServerRequestLimits
{
    // One limit for every band list (REST bandIds, SOAP BandIds and ExtractBand). Repeated
    // band ids stay valid (0,0,0 renders one band as RGB), so the length is what is bounded.
    internal const int MaxBandIds = 256;

    // Each raster id costs a catalog lookup and a clipped pixel scan.
    internal const int MaxRasterIds = 1_000;

    // Input points of a coordinate operation, matching the project operation's geometry limit.
    internal const int MaxInputPoints = 1_000;

    // Counted on the raw text so an over-long list is refused before it is split or parsed.
    internal static bool ExceedsListLimit(string value, int maxEntries)
        => value.AsSpan().Count(',') >= maxEntries;
}
