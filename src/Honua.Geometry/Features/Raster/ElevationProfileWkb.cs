// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Features.Raster.Geometry;

/// <summary>
/// Shared WKB encoder for the elevation-profile line geometry passed to
/// <c>IElevationService.QueryProfileAsync</c>. The byte encoding is behavior
/// (the canonical service decodes these exact bytes), not a protocol wire
/// format, so every elevation adapter — the HTTP endpoint and the gRPC
/// adapter — MUST serialize the profile line through this single helper so they
/// cannot drift on the WKB flags.
/// </summary>
public static class ElevationProfileWkb
{
    /// <summary>
    /// Encodes a profile <see cref="LineString"/> as little-endian 2D WKB
    /// (no SRID, no Z, no M) for submission to the canonical elevation service.
    /// </summary>
    /// <param name="lineString">The profile line to encode.</param>
    /// <returns>The WKB byte sequence consumed by <c>QueryProfileAsync</c>.</returns>
    public static byte[] Write(LineString lineString)
    {
        ArgumentNullException.ThrowIfNull(lineString);
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false);
        return writer.Write(lineString);
    }
}
