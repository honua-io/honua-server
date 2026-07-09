// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Protocols.OData.Services;
using Xunit;

namespace Honua.Protocols.OData.Tests.Services;

public sealed class ODataSqlFragmentMergeHelperTests
{
    [Fact]
    public void Merge_ExistingSqlFilter_ReindexesIncomingParameters()
    {
        var query = new FeatureQuery
        {
            Where = "status = 'active'",
            SqlFilter = new SqlFragment("tenant_id = @p0", new object?[] { "tenant-a" })
        };
        var incoming = new SqlFragment("name = @p0 OR category = @p1", new object?[] { "parks", "open-space" });

        var merged = ODataSqlFragmentMergeHelper.Merge(query, incoming);

        merged.Where.Should().BeNull();
        merged.SqlFilter.Should().NotBeNull();
        merged.SqlFilter!.Sql.Should().Be("(tenant_id = @p0) AND (name = @p1 OR category = @p2)");
        merged.SqlFilter.Parameters.Should().Equal("tenant-a", "parks", "open-space");
    }

    [Fact]
    public void Merge_NullIncomingFragment_PreservesExistingQuery()
    {
        var existing = new SqlFragment("tenant_id = @p0", new object?[] { "tenant-a" });
        var query = new FeatureQuery
        {
            Where = "status = 'active'",
            SqlFilter = existing
        };

        var merged = ODataSqlFragmentMergeHelper.Merge(query, sqlFragment: null);

        merged.Should().Be(query);
    }

    [Fact]
    public void Merge_NullExistingFilter_AppliesIncomingFragmentAndClearsWhere()
    {
        var query = new FeatureQuery { Where = "status = 'active'" };
        var incoming = new SqlFragment("name = @p0", new object?[] { "parks" });

        var merged = ODataSqlFragmentMergeHelper.Merge(query, incoming);

        merged.Where.Should().BeNull();
        merged.SqlFilter.Should().Be(incoming);
    }
}
