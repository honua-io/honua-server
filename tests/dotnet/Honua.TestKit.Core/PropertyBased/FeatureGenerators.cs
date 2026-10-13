// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FsCheck;
using FsCheck.Fluent;
using Honua.Core.Features.FeatureStore.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

using Arb = FsCheck.Fluent.Arb;
using Gen = FsCheck.Fluent.Gen;

namespace Honua.TestKit.PropertyBased;

/// <summary>
/// Property-based generators for feature data to ensure comprehensive testing.
/// </summary>
public static class FeatureGenerators
{
    /// <summary>
    /// Generates realistic feature data with various geometry types and attribute combinations.
    /// </summary>
    public static Arbitrary<Feature> RealisticFeature() =>
        Arb.From(
            from id in Gen.Choose(1, 1000000)
            from geometry in GeometryVariants().Generator
            from attributes in AttributeVariants().Generator
            select Feature.Create(id, geometry, attributes));

    /// <summary>
    /// Generates features with edge case scenarios.
    /// </summary>
    public static Arbitrary<Feature> EdgeCaseFeature() =>
        Arb.From(
            from id in Gen.OneOf(
                Gen.Constant(int.MinValue),
                Gen.Constant(int.MaxValue),
                Gen.Constant(0),
                Gen.Constant(-1))
            from geometry in EdgeCaseGeometry().Generator
            from attributes in EdgeCaseAttributes().Generator
            select Feature.Create(id, geometry, attributes));

    /// <summary>
    /// Generates various geometry types for comprehensive testing.
    /// </summary>
    public static Arbitrary<byte[]?> GeometryVariants() =>
        Arb.From(Gen.OneOf<byte[]?>(
            // Null geometry
            Gen.Constant<byte[]?>(null),
            // Point geometries
            from coord in GeometryGenerators.ValidCoordinate().Generator
            select (byte[]?)ToWkb(CreatePoint(coord)),
            // Line geometries
            from start in GeometryGenerators.ValidCoordinate().Generator
            from end in GeometryGenerators.ValidCoordinate().Generator
            where !start.Equals2D(end)
            select (byte[]?)ToWkb(CreateLineString(start, end)),
            // Polygon geometries
            GeometryGenerators.SimplePolygon().Generator.Select(p => (byte[]?)ToWkb(p))));

    /// <summary>
    /// Generates edge case geometries including boundary conditions and invalid scenarios.
    /// </summary>
    public static Arbitrary<byte[]?> EdgeCaseGeometry() =>
        Arb.From(Gen.OneOf<byte[]?>(
            // Null
            Gen.Constant<byte[]?>(null),
            // Boundary coordinates
            from coord in GeometryGenerators.BoundaryCoordinate().Generator
            select (byte[]?)ToWkb(CreatePoint(coord)),
            // Empty geometries
            Gen.Constant<byte[]?>(ToWkb(CreateEmptyPoint())),
            Gen.Constant<byte[]?>(ToWkb(CreateEmptyLineString())),
            Gen.Constant<byte[]?>(ToWkb(CreateEmptyPolygon())),
            // Degenerate geometries
            from coord in GeometryGenerators.ValidCoordinate().Generator
            select (byte[]?)ToWkb(CreateDegenerateLineString(coord))));

    /// <summary>
    /// Generates realistic attribute dictionaries.
    /// </summary>
    public static Arbitrary<ImmutableDictionary<string, object?>> AttributeVariants() =>
        Arb.From(
            from count in Gen.Choose(1, 10)
            from attrs in Gen.ListOf(AttributePair().Generator, count)
            select ToImmutableAttributes(attrs));

    /// <summary>
    /// Generates edge case attributes including various data types and edge values.
    /// </summary>
    public static Arbitrary<ImmutableDictionary<string, object?>> EdgeCaseAttributes() =>
        Arb.From(Gen.OneOf<ImmutableDictionary<string, object?>>(
            // Empty attributes
            Gen.Constant(ImmutableDictionary<string, object?>.Empty),
            // Single attribute with edge values
            from key in Gen.Constant("test_field")
            from value in EdgeCaseValue().Generator
            select ImmutableDictionary<string, object?>.Empty.Add(key, value),
            // Large attribute set
            from count in Gen.Choose(50, 100)
            from attrs in Gen.ListOf(AttributePair().Generator, count)
            select ToImmutableAttributes(attrs.Select(kv => new KeyValuePair<string, object?>(kv.Key + Guid.NewGuid(), kv.Value)))));

    /// <summary>
    /// Generates key-value pairs for attributes.
    /// </summary>
    private static Arbitrary<KeyValuePair<string, object?>> AttributePair() =>
        Arb.From(
            from key in AttributeKey().Generator
            from value in AttributeValue().Generator
            select new KeyValuePair<string, object?>(key, value));

    /// <summary>
    /// Generates valid attribute keys.
    /// </summary>
    private static Arbitrary<string> AttributeKey() =>
        Arb.From(Gen.OneOf(
            Gen.Elements("name", "description", "status", "category", "created_at", "updated_at"),
            from length in Gen.Choose(3, 30)
            from chars in Gen.ArrayOf(Gen.Elements("abcdefghijklmnopqrstuvwxyz_0123456789".ToCharArray()), length)
            select new string(chars)));

    /// <summary>
    /// Generates various attribute value types.
    /// </summary>
    private static Arbitrary<object?> AttributeValue() =>
        Arb.From(Gen.OneOf(
            // Common types
            FilterExpressionGenerators.StringLiteral().Generator.Select(s => (object?)s),
            FilterExpressionGenerators.NumericLiteral().Generator.Select(n => (object?)n),
            Gen.Elements(true, false).Select(b => (object?)b),
            // Null
            Gen.Constant<object?>(null),
            // DateTime
            Gen.Choose(2020, 2025)
                .SelectMany(year => Gen.Choose(1, 12)
                    .SelectMany(month => Gen.Choose(1, 28)
                        .Select(day => (object?)new DateTime(year, month, day)))),
            // Arrays
            Gen.ListOf(FilterExpressionGenerators.StringLiteral().Generator)
                .Select(list => (object?)list.ToArray()),
            // Nested objects (JSON)
            Gen.Constant<object?>(new { nested = "value", count = 42 })));

    /// <summary>
    /// Generates edge case values including boundary conditions and problematic inputs.
    /// </summary>
    private static Arbitrary<object?> EdgeCaseValue() =>
        Arb.From(Gen.OneOf(
            // Null and empty
            Gen.Constant<object?>(null),
            Gen.Constant<object?>(""),
            Gen.Constant<object?>(DBNull.Value),
            // Numeric boundaries
            Gen.Constant<object?>(int.MinValue),
            Gen.Constant<object?>(int.MaxValue),
            Gen.Constant<object?>(long.MinValue),
            Gen.Constant<object?>(long.MaxValue),
            Gen.Constant<object?>(double.MinValue),
            Gen.Constant<object?>(double.MaxValue),
            Gen.Constant<object?>(double.NaN),
            Gen.Constant<object?>(double.PositiveInfinity),
            Gen.Constant<object?>(double.NegativeInfinity),
            // String edge cases
            Gen.Constant<object?>(new string('x', 10000)), // Very long string
            Gen.Constant<object?>("'; DROP TABLE users; --"), // SQL injection
            Gen.Constant<object?>("<script>alert('xss')</script>"), // XSS
            Gen.Constant<object?>("../../../etc/passwd"), // Path traversal
            Gen.Constant<object?>("🚀✨🌟"), // Unicode
            Gen.Constant<object?>(DateTime.MinValue),
            Gen.Constant<object?>(DateTime.MaxValue),
            Gen.Constant<object?>(Enumerable.Range(1, 1000).ToArray())));

    private static ImmutableDictionary<string, object?> ToImmutableAttributes(IEnumerable<KeyValuePair<string, object?>> attributes)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    private static byte[] ToWkb(Geometry geometry) => new WKBWriter().Write(geometry);

    private static readonly GeometryFactory _factory = new(new PrecisionModel(), 4326);

    private static Point CreatePoint(Coordinate coord) => _factory.CreatePoint(coord);
    private static LineString CreateLineString(Coordinate start, Coordinate end) =>
        _factory.CreateLineString(new[] { start, end });
    private static Point CreateEmptyPoint() => _factory.CreatePoint();
    private static LineString CreateEmptyLineString() => _factory.CreateLineString();
    private static Polygon CreateEmptyPolygon() => _factory.CreatePolygon();
    private static LineString CreateDegenerateLineString(Coordinate coord) =>
        _factory.CreateLineString(new[] { coord, coord });
}
