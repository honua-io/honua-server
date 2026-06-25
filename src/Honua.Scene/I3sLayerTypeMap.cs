// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene;

/// <summary>
/// Maps a Honua <see cref="SceneDatasetType"/> to the Esri I3S
/// <c>layerType</c> string advertised on the <c>3dSceneLayer</c> descriptor and
/// the store <c>profile</c> the served node store conforms to (#1812).
/// </summary>
/// <remarks>
/// The I3S 1.7 layer types Honua serves are <c>3DObject</c> (hosted 3D Tiles
/// meshes — the default), <c>Building</c> (BIM/CityGML Building Scene Layers via
/// <c>BuildingSceneLayerBuilder</c>), and <c>PointCloud</c> (LAS/LAZ/COPC content
/// streamed through the PNTS pipeline). <c>IntegratedMesh</c> is intentionally
/// excluded — Honua has no photogrammetry source for it yet (#1805). Terrain
/// datasets are quantized-mesh elevation surfaces, not I3S scene layers, so they
/// fall back to <c>3DObject</c> and are not advertised as SceneServer layers by
/// the discovery surface.
/// </remarks>
public static class I3sLayerTypeMap
{
    /// <summary>I3S layer type for hosted 3D Tiles object meshes.</summary>
    public const string ThreeDObject = "3DObject";

    /// <summary>I3S layer type for Building Scene Layers.</summary>
    public const string Building = "Building";

    /// <summary>I3S layer type for point clouds.</summary>
    public const string PointCloud = "PointCloud";

    /// <summary>I3S store profile for mesh pyramids (3D Object / Building).</summary>
    public const string MeshPyramidsProfile = "meshpyramids";

    /// <summary>I3S store profile for point-cloud node stores.</summary>
    public const string PointsProfile = "points";

    /// <summary>
    /// Resolves the I3S <c>layerType</c> for the supplied dataset source kind.
    /// Unknown / non-scene kinds default to <see cref="ThreeDObject"/>.
    /// </summary>
    /// <param name="datasetType">The registered dataset's source kind.</param>
    /// <returns>The I3S layer type string for the descriptor.</returns>
    public static string ToLayerType(SceneDatasetType datasetType) => datasetType switch
    {
        SceneDatasetType.Building => Building,
        SceneDatasetType.PointCloud => PointCloud,
        _ => ThreeDObject,
    };

    /// <summary>
    /// Resolves the I3S store <c>profile</c> for the supplied dataset source
    /// kind. Point clouds use the <c>points</c> profile; everything else uses
    /// <c>meshpyramids</c>.
    /// </summary>
    /// <param name="datasetType">The registered dataset's source kind.</param>
    /// <returns>The I3S store profile string for the descriptor.</returns>
    public static string ToStoreProfile(SceneDatasetType datasetType) => datasetType switch
    {
        SceneDatasetType.PointCloud => PointsProfile,
        _ => MeshPyramidsProfile,
    };
}
