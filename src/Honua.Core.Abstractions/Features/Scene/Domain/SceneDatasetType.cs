// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Source kind backing a registered scene dataset.
/// </summary>
public enum SceneDatasetType
{
    /// <summary>
    /// OGC 3D Tiles dataset rooted at a server-side asset directory.
    /// </summary>
    HostedTiles = 0,

    /// <summary>
    /// Cesium-compatible terrain quantized-mesh dataset.
    /// </summary>
    Terrain = 1,

    /// <summary>
    /// Building Scene Layer source (BIM / CityGML produced by
    /// <c>BuildingSceneLayerBuilder</c>). Served to I3S clients as a
    /// <c>Building</c> scene layer.
    /// </summary>
    Building = 2,

    /// <summary>
    /// Point-cloud source (LAS/LAZ/COPC streamed through the PNTS pipeline).
    /// Served to I3S clients as a <c>PointCloud</c> scene layer.
    /// </summary>
    PointCloud = 3
}
