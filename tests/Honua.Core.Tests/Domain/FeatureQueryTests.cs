// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Domain.Features;
using Xunit;

namespace Honua.Core.Tests.Domain;

public class FeatureQueryTests
{
    [Fact]
    public void Constructor_WithDefaultValues_InitializesCorrectly()
    {
        // Act
        var query = new FeatureQuery();

        // Assert
        Assert.Null(query.Where);
        Assert.Null(query.Limit);
        Assert.Null(query.Offset);
        Assert.Null(query.SpatialFilter);
    }

    [Fact]
    public void Constructor_WithValues_InitializesCorrectly()
    {
        // Arrange
        var where = "name = 'test'";
        var limit = 10;
        var offset = 20;

        // Act
        var query = new FeatureQuery
        {
            Where = where,
            Limit = limit,
            Offset = offset
        };

        // Assert
        Assert.Equal(where, query.Where);
        Assert.Equal(limit, query.Limit);
        Assert.Equal(offset, query.Offset);
        Assert.Null(query.SpatialFilter);
    }

    [Fact]
    public void Where_Property_CanBeSetInInitializer()
    {
        // Arrange
        var whereClause = "active = true";

        // Act
        var query = new FeatureQuery { Where = whereClause };

        // Assert
        Assert.Equal(whereClause, query.Where);
    }

    [Fact]
    public void Limit_Property_CanBeSetInInitializer()
    {
        // Arrange
        var limit = 100;

        // Act
        var query = new FeatureQuery { Limit = limit };

        // Assert
        Assert.Equal(limit, query.Limit);
    }

    [Fact]
    public void Offset_Property_CanBeSetInInitializer()
    {
        // Arrange
        var offset = 50;

        // Act
        var query = new FeatureQuery { Offset = offset };

        // Assert
        Assert.Equal(offset, query.Offset);
    }
}