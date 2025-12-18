// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Domain.Features;
using Xunit;

namespace Honua.Core.Tests.Domain;

public class QueryResultTests
{
    [Fact]
    public void Create_WithValidData_ReturnsQueryResult()
    {
        // Arrange
        var totalCount = 100L;
        var items = ImmutableArray.Create("item1", "item2", "item3");
        var hasMoreResults = true;

        // Act
        var result = QueryResult<string>.Create(totalCount, items, hasMoreResults);

        // Assert
        Assert.Equal(totalCount, result.TotalCount);
        Assert.Equal(items, result.Items);
        Assert.Equal(hasMoreResults, result.HasMoreResults);
    }

    [Fact]
    public void Empty_ReturnsEmptyQueryResult()
    {
        // Act
        var result = QueryResult<string>.Empty();

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.True(result.Items.IsEmpty);
        Assert.False(result.HasMoreResults);
    }

    [Fact]
    public void Create_WithEmptyItems_ReturnsQueryResultWithEmptyItems()
    {
        // Arrange
        var totalCount = 0L;
        var items = ImmutableArray<string>.Empty;
        var hasMoreResults = false;

        // Act
        var result = QueryResult<string>.Create(totalCount, items, hasMoreResults);

        // Assert
        Assert.Equal(totalCount, result.TotalCount);
        Assert.True(result.Items.IsEmpty);
        Assert.Equal(hasMoreResults, result.HasMoreResults);
    }

    [Fact]
    public void Create_WithLargeCount_HandlesLargeNumbers()
    {
        // Arrange
        var totalCount = long.MaxValue;
        var items = ImmutableArray.Create("test");
        var hasMoreResults = false;

        // Act
        var result = QueryResult<string>.Create(totalCount, items, hasMoreResults);

        // Assert
        Assert.Equal(totalCount, result.TotalCount);
        Assert.Equal(items, result.Items);
        Assert.False(result.HasMoreResults);
    }
}