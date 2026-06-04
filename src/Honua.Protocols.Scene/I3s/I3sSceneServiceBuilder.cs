// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Protocols.Scene.I3s;

/// <summary>
/// Builds Esri I3S SceneServer descriptors (service + scene layer) from a
/// registered Honua <see cref="SceneDataset"/> and its known geographic extent.
/// This is the inverse of <see cref="I3sToTilesetConverter"/>: it presents a
/// hosted 3D Tiles scene to ArcGIS / I3S clients as a read-only 3D Object
/// scene layer.
/// </summary>
/// <remarks>
/// This slice serves the service and layer descriptor JSON. Per-node geometry
/// streaming (node pages, geometry/attribute/texture binary resources) is a
/// tracked follow-up; the descriptor advertises a 3DObject layer rooted at the
/// scene extent so a client can discover and inspect the layer.
/// </remarks>
public static class I3sSceneServiceBuilder
{
    /// <summary>Default I3S layer type Honua serves for hosted scenes.</summary>
    public const string DefaultLayerType = "3DObject";

    /// <summary>WGS-84 geographic well-known id used for served layers.</summary>
    public const int Wgs84Wkid = 4326;

    /// <summary>The single layer id Honua exposes per scene (always 0).</summary>
    public const int LayerId = 0;

    /// <summary>
    /// Builds the I3S scene-layer descriptor for a scene with the supplied
    /// extent. Returns a 3D Object layer rooted at the geographic extent in
    /// WGS-84.
    /// </summary>
    /// <param name="scene">The hosted scene dataset.</param>
    /// <param name="extent">The scene's WGS-84 extent, when known.</param>
    /// <param name="minHeightMeters">Minimum elevation in meters, when known.</param>
    /// <param name="maxHeightMeters">Maximum elevation in meters, when known.</param>
    public static I3sSceneLayerDocument BuildLayer(
        SceneDataset scene,
        SceneExtent? extent,
        double? minHeightMeters = null,
        double? maxHeightMeters = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var spatialReference = new I3sSpatialReference
        {
            Wkid = Wgs84Wkid,
            LatestWkid = Wgs84Wkid,
        };

        I3sFullExtent? fullExtent = extent is null
            ? null
            : new I3sFullExtent
            {
                Xmin = extent.XMin,
                Ymin = extent.YMin,
                Xmax = extent.XMax,
                Ymax = extent.YMax,
                Zmin = minHeightMeters,
                Zmax = maxHeightMeters,
                SpatialReference = spatialReference,
            };

        return new I3sSceneLayerDocument
        {
            Id = LayerId,
            LayerType = DefaultLayerType,
            Name = scene.Name,
            Description = scene.Description,
            Version = "1.7",
            SpatialReference = spatialReference,
            FullExtent = fullExtent,
            Store = new I3sStore
            {
                Id = scene.Id,
                Profile = "meshpyramids",
                Version = "1.7",
                RootNode = "./nodes/root",
            },
        };
    }

    /// <summary>
    /// Builds the I3S SceneServer service descriptor wrapping a single scene
    /// layer.
    /// </summary>
    /// <param name="scene">The hosted scene dataset.</param>
    /// <param name="extent">The scene's WGS-84 extent, when known.</param>
    /// <param name="minHeightMeters">Minimum elevation in meters, when known.</param>
    /// <param name="maxHeightMeters">Maximum elevation in meters, when known.</param>
    public static I3sSceneServiceDocument BuildService(
        SceneDataset scene,
        SceneExtent? extent,
        double? minHeightMeters = null,
        double? maxHeightMeters = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        return new I3sSceneServiceDocument
        {
            ServiceName = scene.Name,
            ServiceVersion = "1.7",
            SupportedBindings = ["REST"],
            Layers = [BuildLayer(scene, extent, minHeightMeters, maxHeightMeters)],
        };
    }
}
