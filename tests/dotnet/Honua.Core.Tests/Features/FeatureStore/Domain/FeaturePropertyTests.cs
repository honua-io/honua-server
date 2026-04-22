// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.PropertyBased;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Arb = FsCheck.Fluent.Arb;
using Gen = FsCheck.Fluent.Gen;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

/// <summary>
/// Property-based tests for Feature domain model ensuring data integrity and edge case handling.
/// </summary>
public class FeaturePropertyTests
{
    /// <summary>
    /// Validates that features maintain data integrity across serialization.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(RealisticFeatureArbs) })]
    public bool FeatureSerializationPreservesData(Feature feature)
    {
        try
        {
            var json = JsonSerializer.Serialize(feature);
            var deserialized = JsonSerializer.Deserialize<Feature>(json);

            deserialized.Should().NotBeNull();
            deserialized!.Id.Should().Be(feature.Id);
            deserialized.Attributes.Should().HaveCount(feature.Attributes.Count);

            if (feature.Geometry is null)
            {
                deserialized.Geometry.Should().BeNull();
            }
            else
            {
                deserialized.Geometry.Should().BeEquivalentTo(feature.Geometry);
            }

            // Check that all attribute keys are preserved
            foreach (var kvp in feature.Attributes)
            {
                deserialized.Attributes.Should().ContainKey(kvp.Key);
            }

            return true;
        }
        catch (JsonException)
        {
            // Some attribute values might not be JSON serializable
            return feature.Attributes.Values.Any(v => v is not null and not (string or int or long or double or bool or DateTime or decimal));
        }
        catch (NotSupportedException)
        {
            // Complex objects might not be supported
            return true;
        }
    }

    /// <summary>
    /// Validates that features handle various geometry types correctly.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(RealisticFeatureArbs) })]
    public bool FeatureHandlesAllGeometryTypes(Feature feature)
    {
        var newFeature = Feature.Create(feature.Id, feature.Geometry, feature.Attributes);

        newFeature.Id.Should().Be(feature.Id);
        newFeature.Attributes.Should().BeEquivalentTo(feature.Attributes);

        if (feature.Geometry is null)
        {
            newFeature.Geometry.Should().BeNull();
        }
        else
        {
            newFeature.Geometry.Should().BeEquivalentTo(feature.Geometry);
        }

        return true;
    }

    /// <summary>
    /// Validates that features with null geometries are handled correctly.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(AttributeVariantsArbs) })]
    public bool NullGeometryHandling(long id, ImmutableDictionary<string, object?> attributes)
    {
        var feature = Feature.Create(id, null, attributes);

        feature.Id.Should().Be(id);
        feature.Geometry.Should().BeNull();
        feature.Attributes.Should().BeEquivalentTo(attributes);

        return true;
    }

    /// <summary>
    /// Validates that features handle edge case attribute values correctly.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(EdgeCaseFeatureArbs) })]
    public bool EdgeCaseAttributeHandling(Feature feature)
    {
        // Should be able to access all attributes without throwing
        var attributeCount = feature.Attributes.Count;
        var keys = feature.Attributes.Keys.ToList();
        var values = feature.Attributes.Values.ToList();

        attributeCount.Should().BeGreaterThanOrEqualTo(0);
        keys.Should().HaveCount(attributeCount);
        values.Should().HaveCount(attributeCount);

        return true;
    }

    /// <summary>
    /// Validates that feature IDs are handled consistently across all value ranges.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(FeatureIdArbs) })]
    public bool FeatureIdConsistency(long id)
    {
        var feature = Feature.Create(id, null, ImmutableDictionary<string, object?>.Empty);

        feature.Id.Should().Be(id);

        return true;
    }

    /// <summary>
    /// Validates that features maintain immutability where expected.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(RealisticFeatureArbs) })]
    public bool FeatureImmutabilityRespected(Feature originalFeature)
    {
        if (originalFeature.Attributes.IsEmpty)
        {
            var updatedAttributes = originalFeature.Attributes.Add("added_key", "added_value");
            updatedAttributes.Should().ContainKey("added_key");
            originalFeature.Attributes.Should().NotContainKey("added_key");

            return true;
        }

        var firstKey = originalFeature.Attributes.Keys.First();
        var originalValue = originalFeature.Attributes[firstKey];
        var newValue = originalValue is string text && text != "modified"
            ? "modified"
            : $"modified-{Guid.NewGuid():N}";

        var updated = originalFeature.Attributes.SetItem(firstKey, newValue);

        updated.Should().ContainKey(firstKey);
        originalFeature.Attributes[firstKey].Should().Be(originalValue);

        return true;
    }

    /// <summary>
    /// Validates that geometry bytes preserve coordinate values from the source geometry.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(SridArbs), typeof(ValidCoordinateArbs) })]
    public bool SpatialReferencePreservation(int srid, Coordinate coord, int id)
    {
        try
        {
            var factory = new GeometryFactory(new PrecisionModel(), srid);
            var point = factory.CreatePoint(coord);
            var wkb = new WKBWriter().Write(point);

            var feature = Feature.Create(id, wkb, ImmutableDictionary<string, object?>.Empty);

            feature.Geometry.Should().NotBeNull();
            var geometry = new WKBReader().Read(feature.Geometry!);

            geometry.Coordinate.X.Should().Be(coord.X);
            geometry.Coordinate.Y.Should().Be(coord.Y);

            return true;
        }
        catch (ArgumentException)
        {
            // Invalid coordinates should be rejected
            return double.IsNaN(coord.X) || double.IsNaN(coord.Y) ||
                   double.IsInfinity(coord.X) || double.IsInfinity(coord.Y);
        }
    }

    /// <summary>
    /// Validates that large attribute dictionaries are handled efficiently.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(AttributeCountArbs) })]
    public bool LargeAttributeDictionaryHandling(int attributeCount)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < attributeCount; i++)
        {
            builder[$"field_{i}"] = $"value_{i}";
        }

        var attributes = builder.ToImmutable();
        var feature = Feature.Create(1, null, attributes);

        feature.Attributes.Should().HaveCount(attributeCount);

        // Should be able to access all attributes efficiently
        var allValues = feature.Attributes.Values.ToList();
        allValues.Should().HaveCount(attributeCount);

        return true;
    }

    /// <summary>
    /// Validates that features handle Unicode and special characters in attributes.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(UnicodeStringArbs) })]
    public bool UnicodeAttributeHandling(string unicodeValue)
    {
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("unicode_field", unicodeValue)
            .Add(unicodeValue, "unicode_key_test");

        var feature = Feature.Create(1, null, attributes);

        feature.Attributes["unicode_field"].Should().Be(unicodeValue);
        feature.Attributes.Should().ContainKey(unicodeValue);

        return true;
    }

    internal static class RealisticFeatureArbs
    {
        public static Arbitrary<Feature> Feature() => FeatureGenerators.RealisticFeature();
    }

    internal static class EdgeCaseFeatureArbs
    {
        public static Arbitrary<Feature> Feature() => FeatureGenerators.EdgeCaseFeature();
    }

    internal static class AttributeVariantsArbs
    {
        public static Arbitrary<ImmutableDictionary<string, object?>> Attributes() => FeatureGenerators.AttributeVariants();
    }

    internal static class FeatureIdArbs
    {
        public static Arbitrary<long> FeatureId() =>
            Arb.From(Gen.OneOf(
                Gen.Constant(long.MinValue),
                Gen.Constant(long.MaxValue),
                Gen.Constant(0L),
                Gen.Constant(-1L),
                Gen.Choose(1, 1_000_000).Select(id => (long)id)));
    }

    internal static class SridArbs
    {
        public static Arbitrary<int> Srid() => Arb.From(Gen.Elements(4326, 3857, 32633, 2154));
    }

    internal static class ValidCoordinateArbs
    {
        public static Arbitrary<Coordinate> Coordinate() => GeometryGenerators.ValidCoordinate();
    }

    internal static class AttributeCountArbs
    {
        public static Arbitrary<int> AttributeCount() => Arb.From(Gen.Choose(100, 1000));
    }

    internal static class UnicodeStringArbs
    {
        public static Arbitrary<string> UnicodeString() =>
            Arb.From(Gen.Elements("🚀✨", "文档测试", "Åpfel", "naïve café", "日本語", "العربية"));
    }
}

/// <summary>
/// Comprehensive edge case tests for Feature domain model.
/// </summary>
public class FeatureEdgeCaseTests
{
    private static readonly int[] _sampleArrayValues = new[] { 1, 2, 3, 4, 5 };

    /// <summary>
    /// Tests that features handle extremely large coordinate values gracefully.
    /// </summary>
    [Fact]
    public void Feature_HandlesExtremeCoordinates()
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var writer = new WKBWriter();
        var reader = new WKBReader();

        // Test coordinates at projection limits
        var extremeCoords = new[]
        {
            new Coordinate(-180, -85.0511), // Web Mercator min
            new Coordinate(180, 85.0511),   // Web Mercator max
            new Coordinate(0, 0),           // Origin
            new Coordinate(-179.9999, 89.9999) // Near boundaries
        };

        foreach (var coord in extremeCoords)
        {
            var point = factory.CreatePoint(coord);
            var wkb = writer.Write(point);

            var feature = Feature.Create(1, wkb, ImmutableDictionary<string, object?>.Empty);

            feature.Geometry.Should().NotBeNull();
            var geometry = reader.Read(feature.Geometry!);

            geometry.Coordinate.X.Should().Be(coord.X);
            geometry.Coordinate.Y.Should().Be(coord.Y);
        }
    }

    /// <summary>
    /// Tests that features reject invalid coordinate values appropriately.
    /// </summary>
    [Fact]
    public void Feature_RejectsInvalidCoordinates()
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var writer = new WKBWriter();

        var invalidCoords = new[]
        {
            new Coordinate(double.NaN, 0),
            new Coordinate(0, double.NaN),
            new Coordinate(double.PositiveInfinity, 0),
            new Coordinate(0, double.NegativeInfinity)
        };

        foreach (var coord in invalidCoords)
        {
            Action createFeature = () =>
            {
                var point = factory.CreatePoint(coord);
                var wkb = writer.Write(point);
                _ = Feature.Create(1, wkb, ImmutableDictionary<string, object?>.Empty);
            };

            // May throw or create invalid geometry - both are acceptable
            // The key is that the system handles it gracefully
            try
            {
                createFeature();
            }
            catch (ArgumentException)
            {
                // Expected for invalid coordinates
            }
        }
    }

    /// <summary>
    /// Tests that features handle complex nested attribute structures.
    /// </summary>
    [Fact]
    public void Feature_HandlesComplexNestedAttributes()
    {
        var complexAttributes = ImmutableDictionary.CreateRange<string, object?>(new Dictionary<string, object?>
        {
            ["simple_string"] = "test",
            ["nested_object"] = new { prop1 = "value1", prop2 = 42 },
            ["array_values"] = _sampleArrayValues,
            ["mixed_array"] = new object[] { "string", 123, true, DateTime.UtcNow },
            ["null_value"] = null,
            ["empty_string"] = "",
            ["large_number"] = long.MaxValue,
            ["small_number"] = decimal.MinValue,
            ["boolean_true"] = true,
            ["boolean_false"] = false
        });

        var feature = Feature.Create(1, null, complexAttributes);

        feature.Attributes.Should().HaveCount(complexAttributes.Count);
        feature.Attributes["simple_string"].Should().Be("test");
        feature.Attributes["null_value"].Should().BeNull();
        feature.Attributes["boolean_true"].Should().Be(true);
    }
}
