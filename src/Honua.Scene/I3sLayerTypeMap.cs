// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene;

public static class I3sLayerTypeMap
{
    public const string ThreeDObject = "3DObject";

    public const string Building = "Building";

    public const string PointCloud = "PointCloud";

    public const string MeshPyramidsProfile = "meshpyramids";

    public const string PointsProfile = "points";

    public static string ToLayerType(SceneDatasetType datasetType) => datasetType switch
    {
        SceneDatasetType.Building => Building,
        SceneDatasetType.PointCloud => PointCloud,
        _ => ThreeDObject,
    };

    public static string ToStoreProfile(SceneDatasetType datasetType) => datasetType switch
    {
        SceneDatasetType.PointCloud => PointsProfile,
        _ => MeshPyramidsProfile,
    };
}
