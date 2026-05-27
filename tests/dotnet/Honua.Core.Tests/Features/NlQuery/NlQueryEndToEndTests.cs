// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.NlQuery.Domain;
using Honua.Core.Features.NlQuery.Services;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.NlQuery;

/// <summary>
/// End-to-end tests verifying that realistic NL query filter plans compile
/// correctly into the expected FilterExpression AST. These represent the 10
/// acceptance criteria test cases for ticket #343.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class NlQueryEndToEndTests
{
    private readonly MetadataV2Resource _parcelsResource;
    private readonly MetadataV2Resource _sensorResource;

    public NlQueryEndToEndTests()
    {
        _parcelsResource = CreateResource(
            "parcels",
            MetadataV2GeometryType.Polygon,
            [
                Field("objectid", MetadataV2FieldType.Integer, nullable: false, roles: ["id.primary"]),
                Field("parcel_id", MetadataV2FieldType.String, length: 20),
                Field("owner_name", MetadataV2FieldType.String, length: 200),
                Field("zoning", MetadataV2FieldType.String, length: 10),
                Field("assessed_value", MetadataV2FieldType.Double),
                Field("area_sqft", MetadataV2FieldType.Double),
                Field("year_built", MetadataV2FieldType.Integer),
                Field("last_sale_date", MetadataV2FieldType.DateTime),
                Field("is_vacant", MetadataV2FieldType.Boolean),
                Field("shape", MetadataV2FieldType.Geometry, roles: ["geometry.primary"])
            ],
            description: "Land parcels with zoning and assessment data");

        _sensorResource = CreateResource(
            "air_quality_sensors",
            MetadataV2GeometryType.Point,
            [
                Field("objectid", MetadataV2FieldType.Integer, nullable: false, roles: ["id.primary"]),
                Field("sensor_id", MetadataV2FieldType.String, length: 50),
                Field("station_name", MetadataV2FieldType.String, length: 100),
                Field("pollutant", MetadataV2FieldType.String, length: 20),
                Field("aqi_value", MetadataV2FieldType.Integer),
                Field("reading_time", MetadataV2FieldType.DateTime),
                Field("is_active", MetadataV2FieldType.Boolean),
                Field("shape", MetadataV2FieldType.Geometry, roles: ["geometry.primary"])
            ],
            description: "Air quality monitoring sensors");
    }

    /// <summary>
    /// NL: "Show me all vacant parcels zoned commercial"
    /// Expected: is_vacant = true AND zoning = 'C' (multi-clause comparison)
    /// </summary>
    [UnitTest]
    [Operation(Operations.Query)]
    public void E2E_01_VacantCommercialParcels_CompilesToBoolAndStringComparison()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [
            { "type": "comparison", "comparison": { "property": "is_vacant", "operator": "eq", "value": true } },
            { "type": "comparison", "comparison": { "property": "zoning", "operator": "eq", "value": "C" } }
          ]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _parcelsResource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var and = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        and.Operator.Should().Be(BinaryOperator.And);
        and.Left.Should().BeOfType<BinaryExpression>().Which.Left
            .Should().BeOfType<PropertyReference>().Which.PropertyName.Should().Be("is_vacant");
        and.Right.Should().BeOfType<BinaryExpression>().Which.Right
            .Should().BeOfType<Literal>().Which.Value.Should().Be("C");
    }

    /// <summary>
    /// NL: "Find parcels worth more than $500,000 that were built before 1990"
    /// Expected: assessed_value &gt; 500000 AND year_built &lt; 1990
    /// </summary>
    [UnitTest]
    [Operation(Operations.Query)]
    public void E2E_02_HighValueOldParcels_CompilesToNumericComparisons()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [
            { "type": "comparison", "comparison": { "property": "assessed_value", "operator": "gt", "value": 500000 } },
            { "type": "comparison", "comparison": { "property": "year_built", "operator": "lt", "value": 1990 } }
          ]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _parcelsResource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var and = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        and.Operator.Should().Be(BinaryOperator.And);
        var left = and.Left.Should().BeOfType<BinaryExpression>().Subject;
        left.Operator.Should().Be(BinaryOperator.GreaterThan);
        var right = and.Right.Should().BeOfType<BinaryExpression>().Subject;
        right.Operator.Should().Be(BinaryOperator.LessThan);
    }

    /// <summary>
    /// NL: "Show parcels within downtown Portland"
    /// Expected: spatial intersects with a polygon envelope
    /// </summary>
    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void E2E_03_ParcelsWithinPolygon_CompilesToSpatialIntersects()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "spatial",
            "spatial": {
              "operator": "intersects",
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[-122.685, 45.510], [-122.655, 45.510], [-122.655, 45.530], [-122.685, 45.530], [-122.685, 45.510]]]
              }
            }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _parcelsResource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var spatial = result.Expression.Should().BeOfType<SpatialPredicate>().Subject;
        spatial.Operator.Should().Be(SpatialOperator.Intersects);
        spatial.Right.Should().BeOfType<GeometryLiteral>().Which.Srid.Should().Be(4326);
    }

    /// <summary>
    /// NL: "Find parcels sold after January 2024 near the airport"
    /// Expected: temporal after + spatial dwithin (combined)
    /// </summary>
    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void E2E_04_RecentSalesNearAirport_CompilesToTemporalPlusSpatial()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [
            {
              "type": "temporal",
              "temporal": { "property": "last_sale_date", "operator": "after", "start": "2024-01-01T00:00:00Z" }
            },
            {
              "type": "spatial",
              "spatial": {
                "operator": "dwithin",
                "geometry": { "type": "Point", "coordinates": [-122.5975, 45.5886] },
                "distance": 3,
                "distanceUnit": "km"
              }
            }
          ]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _parcelsResource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var and = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        and.Operator.Should().Be(BinaryOperator.And);
        and.Left.Should().BeOfType<TemporalPredicate>();
        var dwithin = and.Right.Should().BeOfType<SpatialDistancePredicate>().Subject;
        dwithin.Operator.Should().Be(SpatialOperator.DWithin);
        var dist = dwithin.Distance.Should().BeOfType<Literal>().Subject;
        ((double)dist.Value!).Should().Be(3000.0); // 3 km → meters
    }

    /// <summary>
    /// NL: "Show parcels zoned residential or mixed-use"
    /// Expected: zoning IN ['R', 'MU'] via OR combinator or IN operator
    /// </summary>
    [UnitTest]
    [Operation(Operations.Query)]
    public void E2E_05_MultiZoning_CompilesToInList()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "comparison",
            "comparison": { "property": "zoning", "operator": "in", "value": ["R", "MU"] }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _parcelsResource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var binary = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        binary.Operator.Should().Be(BinaryOperator.In);
        binary.Right.Should().BeOfType<ValueList>().Which.Values.Should().HaveCount(2);
    }

    /// <summary>
    /// NL: "Find large parcels over 10,000 sqft that are either residential or commercial"
    /// Expected: area_sqft > 10000 AND (zoning = 'R' OR zoning = 'C') — nested OR
    /// </summary>
    [UnitTest]
    [Operation(Operations.Query)]
    public void E2E_06_LargeParcelsWithNestedZoning_CompilesToNestedOr()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [
            { "type": "comparison", "comparison": { "property": "area_sqft", "operator": "gt", "value": 10000 } },
            { "type": "nested", "nested": {
              "combinator": "or",
              "clauses": [
                { "type": "comparison", "comparison": { "property": "zoning", "operator": "eq", "value": "R" } },
                { "type": "comparison", "comparison": { "property": "zoning", "operator": "eq", "value": "C" } }
              ]
            }}
          ]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _parcelsResource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var and = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        and.Operator.Should().Be(BinaryOperator.And);
        var nested = and.Right.Should().BeOfType<BinaryExpression>().Subject;
        nested.Operator.Should().Be(BinaryOperator.Or);
    }

    /// <summary>
    /// NL: "Show sensors with AQI above 150 reading PM2.5"
    /// Expected: aqi_value > 150 AND pollutant = 'PM2.5'
    /// </summary>
    [UnitTest]
    [Operation(Operations.Query)]
    public void E2E_07_HighAqiSensors_CompilesToMultiFieldComparison()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [
            { "type": "comparison", "comparison": { "property": "aqi_value", "operator": "gt", "value": 150 } },
            { "type": "comparison", "comparison": { "property": "pollutant", "operator": "eq", "value": "PM2.5" } }
          ]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _sensorResource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var and = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        and.Operator.Should().Be(BinaryOperator.And);
    }

    /// <summary>
    /// NL: "Show me parcels where the owner name contains 'Smith'"
    /// Expected: owner_name LIKE '%Smith%'
    /// </summary>
    [UnitTest]
    [Operation(Operations.Query)]
    public void E2E_08_OwnerNameSearch_CompilesToLikePattern()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "comparison",
            "comparison": { "property": "owner_name", "operator": "like", "value": "%Smith%" }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _parcelsResource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var binary = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        binary.Operator.Should().Be(BinaryOperator.Like);
        binary.Right.Should().BeOfType<Literal>().Which.Value.Should().Be("%Smith%");
    }

    /// <summary>
    /// NL: "Find active sensors within 2 miles of downtown that read after March 2025"
    /// Expected: is_active = true AND spatial dwithin AND temporal after (three-clause AND)
    /// </summary>
    [UnitTest]
    [Operation(Operations.SpatialQuery)]
    public void E2E_09_ActiveSensorsNearbyRecent_CompilesToThreeClauseAnd()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [
            { "type": "comparison", "comparison": { "property": "is_active", "operator": "eq", "value": true } },
            {
              "type": "spatial",
              "spatial": {
                "operator": "dwithin",
                "geometry": { "type": "Point", "coordinates": [-122.6765, 45.5231] },
                "distance": 2,
                "distanceUnit": "miles"
              }
            },
            {
              "type": "temporal",
              "temporal": { "property": "reading_time", "operator": "after", "start": "2025-03-01T00:00:00Z" }
            }
          ]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _sensorResource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        // Three clauses with AND: ((is_active = true AND dwithin) AND reading_time after ...)
        var topAnd = result.Expression.Should().BeOfType<BinaryExpression>().Subject;
        topAnd.Operator.Should().Be(BinaryOperator.And);
        topAnd.Right.Should().BeOfType<TemporalPredicate>();
        var innerAnd = topAnd.Left.Should().BeOfType<BinaryExpression>().Subject;
        innerAnd.Operator.Should().Be(BinaryOperator.And);
        innerAnd.Right.Should().BeOfType<SpatialDistancePredicate>();
    }

    /// <summary>
    /// NL: "Find parcels sold between 2023 and 2024"
    /// Expected: temporal during with interval
    /// </summary>
    [UnitTest]
    [Operation(Operations.Query)]
    public void E2E_10_ParcelsSoldDuringPeriod_CompilesToTemporalDuring()
    {
        var plan = Deserialize("""
        {
          "combinator": "and",
          "clauses": [{
            "type": "temporal",
            "temporal": {
              "property": "last_sale_date",
              "operator": "during",
              "start": "2023-01-01T00:00:00Z",
              "end": "2024-12-31T23:59:59Z"
            }
          }]
        }
        """);

        var result = FilterPlanCompiler.Compile(plan, _parcelsResource);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var temporal = result.Expression.Should().BeOfType<TemporalPredicate>().Subject;
        temporal.Operator.Should().Be(TemporalOperator.During);
        temporal.Left.Should().BeOfType<PropertyReference>().Which.PropertyName.Should().Be("last_sale_date");
        temporal.Right.Should().BeOfType<IntervalLiteral>();
    }

    private static FilterPlan Deserialize(string json)
    {
        return JsonSerializer.Deserialize<FilterPlan>(json)
            ?? throw new InvalidOperationException("Failed to deserialize filter plan fixture.");
    }

    private static MetadataV2Resource CreateResource(
        string name,
        MetadataV2GeometryType geometryType,
        IReadOnlyList<MetadataV2Field> fields,
        string? description = null)
    {
        var geometryField = fields.FirstOrDefault(field =>
            field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography);

        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"resource:{name}",
                Name = name,
                Description = description,
            },
            SchemaFields = fields,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = geometryType,
                PrimaryGeometryField = geometryField?.Name,
            },
        };
    }

    private static MetadataV2Field Field(
        string name,
        MetadataV2FieldType type,
        bool nullable = true,
        int? length = null,
        IReadOnlyList<string>? roles = null)
        => new()
        {
            Name = name,
            Type = type,
            Nullable = nullable,
            Length = length,
            SemanticRoles = roles ?? [],
        };
}
