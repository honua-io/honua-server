// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>
/// Configuration options that control whether GeoServices ImageServer service
/// metadata advertises a WebMercatorQuad tile cache (<c>singleFusedMapCache</c> +
/// <c>tileInfo</c>) to match the live <c>/tile/{z}/{y}/{x}</c> route.
/// </summary>
/// <remarks>
/// <para>
/// The <c>/tile</c> route is always served, but advertising it in service metadata
/// is opt-in and disabled by default. This is deliberate: the ImageServer service
/// descriptor is also returned verbatim from the <c>conf.json</c> route that the
/// ArcGIS Maps SDK for .NET native runtime reads while loading an
/// <c>ImageServiceRaster</c>. That native parser treats a service advertising a
/// cache (a <c>singleFusedMapCache:true</c> / tile-cache signal) as CACHED and then
/// rejects honua's dynamic <c>exportImage</c> configuration with "Failed to read
/// configuration data" (#1456).
/// </para>
/// <para>
/// Enabling <see cref="Enabled"/> lets a deployment that primarily serves tiled
/// Esri clients such as <c>L.esri.tiledMapLayer</c> opt in to the tiled contract
/// (#1648); the default keeps the #1456-safe dynamic contract so the native .NET
/// SDK dynamic <c>exportImage</c> path is never regressed.
/// </para>
/// </remarks>
public sealed class ImageServerTileMetadataOptions
{
    /// <summary>
    /// The configuration section name that binds to these options.
    /// </summary>
    public const string SectionName = "GeoServices:ImageServer:TileMetadata";

    /// <summary>
    /// When <see langword="true"/>, ImageServer service metadata advertises
    /// <c>singleFusedMapCache:true</c> and a WebMercatorQuad <c>tileInfo</c> block
    /// covering levels <c>0..</c><see cref="MaxLevel"/>. Defaults to
    /// <see langword="false"/>, which preserves the dynamic (#1456-safe) contract
    /// that the ArcGIS Maps SDK for .NET native runtime requires.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Highest WebMercatorQuad zoom level included in the advertised
    /// <c>tileInfo.lods</c> array when <see cref="Enabled"/> is set. Esri clients
    /// derive the maximum usable zoom from the largest level present. Defaults to
    /// 23 (the standard Esri WebMercator basemap depth); the live tile route accepts
    /// up to level 28.
    /// </summary>
    [Range(0, 28)]
    public int MaxLevel { get; set; } = 23;
}
