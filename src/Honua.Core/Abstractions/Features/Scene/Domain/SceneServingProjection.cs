// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Scene.Domain;

public static class SceneServingProjection
{
    public const string ThreeDTilesWireFormat = "3d-tiles";

    public const string I3sWireFormat = "i3s";

    public const string CopcWireFormat = "copc";

    public const string QuantizedMeshWireFormat = "quantized-mesh";

    public const string TerrainRgbWireFormat = "terrain-rgb";

    public const string UnclassifiedWireContentKind = "unclassified";

    public const string MeshWireContentKind = "mesh";

    public const string BuildingsWireContentKind = "buildings";

    public const string PointCloudWireContentKind = "point-cloud";

    public const string TerrainWireContentKind = "terrain";

    public const string Vector3DWireContentKind = "vector-3d";

    public static AccessPolicy? ToAccessPolicy(SceneDatasetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // A record that says both "public" and "requires authentication" is read as protected.
        return record.IsPublic && !record.RequiresAuth
            ? null
            : new AccessPolicy
            {
                AllowAnonymous = false,
                AllowedRoles = record.AllowedRoles?.ToArray(),
            };
    }

    public static SceneServingFormat ResolveServingFormat(SceneDatasetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.ServingFormat is { } explicitFormat)
        {
            return explicitFormat;
        }

        if (IsCopcFileName(record.TilesetFileName))
        {
            return SceneServingFormat.Copc;
        }

        return record.DatasetType == SceneDatasetType.Terrain
            ? SceneServingFormat.TerrainRgb
            : SceneServingFormat.ThreeDTiles;
    }

    public static SceneServingFormat ResolveServingFormat(SceneDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (dataset.ServingFormat is { } explicitFormat)
        {
            return explicitFormat;
        }

        return IsCopcFileName(dataset.TilesetFileName)
            ? SceneServingFormat.Copc
            : SceneServingFormat.ThreeDTiles;
    }

    public static SceneContentKind ResolveContentKind(SceneDatasetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.ContentKind is { } explicitKind)
        {
            return explicitKind;
        }

        if (ResolveServingFormat(record) == SceneServingFormat.Copc)
        {
            return SceneContentKind.PointCloud;
        }

        // Legacy HostedTiles rows carry no semantic provenance; never guess Mesh.
        return record.DatasetType switch
        {
            SceneDatasetType.Building => SceneContentKind.Buildings,
            SceneDatasetType.PointCloud => SceneContentKind.PointCloud,
            SceneDatasetType.Terrain => SceneContentKind.Terrain,
            _ => SceneContentKind.Unclassified
        };
    }

    public static SceneContentKind ResolveContentKind(SceneDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (dataset.ContentKind is { } explicitKind)
        {
            return explicitKind;
        }

        return ResolveServingFormat(dataset) == SceneServingFormat.Copc
            ? SceneContentKind.PointCloud
            : SceneContentKind.Unclassified;
    }

    public static string ToWireValue(SceneServingFormat format) => format switch
    {
        SceneServingFormat.ThreeDTiles => ThreeDTilesWireFormat,
        SceneServingFormat.I3s => I3sWireFormat,
        SceneServingFormat.Copc => CopcWireFormat,
        SceneServingFormat.QuantizedMesh => QuantizedMeshWireFormat,
        SceneServingFormat.TerrainRgb => TerrainRgbWireFormat,
        _ => ThreeDTilesWireFormat
    };

    public static bool TryParseServingFormat(string? value, out SceneServingFormat format)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            format = default;
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "3d-tiles":
            case "3dtiles":
            case "3d_tiles":
            case "hosted_tiles":
            case "hostedtiles":
                format = SceneServingFormat.ThreeDTiles;
                return true;
            case "i3s":
                format = SceneServingFormat.I3s;
                return true;
            case "copc":
                format = SceneServingFormat.Copc;
                return true;
            case "quantized-mesh":
            case "quantized_mesh":
            case "quantizedmesh":
                format = SceneServingFormat.QuantizedMesh;
                return true;
            case "terrain-rgb":
            case "terrain_rgb":
            case "terrainrgb":
                format = SceneServingFormat.TerrainRgb;
                return true;
            default:
                format = default;
                return false;
        }
    }

    public static string ToWireValue(SceneContentKind kind) => kind switch
    {
        SceneContentKind.Unclassified => UnclassifiedWireContentKind,
        SceneContentKind.Mesh => MeshWireContentKind,
        SceneContentKind.Buildings => BuildingsWireContentKind,
        SceneContentKind.PointCloud => PointCloudWireContentKind,
        SceneContentKind.Terrain => TerrainWireContentKind,
        SceneContentKind.Vector3D => Vector3DWireContentKind,
        _ => UnclassifiedWireContentKind
    };

    public static bool TryParseContentKind(string? value, out SceneContentKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            kind = default;
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "unclassified":
                kind = SceneContentKind.Unclassified;
                return true;
            case "mesh":
            case "3d-object":
            case "3d_object":
            case "3dobject":
                kind = SceneContentKind.Mesh;
                return true;
            case "buildings":
            case "building":
                kind = SceneContentKind.Buildings;
                return true;
            case "point-cloud":
            case "point_cloud":
            case "pointcloud":
                kind = SceneContentKind.PointCloud;
                return true;
            case "terrain":
                kind = SceneContentKind.Terrain;
                return true;
            case "vector-3d":
            case "vector_3d":
            case "vector3d":
                kind = SceneContentKind.Vector3D;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public static bool IsCopcFileName(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
            && fileName.EndsWith(".copc.laz", StringComparison.OrdinalIgnoreCase);
}
