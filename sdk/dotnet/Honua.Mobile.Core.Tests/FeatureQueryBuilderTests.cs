// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Mobile.Core.Models;
using Honua.Mobile.Core.Querying;

namespace Honua.Mobile.Core.Tests;

/// <summary>
/// Tests for the FeatureQueryBuilder fluent interface.
/// </summary>
public class FeatureQueryBuilderTests
{
    [Fact]
    public void Create_ShouldReturnNewBuilder()
    {
        // Act
        var builder = FeatureQueryBuilder.Create();

        // Assert
        builder.Should().NotBeNull();
        var query = builder.Build();
        query.Should().NotBeNull();
    }

    [Fact]
    public void Where_ShouldSetWhereClause()
    {
        // Arrange
        const string whereClause = "STATUS = 'Active'";

        // Act
        var query = FeatureQueryBuilder.Create()
            .Where(whereClause)
            .Build();

        // Assert
        query.Where.Should().Be(whereClause);
    }

    [Fact]
    public void WithObjectIds_ShouldSetObjectIds()
    {
        // Arrange
        var objectIds = new long[] { 1, 2, 3, 5, 8 };

        // Act
        var query = FeatureQueryBuilder.Create()
            .WithObjectIds(objectIds)
            .Build();

        // Assert
        query.ObjectIds.Should().BeEquivalentTo(objectIds);
    }

    [Fact]
    public void WithFields_ShouldSetOutFields()
    {
        // Arrange
        var fields = new[] { "OBJECTID", "NAME", "STATUS" };

        // Act
        var query = FeatureQueryBuilder.Create()
            .WithFields(fields)
            .Build();

        // Assert
        query.OutFields.Should().BeEquivalentTo(fields);
    }

    [Fact]
    public void WithAllFields_ShouldSetOutFieldsToNull()
    {
        // Act
        var query = FeatureQueryBuilder.Create()
            .WithFields("FIELD1", "FIELD2") // Set some fields first
            .WithAllFields() // Then clear them
            .Build();

        // Assert
        query.OutFields.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithGeometry_ShouldSetReturnGeometry(bool returnGeometry)
    {
        // Act
        var query = FeatureQueryBuilder.Create()
            .WithGeometry(returnGeometry)
            .Build();

        // Assert
        query.ReturnGeometry.Should().Be(returnGeometry);
    }

    [Fact]
    public void WithoutGeometry_ShouldSetReturnGeometryToFalse()
    {
        // Act
        var query = FeatureQueryBuilder.Create()
            .WithoutGeometry()
            .Build();

        // Assert
        query.ReturnGeometry.Should().BeFalse();
    }

    [Fact]
    public void WithPaging_ShouldSetOffsetAndLimit()
    {
        // Arrange
        const int offset = 20;
        const int limit = 50;

        // Act
        var query = FeatureQueryBuilder.Create()
            .WithPaging(offset, limit)
            .Build();

        // Assert
        query.Offset.Should().Be(offset);
        query.Limit.Should().Be(limit);
    }

    [Fact]
    public void WithLimit_ShouldSetLimit()
    {
        // Arrange
        const int limit = 100;

        // Act
        var query = FeatureQueryBuilder.Create()
            .WithLimit(limit)
            .Build();

        // Assert
        query.Limit.Should().Be(limit);
        query.Offset.Should().BeNull();
    }

    [Fact]
    public void OrderByAsc_ShouldSetOrderBy()
    {
        // Arrange
        const string fieldName = "NAME";

        // Act
        var query = FeatureQueryBuilder.Create()
            .OrderByAsc(fieldName)
            .Build();

        // Assert
        query.OrderBy.Should().Be($"{fieldName} ASC");
    }

    [Fact]
    public void OrderByDesc_ShouldSetOrderBy()
    {
        // Arrange
        const string fieldName = "CREATED_DATE";

        // Act
        var query = FeatureQueryBuilder.Create()
            .OrderByDesc(fieldName)
            .Build();

        // Assert
        query.OrderBy.Should().Be($"{fieldName} DESC");
    }

    [Fact]
    public void Distinct_ShouldSetDistinctFlag()
    {
        // Act
        var query = FeatureQueryBuilder.Create()
            .Distinct()
            .Build();

        // Assert
        query.Distinct.Should().BeTrue();
    }

    [Fact]
    public void Intersects_ShouldSetSpatialFilter()
    {
        // Arrange
        var point = PointGeometry.Create(-122.4194, 37.7749);

        // Act
        var query = FeatureQueryBuilder.Create()
            .Intersects(point)
            .Build();

        // Assert
        query.SpatialFilter.Should().NotBeNull();
        query.SpatialFilter!.Geometry.Should().BeOfType<PointGeometry>();
        query.SpatialFilter.Relationship.Should().Be(SpatialRelationship.Intersects);
    }

    [Fact]
    public void Within_ShouldSetSpatialFilter()
    {
        // Arrange
        var point = PointGeometry.Create(-122.4194, 37.7749);

        // Act
        var query = FeatureQueryBuilder.Create()
            .Within(point)
            .Build();

        // Assert
        query.SpatialFilter.Should().NotBeNull();
        query.SpatialFilter!.Relationship.Should().Be(SpatialRelationship.Within);
    }

    [Fact]
    public void WithinDistance_ShouldSetSpatialFilterWithDistance()
    {
        // Arrange
        var point = PointGeometry.Create(-122.4194, 37.7749);
        const double distance = 1000;
        const DistanceUnit unit = DistanceUnit.Meters;

        // Act
        var query = FeatureQueryBuilder.Create()
            .WithinDistance(point, distance, unit)
            .Build();

        // Assert
        query.SpatialFilter.Should().NotBeNull();
        query.SpatialFilter!.Relationship.Should().Be(SpatialRelationship.WithinDistance);
        query.SpatialFilter.Distance.Should().Be(distance);
        query.SpatialFilter.DistanceUnit.Should().Be(unit);
    }

    [Fact]
    public void WithStatistics_ShouldSetStatistics()
    {
        // Arrange
        var statistics = new[]
        {
            new StatisticDefinition
            {
                Field = "POPULATION",
                Type = StatisticType.Sum,
                OutputFieldName = "TOTAL_POPULATION"
            },
            new StatisticDefinition
            {
                Field = "POPULATION",
                Type = StatisticType.Average,
                OutputFieldName = "AVG_POPULATION"
            }
        };

        // Act
        var query = FeatureQueryBuilder.Create()
            .WithStatistics(statistics)
            .Build();

        // Assert
        query.Statistics.Should().HaveCount(2);
        query.Statistics.Should().BeEquivalentTo(statistics);
    }

    [Fact]
    public void GroupBy_ShouldSetGroupByFields()
    {
        // Arrange
        var groupFields = new[] { "STATE", "COUNTY" };

        // Act
        var query = FeatureQueryBuilder.Create()
            .GroupBy(groupFields)
            .Build();

        // Assert
        query.GroupByFields.Should().BeEquivalentTo(groupFields);
    }

    [Fact]
    public void MethodChaining_ShouldCombineAllOptions()
    {
        // Act
        var query = FeatureQueryBuilder.Create()
            .Where("STATUS = 'Active'")
            .WithObjectIds(1, 2, 3)
            .WithFields("OBJECTID", "NAME")
            .WithGeometry(true)
            .WithLimit(50)
            .OrderByDesc("NAME")
            .Distinct()
            .Build();

        // Assert
        query.Where.Should().Be("STATUS = 'Active'");
        query.ObjectIds.Should().BeEquivalentTo(new long[] { 1, 2, 3 });
        query.OutFields.Should().BeEquivalentTo(new[] { "OBJECTID", "NAME" });
        query.ReturnGeometry.Should().BeTrue();
        query.Limit.Should().Be(50);
        query.OrderBy.Should().Be("NAME DESC");
        query.Distinct.Should().BeTrue();
    }

    [Fact]
    public void ImplicitConversion_ShouldConvertToFeatureQuery()
    {
        // Act
        FeatureQuery query = FeatureQueryBuilder.Create()
            .Where("STATUS = 'Active'")
            .WithLimit(10);

        // Assert
        query.Where.Should().Be("STATUS = 'Active'");
        query.Limit.Should().Be(10);
    }

    [Fact]
    public void BuilderIsImmutable_ShouldCreateNewInstancesForEachMethod()
    {
        // Arrange
        var builder1 = FeatureQueryBuilder.Create();

        // Act
        var builder2 = builder1.Where("STATUS = 'Active'");
        var builder3 = builder2.WithLimit(10);

        // Assert
        builder1.Should().NotBeSameAs(builder2);
        builder2.Should().NotBeSameAs(builder3);

        // Original builder should be unchanged
        builder1.Build().Where.Should().BeNull();
        builder1.Build().Limit.Should().BeNull();

        // Each builder should have cumulative changes
        builder2.Build().Where.Should().Be("STATUS = 'Active'");
        builder2.Build().Limit.Should().BeNull();

        builder3.Build().Where.Should().Be("STATUS = 'Active'");
        builder3.Build().Limit.Should().Be(10);
    }
}