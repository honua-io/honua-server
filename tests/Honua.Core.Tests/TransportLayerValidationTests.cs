// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using NetTopologySuite.Geometries;
using Honua.Core.Models;
using Honua.Core.Transport.Converters;
using Xunit;

namespace Honua.Core.Tests.Transport;

/// <summary>
/// Validation tests for the transport layer implementation.
/// These tests verify round-trip conversion accuracy and protocol compliance.
/// </summary>
public class TransportLayerValidationTests
{
    [Fact]
    public void FeatureQuery_ToGrpcAndBack_PreservesAllProperties()
    {
        // Arrange
        var originalQuery = new FeatureQuery
        {
            Where = "Name LIKE 'Test%'",
            ObjectIds = ImmutableArray.Create(1L, 2L, 3L),
            OutFields = ImmutableArray.Create("Name", "Description", "Category"),
            ReturnGeometry = true,
            Offset = 10,
            Count = 50,
            OrderBy = "Name ASC",
            ReturnDistinct = true,
            SpatialFilter = new SpatialFilter
            {
                FilterGeometry = new GeometryFactory().CreatePoint(new Coordinate(-122.4194, 37.7749)),
                Relationship = SpatialRelationship.Within,
                BufferDistance = 1000,
                BufferUnit = DistanceUnit.Meters
            },
            Statistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    FieldName = "Population",
                    StatisticType = StatisticType.Sum,
                    OutputFieldName = "TotalPopulation"
                }
            ),
            GroupBy = ImmutableArray.Create("Category")
        };

        // Act - Convert to gRPC and back
        var grpcRequest = FeatureConverter.ToGrpcRequest(originalQuery, "test-service", 1);
        var roundTripQuery = FeatureConverter.FromGrpcRequest(grpcRequest);

        // Assert - Verify all properties are preserved
        Assert.Equal(originalQuery.Where, roundTripQuery.Where);
        Assert.Equal(originalQuery.ObjectIds, roundTripQuery.ObjectIds);
        Assert.Equal(originalQuery.OutFields, roundTripQuery.OutFields);
        Assert.Equal(originalQuery.ReturnGeometry, roundTripQuery.ReturnGeometry);
        Assert.Equal(originalQuery.Offset, roundTripQuery.Offset);
        Assert.Equal(originalQuery.Count, roundTripQuery.Count);
        Assert.Equal(originalQuery.OrderBy, roundTripQuery.OrderBy);
        Assert.Equal(originalQuery.ReturnDistinct, roundTripQuery.ReturnDistinct);

        // Verify spatial filter conversion
        Assert.NotNull(roundTripQuery.SpatialFilter);
        Assert.Equal(originalQuery.SpatialFilter.Relationship, roundTripQuery.SpatialFilter.Relationship);
        Assert.Equal(originalQuery.SpatialFilter.BufferDistance, roundTripQuery.SpatialFilter.BufferDistance);
        Assert.Equal(originalQuery.SpatialFilter.BufferUnit, roundTripQuery.SpatialFilter.BufferUnit);

        // Verify statistics conversion
        Assert.Equal(originalQuery.Statistics?.Length, roundTripQuery.Statistics?.Length);
        if (originalQuery.Statistics?.Length > 0)
        {
            var originalStat = originalQuery.Statistics.Value[0];
            var roundTripStat = roundTripQuery.Statistics!.Value[0];
            Assert.Equal(originalStat.FieldName, roundTripStat.FieldName);
            Assert.Equal(originalStat.StatisticType, roundTripStat.StatisticType);
            Assert.Equal(originalStat.OutputFieldName, roundTripStat.OutputFieldName);
        }

        Assert.Equal(originalQuery.GroupBy, roundTripQuery.GroupBy);
    }

    [Theory]
    [InlineData("POINT(-122.4194 37.7749)")]
    [InlineData("POLYGON((-122.5 37.7, -122.3 37.7, -122.3 37.8, -122.5 37.8, -122.5 37.7))")]
    [InlineData("LINESTRING(-122.5 37.7, -122.4 37.75, -122.3 37.8)")]
    public void GeometryConverter_RoundTripConversion_PreservesGeometry(string wkt)
    {
        // Arrange
        var geometryFactory = new GeometryFactory();
        var reader = new NetTopologySuite.IO.WKTReader(geometryFactory);
        var originalGeometry = reader.Read(wkt);

        // Act - Convert to gRPC and back
        var grpcGeometry = GeometryConverter.ToGrpc(originalGeometry);
        var roundTripGeometry = GeometryConverter.FromGrpc(grpcGeometry);

        // Assert - Verify geometry is preserved
        Assert.Equal(originalGeometry.GeometryType, roundTripGeometry.GeometryType);
        Assert.True(originalGeometry.EqualsExact(roundTripGeometry, 1e-10));
    }

    [Fact]
    public void SpatialReferenceConverter_RoundTripConversion_PreservesAllProperties()
    {
        // Arrange
        var originalSr = new SpatialReference
        {
            WKID = 4326,
            LatestWKID = 4326,
            WKT = "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]"
        };

        // Act - Convert to gRPC and back
        var grpcSr = SpatialReferenceConverter.ToGrpc(originalSr);
        var roundTripSr = SpatialReferenceConverter.FromGrpc(grpcSr);

        // Assert - Verify all properties are preserved
        Assert.Equal(originalSr.WKID, roundTripSr.WKID);
        Assert.Equal(originalSr.LatestWKID, roundTripSr.LatestWKID);
        Assert.Equal(originalSr.WKT, roundTripSr.WKT);
    }

    [Theory]
    [InlineData(SpatialRelationship.Intersects)]
    [InlineData(SpatialRelationship.Within)]
    [InlineData(SpatialRelationship.Contains)]
    [InlineData(SpatialRelationship.WithinDistance)]
    public void SpatialRelationshipConverter_AllValues_ConvertCorrectly(SpatialRelationship relationship)
    {
        // Arrange
        var spatialFilter = new SpatialFilter
        {
            FilterGeometry = new GeometryFactory().CreatePoint(new Coordinate(0, 0)),
            Relationship = relationship
        };

        // Act - Convert to gRPC and back
        var grpcFilter = SpatialFilterConverter.ToGrpc(spatialFilter);
        var roundTripFilter = SpatialFilterConverter.FromGrpc(grpcFilter);

        // Assert - Verify relationship is preserved
        Assert.Equal(relationship, roundTripFilter.Relationship);
    }

    [Theory]
    [InlineData(DistanceUnit.Meters)]
    [InlineData(DistanceUnit.Feet)]
    [InlineData(DistanceUnit.Kilometers)]
    [InlineData(DistanceUnit.Miles)]
    public void DistanceUnitConverter_AllValues_ConvertCorrectly(DistanceUnit unit)
    {
        // Arrange
        var spatialFilter = new SpatialFilter
        {
            FilterGeometry = new GeometryFactory().CreatePoint(new Coordinate(0, 0)),
            BufferDistance = 100,
            BufferUnit = unit
        };

        // Act - Convert to gRPC and back
        var grpcFilter = SpatialFilterConverter.ToGrpc(spatialFilter);
        var roundTripFilter = SpatialFilterConverter.FromGrpc(grpcFilter);

        // Assert - Verify unit is preserved
        Assert.Equal(unit, roundTripFilter.BufferUnit);
    }

    [Theory]
    [InlineData(StatisticType.Count)]
    [InlineData(StatisticType.Sum)]
    [InlineData(StatisticType.Average)]
    [InlineData(StatisticType.Min)]
    [InlineData(StatisticType.Max)]
    [InlineData(StatisticType.StandardDeviation)]
    [InlineData(StatisticType.Variance)]
    public void StatisticTypeConverter_AllValues_ConvertCorrectly(StatisticType statisticType)
    {
        // Arrange
        var statDefinition = new StatisticDefinition
        {
            FieldName = "TestField",
            StatisticType = statisticType,
            OutputFieldName = "TestOutput"
        };

        // Act - Convert to gRPC and back
        var grpcStat = StatisticDefinitionConverter.ToGrpc(statDefinition);
        var roundTripStat = StatisticDefinitionConverter.FromGrpc(grpcStat);

        // Assert - Verify statistic type is preserved
        Assert.Equal(statisticType, roundTripStat.StatisticType);
        Assert.Equal(statDefinition.FieldName, roundTripStat.FieldName);
        Assert.Equal(statDefinition.OutputFieldName, roundTripStat.OutputFieldName);
    }

    [Theory]
    [InlineData("string_value", "test string")]
    [InlineData("int_value", 42)]
    [InlineData("long_value", 9223372036854775807L)]
    [InlineData("double_value", 3.14159)]
    [InlineData("bool_value", true)]
    [InlineData("null_value", null)]
    public void AttributeConverter_VariousTypes_ConvertCorrectly(string testCase, object? value)
    {
        // Act - Convert to gRPC and back
        var grpcValue = AttributeConverter.ToGrpc(value);
        var roundTripValue = AttributeConverter.FromGrpc(grpcValue);

        // Assert - Verify value is preserved
        Assert.Equal(value, roundTripValue);
    }
}
