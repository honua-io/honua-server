// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>
/// Configuration that enables ImageServer WMTS to advertise and serve tile matrix sets (gridsets)
/// beyond the default WebMercatorQuad — starting with the shared WorldCRS84Quad built-in and any
/// operator-defined gridset registered in <see cref="Core.Features.Tiles.ITileMatrixSetRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is additive and disabled by default: with an empty <see cref="Enabled"/> list ImageServer
/// WMTS advertises exactly WebMercatorQuad and rejects every other <c>TILEMATRIXSET</c>, so the
/// GetCapabilities XML and the OGC WMTS 1.0 CITE baseline (60/60) are byte-for-byte preserved.
/// </para>
/// <para>
/// Only identifiers that resolve in the canonical tile-matrix-set registry take effect; unknown or
/// misconfigured identifiers are ignored so a rolling deployment can add a gridset to configuration
/// before every replica understands it without any replica advertising a grid it cannot serve. The
/// reserved WebMercatorQuad default is always served and does not need to be listed.
/// </para>
/// </remarks>
public sealed class ImageServerTileMatrixSetOptions
{
    /// <summary>
    /// The configuration section name that binds to these options.
    /// </summary>
    public const string SectionName = "GeoServices:ImageServer:TileMatrixSets";

    /// <summary>
    /// The additional tile matrix set identifiers (beyond WebMercatorQuad) that ImageServer WMTS
    /// advertises and serves for each layer, e.g. <c>WorldCRS84Quad</c>. Defaults to an empty list,
    /// which preserves the WebMercatorQuad-only contract and the WMTS CITE baseline.
    /// </summary>
    public IList<string> Enabled { get; } = new List<string>();
}
