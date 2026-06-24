// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene;
using Xunit;

namespace Honua.Core.Tests.Features.Scene;

/// <summary>
/// Unit tests for the <see cref="SceneDatasetType"/> -> I3S layerType / store
/// profile mapping (#1812).
/// </summary>
public sealed class I3sLayerTypeMapTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(SceneDatasetType.HostedTiles, "3DObject")]
    [InlineData(SceneDatasetType.Terrain, "3DObject")]
    [InlineData(SceneDatasetType.Building, "Building")]
    [InlineData(SceneDatasetType.PointCloud, "PointCloud")]
    public void ToLayerType_MapsEachSourceKind(SceneDatasetType type, string expected)
        => I3sLayerTypeMap.ToLayerType(type).Should().Be(expected);

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(SceneDatasetType.HostedTiles, "meshpyramids")]
    [InlineData(SceneDatasetType.Building, "meshpyramids")]
    [InlineData(SceneDatasetType.PointCloud, "points")]
    public void ToStoreProfile_MapsEachSourceKind(SceneDatasetType type, string expected)
        => I3sLayerTypeMap.ToStoreProfile(type).Should().Be(expected);
}
