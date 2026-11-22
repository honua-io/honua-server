// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Scene.Terrain;

public sealed record QuantizedMeshTileInput
{
    public required TerrainGeographicBounds Bounds { get; init; }

    public required int GridSize { get; init; }

    public required double?[] Elevations { get; init; }

    public bool IncludeVertexNormals { get; init; }
}

public readonly record struct QuantizedMeshHeader(
    double CenterX,
    double CenterY,
    double CenterZ,
    float MinimumHeight,
    float MaximumHeight,
    double BoundingSphereCenterX,
    double BoundingSphereCenterY,
    double BoundingSphereCenterZ,
    double BoundingSphereRadius,
    double HorizonOcclusionPointX,
    double HorizonOcclusionPointY,
    double HorizonOcclusionPointZ);

public sealed record QuantizedMeshTile
{
    public required QuantizedMeshHeader Header { get; init; }

    public required ushort[] U { get; init; }

    public required ushort[] V { get; init; }

    public required ushort[] Height { get; init; }

    public required uint[] Indices { get; init; }

    public required uint[] WestIndices { get; init; }

    public required uint[] SouthIndices { get; init; }

    public required uint[] EastIndices { get; init; }

    public required uint[] NorthIndices { get; init; }

    public byte[]? OctEncodedNormals { get; init; }

    public bool Uses32BitIndices { get; init; }
}
