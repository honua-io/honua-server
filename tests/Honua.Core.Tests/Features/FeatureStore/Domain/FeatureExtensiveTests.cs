// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using FsCheck;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.PropertyBased;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

/// <summary>
/// Extensive property-based and edge case tests for Feature domain model
/// </summary>
public class FeatureExtensiveTests
{
    [UnitTest]
    public Property Feature_CreateWithPositiveId_ShouldHaveCorrectId()
    {
        return Prop.ForAll<PositiveInt>(id =>
        {
            var feature = Feature.Create(id.Get, new byte[] { 1 });
            return feature.Id == id.Get;
        });
    }

    [UnitTest]
    public Property Feature_CreateWithAttributes_ShouldPreserveAllAttributes()
    {
        return Prop.ForAll(FeatureGenerators.ArbitraryAttributes(), attributes =>
        {
            var feature = Feature.Create(1, new byte[] { 1 }, attributes);
            return feature.Attributes.Keys.SequenceEqual(attributes.Keys) &&
                   feature.Attributes.Values.SequenceEqual(attributes.Values);
        });
    }

    [UnitTest]
    public Property Feature_CreateWithGeometry_ShouldPreserveGeometry()
    {
        return Prop.ForAll(FeatureGenerators.ArbitraryGeometry(), geometry =>
        {
            var feature = Feature.Create(1, geometry, ImmutableDictionary<string, object?>.Empty);
            if (geometry == null)
            {
                return feature.Geometry == null;
            }
            return feature.Geometry != null && feature.Geometry.SequenceEqual(geometry);
        });
    }

    [UnitTest]
    public Property Feature_WithLargeAttributes_ShouldHandleEfficiently()
    {
        return Prop.ForAll(FeatureGenerators.ArbitraryLargeAttributes(), attributes =>
        {
            var feature = Feature.Create(1, new byte[] { 1 }, attributes);
            return feature.Attributes.Count == attributes.Count;
        });
    }

    [UnitTest]
    public void Feature_WithMaxInt64Id_ShouldCreateSuccessfully()
    {
        // Arrange & Act
        var feature = Feature.Create(long.MaxValue, new byte[] { 1 });

        // Assert
        feature.Id.Should().Be(long.MaxValue);
    }

    [UnitTest]
    public void Feature_WithMinInt64Id_ShouldCreateSuccessfully()
    {
        // Arrange & Act
        var feature = Feature.Create(long.MinValue, new byte[] { 1 });

        // Assert
        feature.Id.Should().Be(long.MinValue);
    }

    [UnitTest]
    public void Feature_WithEmptyGeometry_ShouldCreateSuccessfully()
    {
        // Arrange & Act
        var feature = Feature.Create(1, Array.Empty<byte>());

        // Assert
        feature.Geometry.Should().BeEmpty();
    }

    [UnitTest]
    public void Feature_WithLargeGeometry_ShouldCreateSuccessfully()
    {
        // Arrange
        var largeGeometry = new byte[1024 * 1024]; // 1MB geometry
        new Random().NextBytes(largeGeometry);

        // Act
        var feature = Feature.Create(1, largeGeometry);

        // Assert
        feature.Geometry.Should().HaveCount(1024 * 1024);
    }

    [UnitTest]
    public void Feature_WithSpecialCharacterAttributes_ShouldPreserveThem()
    {
        // Arrange
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("unicode", "Hello 🌍 World")
            .Add("emoji", "🚀🌟💫")
            .Add("special_chars", "!@#$%^&*(){}[]|\\:;\"'<>,.?/~`")
            .Add("xml_chars", "<>&\"'")
            .Add("null_value", null);

        // Act
        var feature = Feature.Create(1, new byte[] { 1 }, attributes);

        // Assert
        feature.Attributes.Should().BeEquivalentTo(attributes);
    }

    [UnitTest]
    public void Feature_WithNumericAttributes_ShouldPreserveTypes()
    {
        // Arrange
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("int", 42)
            .Add("long", 9223372036854775807L)
            .Add("double", 3.14159)
            .Add("decimal", 123.456m)
            .Add("float", 2.71828f);

        // Act
        var feature = Feature.Create(1, new byte[] { 1 }, attributes);

        // Assert
        feature.Attributes.Should().BeEquivalentTo(attributes);
    }

    [UnitTest]
    public void Feature_WithBooleanAttributes_ShouldPreserveValues()
    {
        // Arrange
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("true_value", true)
            .Add("false_value", false);

        // Act
        var feature = Feature.Create(1, new byte[] { 1 }, attributes);

        // Assert
        feature.Attributes.Should().BeEquivalentTo(attributes);
    }

    [UnitTest]
    public void Feature_WithDateTimeAttributes_ShouldPreserveValues()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("created_at", now)
            .Add("modified_at", DateTimeOffset.UtcNow);

        // Act
        var feature = Feature.Create(1, new byte[] { 1 }, attributes);

        // Assert
        feature.Attributes.Should().BeEquivalentTo(attributes);
    }

    [UnitTest]
    public void Feature_WithComplexNestedAttributes_ShouldHandleThem()
    {
        // Arrange
        var complexValue = new Dictionary<string, object?>
        {
            ["nested"] = new Dictionary<string, object?> { ["level2"] = "value" },
            ["array"] = new[] { 1, 2, 3 },
            ["mixed"] = new object[] { "string", 42, true }
        };

        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("complex", complexValue);

        // Act
        var feature = Feature.Create(1, new byte[] { 1 }, attributes);

        // Assert
        feature.Attributes["complex"].Should().BeEquivalentTo(complexValue);
    }

    [UnitTest]
    public void Feature_WithVeryLongAttributeName_ShouldHandleIt()
    {
        // Arrange
        var longName = new string('a', 1000);
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add(longName, "value");

        // Act
        var feature = Feature.Create(1, new byte[] { 1 }, attributes);

        // Assert
        feature.Attributes.Should().ContainKey(longName);
        feature.Attributes[longName].Should().Be("value");
    }

    [UnitTest]
    public void Feature_WithVeryLongAttributeValue_ShouldHandleIt()
    {
        // Arrange
        var longValue = new string('x', 10000);
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("long_value", longValue);

        // Act
        var feature = Feature.Create(1, new byte[] { 1 }, attributes);

        // Assert
        feature.Attributes["long_value"].Should().Be(longValue);
    }

    [UnitTest]
    public void Feature_WithManyAttributes_ShouldHandleEfficiently()
    {
        // Arrange
        var attributesBuilder = ImmutableDictionary.CreateBuilder<string, object?>();
        for (int i = 0; i < 1000; i++)
        {
            attributesBuilder.Add($"attr_{i}", $"value_{i}");
        }
        var attributes = attributesBuilder.ToImmutable();

        // Act
        var feature = Feature.Create(1, new byte[] { 1 }, attributes);

        // Assert
        feature.Attributes.Should().HaveCount(1000);
        feature.Attributes["attr_500"].Should().Be("value_500");
    }
}
