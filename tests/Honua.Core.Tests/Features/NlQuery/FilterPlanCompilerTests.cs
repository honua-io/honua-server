// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.NlQuery.Domain;
using Honua.Core.Features.NlQuery.Services;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.NlQuery;

[Protocol(Protocols.TestQuality)]
public sealed class FilterPlanCompilerTests
{
    private readonly LayerDefinition _testLayer;

    public FilterPlanCompilerTests()
    {
        _testLayer = new LayerDefinition(
            Id: 1,
            Name: "test_layer",
            Description: "Test layer for NL query compilation",
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.WGS84,
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 100),
                new FieldDefinition("population", FieldType.Integer),
                new FieldDefinition("height", FieldType.Double),
                new FieldDefinition("category", FieldType.String, Length: 50),
                new FieldDefinition("active", FieldType.Boolean),
                new FieldDefinition("created_at", FieldType.DateTime),
                new FieldDefinition("shape", FieldType.Geometry)
            ]);
    }

    // --- Comparison clause tests ---

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_ComparisonStringEq_ReturnsBinaryExpression()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{ "type": "comparison", "comparison": { "property": "name", "operator": "eq", "value": "Portland" } }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var binary = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        binary.Operator.Should().Be(BinaryOperator.Equal);
        binary.Left.Should().BeOfType<PropertyReference>().Which.PropertyName.Should().Be("name");
        binary.Right.Should().BeOfType<Literal>().Which.Value.Should().Be("Portland");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_ComparisonNumericGt_ReturnsBinaryExpression()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{ "type": "comparison", "comparison": { "property": "population", "operator": "gt", "value": 50000 } }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var binary = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        binary.Operator.Should().Be(BinaryOperator.GreaterThan);
        binary.Left.Should().BeOfType<PropertyReference>().Which.PropertyName.Should().Be("population");
        binary.Right.Should().BeOfType<Literal>().Which.Type.Should().Be(LiteralType.Number);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_ComparisonBoolean_ReturnsBinaryExpression()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{ "type": "comparison", "comparison": { "property": "active", "operator": "eq", "value": true } }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var binary = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        binary.Right.Should().BeOfType<Literal>().Which.Type.Should().Be(LiteralType.Boolean);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_ComparisonInList_ReturnsBinaryWithValueList()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{ "type": "comparison", "comparison": { "property": "category", "operator": "in", "value": ["park", "recreation", "garden"] } }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "unknown error");
        var binary = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        binary.Operator.Should().Be(BinaryOperator.In);
        var valueList = binary.Right.Should().BeOfType<ValueList>().Subject;
        valueList.Values.Should().HaveCount(3);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_ComparisonLike_ReturnsBinaryExpression()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{ "type": "comparison", "comparison": { "property": "name", "operator": "like", "value": "%Park%" } }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var binary = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        binary.Operator.Should().Be(BinaryOperator.Like);
    }

    // --- Spatial clause tests ---

    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void Compile_SpatialIntersectsPolygon_ReturnsSpatialPredicate()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "spatial",
            "spatial": {
              "operator": "intersects",
              "geometry": { "type": "Polygon", "coordinates": [[[-122.5, 37.7], [-122.3, 37.7], [-122.3, 37.9], [-122.5, 37.9], [-122.5, 37.7]]] }
            }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var spatial = result.Expression.Should().BeOfType<SpatialPredicate>().Subject;
        spatial.Operator.Should().Be(SpatialOperator.Intersects);
        spatial.Left.Should().BeOfType<PropertyReference>().Which.PropertyName.Should().Be("shape");
        spatial.Right.Should().BeOfType<GeometryLiteral>();
    }

    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void Compile_SpatialDWithinPoint_ReturnsSpatialDistancePredicate()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "spatial",
            "spatial": {
              "operator": "dwithin",
              "geometry": { "type": "Point", "coordinates": [-122.6765, 45.5231] },
              "distance": 5,
              "distanceUnit": "kilometers"
            }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var spatial = result.Expression.Should().BeOfType<SpatialDistancePredicate>().Subject;
        spatial.Operator.Should().Be(SpatialOperator.DWithin);
        spatial.Left.Should().BeOfType<PropertyReference>().Which.PropertyName.Should().Be("shape");
        spatial.Right.Should().BeOfType<GeometryLiteral>();
        var distance = spatial.Distance.Should().BeOfType<Literal>().Subject;
        distance.Type.Should().Be(LiteralType.Number);
        ((double)distance.Value!).Should().Be(5000.0); // 5 km converted to meters
    }

    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void Compile_SpatialWithinPolygon_ReturnsSpatialPredicate()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "spatial",
            "spatial": {
              "operator": "within",
              "geometry": { "type": "Polygon", "coordinates": [[[-122.5, 37.7], [-122.3, 37.7], [-122.3, 37.9], [-122.5, 37.9], [-122.5, 37.7]]] }
            }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var spatial = result.Expression.Should().BeOfType<SpatialPredicate>().Subject;
        spatial.Operator.Should().Be(SpatialOperator.Within);
    }

    // --- Temporal clause tests ---

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_TemporalAfter_ReturnsTemporalPredicate()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "temporal",
            "temporal": { "property": "created_at", "operator": "after", "start": "2025-01-01T00:00:00Z" }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var temporal = result.Expression.Should().BeOfType<TemporalPredicate>().Subject;
        temporal.Operator.Should().Be(TemporalOperator.After);
        temporal.Left.Should().BeOfType<PropertyReference>().Which.PropertyName.Should().Be("created_at");
        temporal.Right.Should().BeOfType<Literal>().Which.Type.Should().Be(LiteralType.DateTime);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_TemporalBefore_ReturnsTemporalPredicate()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "temporal",
            "temporal": { "property": "created_at", "operator": "before", "end": "2025-12-31T23:59:59Z" }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var temporal = result.Expression.Should().BeOfType<TemporalPredicate>().Subject;
        temporal.Operator.Should().Be(TemporalOperator.Before);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_TemporalDuring_ReturnsTemporalPredicateWithInterval()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "temporal",
            "temporal": { "property": "created_at", "operator": "during", "start": "2025-01-01T00:00:00Z", "end": "2025-12-31T23:59:59Z" }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var temporal = result.Expression.Should().BeOfType<TemporalPredicate>().Subject;
        temporal.Operator.Should().Be(TemporalOperator.During);
        temporal.Right.Should().BeOfType<IntervalLiteral>();
    }

    // --- Nested clause tests ---

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_NestedOrInsideAnd_ReturnsNestedBinaryExpressions()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [
            { "type": "comparison", "comparison": { "property": "population", "operator": "gt", "value": 10000 } },
            { "type": "nested", "nested": {
              "combinator": "or",
              "clauses": [
                { "type": "comparison", "comparison": { "property": "category", "operator": "eq", "value": "park" } },
                { "type": "comparison", "comparison": { "property": "category", "operator": "eq", "value": "garden" } }
              ]
            }}
          ]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        // Top level: AND(population > 10000, OR(category = park, category = garden))
        var topAnd = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        topAnd.Operator.Should().Be(BinaryOperator.And);
        topAnd.Left.Should().BeOfType<BinaryExpression>().Which.Operator.Should().Be(BinaryOperator.GreaterThan);
        var nestedOr = topAnd.Right.Should().BeOfType<BinaryExpression>().Subject;
        nestedOr.Operator.Should().Be(BinaryOperator.Or);
    }

    // --- Combined clause tests ---

    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void Compile_CombinedSpatialAndComparison_ReturnsCombinedExpression()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [
            { "type": "comparison", "comparison": { "property": "height", "operator": "gt", "value": 50 } },
            { "type": "spatial", "spatial": {
              "operator": "intersects",
              "geometry": { "type": "Polygon", "coordinates": [[[-122.5, 37.7], [-122.3, 37.7], [-122.3, 37.9], [-122.5, 37.9], [-122.5, 37.7]]] }
            }}
          ]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var and = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        and.Operator.Should().Be(BinaryOperator.And);
        and.Left.Should().BeOfType<BinaryExpression>(); // height > 50
        and.Right.Should().BeOfType<SpatialPredicate>(); // intersects
    }

    // --- Error cases ---

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_InvalidPropertyName_ReturnsFailure()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{ "type": "comparison", "comparison": { "property": "nonexistent", "operator": "eq", "value": "test" } }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("nonexistent");
        result.ErrorMessage.Should().Contain("does not exist");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_UnsupportedOperator_ReturnsFailure()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{ "type": "comparison", "comparison": { "property": "name", "operator": "regex", "value": ".*" } }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("regex");
    }

    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void Compile_MalformedGeoJson_ReturnsFailure()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "spatial",
            "spatial": {
              "operator": "intersects",
              "geometry": { "type": "InvalidType", "coordinates": [] }
            }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("GeoJSON");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_EmptyClauses_ReturnsFailure()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": []
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no clauses");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_UnknownClauseType_ReturnsFailure()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{ "type": "unknown_type" }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("unknown_type");
    }

    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void Compile_DWithinWithoutDistance_ReturnsFailure()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "spatial",
            "spatial": {
              "operator": "dwithin",
              "geometry": { "type": "Point", "coordinates": [-122.6765, 45.5231] }
            }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Distance");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_SpatialOnLayerWithoutGeometry_ReturnsFailure()
    {
        var noGeomLayer = new LayerDefinition(
            Id: 2,
            Name: "no_geom",
            Description: null,
            GeometryType: GeometryType.None,
            SpatialReference: SpatialReference.WGS84,
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 100)
            ]);

        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "spatial",
            "spatial": {
              "operator": "intersects",
              "geometry": { "type": "Point", "coordinates": [0, 0] }
            }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, noGeomLayer);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("geometry field");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_DistanceUnitConversion_ConvertsToMeters()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "spatial",
            "spatial": {
              "operator": "dwithin",
              "geometry": { "type": "Point", "coordinates": [0, 0] },
              "distance": 1,
              "distanceUnit": "miles"
            }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var spatial = result.Expression.Should().BeOfType<SpatialDistancePredicate>().Subject;
        var distance = spatial.Distance.Should().BeOfType<Literal>().Subject;
        ((double)distance.Value!).Should().BeApproximately(1609.344, 0.001);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Compile_OrCombinator_ProducesOrExpression()
    {
        var plan = Deserialize("""
        {
          "combinator": "or",
          "clauses": [
            { "type": "comparison", "comparison": { "property": "name", "operator": "eq", "value": "A" } },
            { "type": "comparison", "comparison": { "property": "name", "operator": "eq", "value": "B" } }
          ]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _testLayer);

        result.IsSuccess.Should().BeTrue();
        var or = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        or.Operator.Should().Be(BinaryOperator.Or);
    }

    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void Compile_SpatialOnNon4326Layer_GeoJsonDefaultsTo4326()
    {
        // Regression: untagged GeoJSON must default to EPSG:4326 per RFC 7946,
        // not the layer SRID, so the downstream pipeline can ST_Transform correctly.
        var webMercatorLayer = new LayerDefinition(
            Id: 3,
            Name: "mercator_layer",
            Description: null,
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.WebMercator,
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 100),
                new FieldDefinition("shape", FieldType.Geometry)
            ]);

        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "spatial",
            "spatial": {
              "operator": "intersects",
              "geometry": { "type": "Point", "coordinates": [-122.6765, 45.5231] }
            }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, webMercatorLayer);

        result.IsSuccess.Should().BeTrue();
        var spatial = result.Expression.Should().BeOfType<SpatialPredicate>().Subject;
        var geom = spatial.Right.Should().BeOfType<GeometryLiteral>().Subject;
        geom.Srid.Should().Be(4326, "untagged GeoJSON defaults to WGS 84 regardless of layer SRID");
    }

    private static FilterPlan Deserialize(string json)
    {
        return JsonSerializer.Deserialize<FilterPlan>(json)
            ?? throw new InvalidOperationException("Failed to deserialize filter plan fixture.");
    }
}
