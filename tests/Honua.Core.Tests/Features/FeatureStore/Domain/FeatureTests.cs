// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Xunit;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

/// <summary>
/// Unit tests for Feature domain model
/// </summary>
public class FeatureTests
{
    [Fact]
    public void Create_WithAttributes_ShouldCreateValidFeature()
    {
        // Arrange
        const long expectedId = 123;
        var expectedGeometry = new byte[] { 1, 2, 3 };
        var expectedAttributes = ImmutableDictionary<string, object?>.Empty
            .Add("name", "Test Feature")
            .Add("type", "Point");

        // Act
        var feature = Feature.Create(expectedId, expectedGeometry, expectedAttributes);

        // Assert
        feature.Id.Should().Be(expectedId);
        feature.Geometry.Should().BeEquivalentTo(expectedGeometry);
        feature.Attributes.Should().BeEquivalentTo(expectedAttributes);
    }

    [Fact]
    public void Create_WithoutAttributes_ShouldCreateFeatureWithEmptyAttributes()
    {
        // Arrange
        const long expectedId = 456;
        var expectedGeometry = new byte[] { 4, 5, 6 };

        // Act
        var feature = Feature.Create(expectedId, expectedGeometry);

        // Assert
        feature.Id.Should().Be(expectedId);
        feature.Geometry.Should().BeEquivalentTo(expectedGeometry);
        feature.Attributes.Should().NotBeNull();
        feature.Attributes.Should().BeEmpty();
    }

    [Fact]
    public void Create_WithNullGeometry_ShouldCreateValidFeature()
    {
        // Arrange
        const long expectedId = 789;
        var expectedAttributes = ImmutableDictionary<string, object?>.Empty
            .Add("name", "No Geometry Feature");

        // Act
        var feature = Feature.Create(expectedId, null, expectedAttributes);

        // Assert
        feature.Id.Should().Be(expectedId);
        feature.Geometry.Should().BeNull();
        feature.Attributes.Should().BeEquivalentTo(expectedAttributes);
    }

    [Fact]
    public void Feature_WithSameData_ShouldHaveEqualIds()
    {
        // Arrange
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("test", "value");
        var feature1 = Feature.Create(1, new byte[] { 1 }, attributes);
        var feature2 = Feature.Create(1, new byte[] { 1 }, attributes);

        // Act & Assert
        feature1.Id.Should().Be(feature2.Id);
        feature1.Attributes.Should().BeEquivalentTo(feature2.Attributes);
        feature1.Geometry.Should().BeEquivalentTo(feature2.Geometry);
    }
}