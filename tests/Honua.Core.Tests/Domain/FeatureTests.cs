// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Domain.Features;
using Xunit;

namespace Honua.Core.Tests.Domain;

public class FeatureTests
{
    [Fact]
    public void Create_WithValidData_ReturnsFeature()
    {
        // Arrange
        var id = 123L;
        var geometry = new byte[] { 1, 2, 3, 4 };
        var attributes = new Dictionary<string, object?> { ["name"] = "Test" }.ToImmutableDictionary();

        // Act
        var feature = Feature.Create(id, geometry, attributes);

        // Assert
        Assert.Equal(id, feature.Id);
        Assert.Equal(geometry, feature.Geometry);
        Assert.Equal(attributes, feature.Attributes);
    }

    [Fact]
    public void Create_WithNullGeometry_ReturnsFeatureWithNullGeometry()
    {
        // Arrange
        var id = 456L;
        var attributes = ImmutableDictionary<string, object?>.Empty;

        // Act
        var feature = Feature.Create(id, null, attributes);

        // Assert
        Assert.Equal(id, feature.Id);
        Assert.Null(feature.Geometry);
        Assert.Equal(attributes, feature.Attributes);
    }

    [Fact]
    public void Create_WithEmptyAttributes_ReturnsFeatureWithEmptyAttributes()
    {
        // Arrange
        var id = 789L;
        var attributes = ImmutableDictionary<string, object?>.Empty;

        // Act
        var feature = Feature.Create(id, null, attributes);

        // Assert
        Assert.Equal(id, feature.Id);
        Assert.Empty(feature.Attributes);
    }
}