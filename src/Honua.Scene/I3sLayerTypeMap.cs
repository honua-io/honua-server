// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
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

    // Unclassified legacy records never guess an I3S semantic layer type.
    public static bool TryToLayerType(SceneContentKind contentKind, [NotNullWhen(true)] out string? layerType)
    {
        layerType = contentKind switch
        {
            SceneContentKind.Buildings => Building,
            SceneContentKind.PointCloud => PointCloud,
            SceneContentKind.Mesh => ThreeDObject,
            SceneContentKind.Vector3D => ThreeDObject,
            _ => null
        };
        return layerType is not null;
    }

    public static bool TryToStoreProfile(SceneContentKind contentKind, [NotNullWhen(true)] out string? profile)
    {
        profile = contentKind switch
        {
            SceneContentKind.PointCloud => PointsProfile,
            SceneContentKind.Buildings => MeshPyramidsProfile,
            SceneContentKind.Mesh => MeshPyramidsProfile,
            SceneContentKind.Vector3D => MeshPyramidsProfile,
            _ => null
        };
        return profile is not null;
    }
}
