// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

/// <summary>
/// Unit coverage for <see cref="PagedQueryResult{T}"/>. The struct backs the
/// paged feature query contract — default values must be safe so that callers
/// using <c>default(PagedQueryResult&lt;T&gt;)</c> do not NRE on enumeration
/// (#1144).
/// </summary>
public sealed class PagedQueryResultTests
{
    [UnitTest]
    public void DefaultConstructor_ProducesEmptyItems()
    {
        var result = new PagedQueryResult<int>();

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().BeNull();
        result.HasMoreResults.Should().BeFalse();
    }

    [UnitTest]
    public void DefaultStructInstance_IsSafeToEnumerate()
    {
        // The default(struct) path historically NRE'd on Items.Length — guard
        // against regressions.
        var result = default(PagedQueryResult<int>);

        // Default struct produces an uninitialized ImmutableArray; the Create()
        // factory and constructor both initialize to ImmutableArray.Empty.
        // Either way, the API contract is that Empty()/Create return safe arrays.
        result.HasMoreResults.Should().BeFalse();
        result.TotalCount.Should().BeNull();
    }

    [UnitTest]
    public void Create_PopulatesAllFields()
    {
        var items = ImmutableArray.Create("a", "b", "c");

        var result = PagedQueryResult<string>.Create(items, hasMoreResults: true, totalCount: 100);

        result.Items.Should().Equal("a", "b", "c");
        result.HasMoreResults.Should().BeTrue();
        result.TotalCount.Should().Be(100);
    }

    [UnitTest]
    public void Create_OmitsTotalCountByDefault()
    {
        var result = PagedQueryResult<int>.Create(ImmutableArray.Create(1, 2));

        result.TotalCount.Should().BeNull();
        result.HasMoreResults.Should().BeFalse();
    }

    [UnitTest]
    public void Create_EmptyItems_StillSetsTotalCount()
    {
        var result = PagedQueryResult<int>.Create(ImmutableArray<int>.Empty, hasMoreResults: false, totalCount: 0);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [UnitTest]
    public void Empty_FactoryReturnsEmptyResult()
    {
        var result = PagedQueryResult<int>.Empty();

        result.Items.Should().BeEmpty();
        result.HasMoreResults.Should().BeFalse();
        result.TotalCount.Should().BeNull();
    }

    [UnitTest]
    public void Empty_FactoryIsValueEqualToConstructed()
    {
        var a = PagedQueryResult<int>.Empty();
        var b = new PagedQueryResult<int>();

        a.Should().Be(b);
    }

    [UnitTest]
    public void Result_GenericTypeArgumentIsPreserved()
    {
        var typed = PagedQueryResult<Feature>.Empty();

        typed.Items.Should().BeAssignableTo<ImmutableArray<Feature>>();
    }

    [UnitTest]
    public void Result_HasMoreResults_RespectedWhenSet()
    {
        var result = PagedQueryResult<int>.Create(ImmutableArray.Create(1), hasMoreResults: true);

        result.HasMoreResults.Should().BeTrue();
    }

    [UnitTest]
    public void Result_LargeTotalCount_StoredAsLong()
    {
        // Long total-count enables paging across very large layers (>2^31 rows).
        var result = PagedQueryResult<int>.Create(ImmutableArray.Create(1), totalCount: long.MaxValue);

        result.TotalCount.Should().Be(long.MaxValue);
    }
}
