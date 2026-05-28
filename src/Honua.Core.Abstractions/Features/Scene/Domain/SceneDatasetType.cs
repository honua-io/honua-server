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
    Terrain = 1
}
