// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.FeatureStore.Services;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderWhereClauseTests
{
    [Fact]
    public void ParseAndParameterizeWhereClause_NumericLiteral_CastsAttributeToNumeric()
    {
        var parameters = new List<object>();
        var paramIndex = 1;

        var sql = FeatureQueryBuilder.ParseAndParameterizeWhereClause("population > 1000", ref paramIndex, parameters);

        sql.Should().Contain("NULLIF");
        sql.Should().Contain("::numeric");
        sql.Should().Contain("attributes->>'population'");
        parameters.Should().ContainSingle();
        parameters[0].Should().Be(1000m);
        paramIndex.Should().Be(2);
    }

    [Fact]
    public void ParseAndParameterizeWhereClause_QuotedNumericLiteral_DoesNotCastAttribute()
    {
        var parameters = new List<object>();
        var paramIndex = 1;

        var sql = FeatureQueryBuilder.ParseAndParameterizeWhereClause("population = '1000'", ref paramIndex, parameters);

        sql.Should().NotContain("::numeric");
        sql.Should().Contain("attributes->>'population'");
        parameters.Should().ContainSingle();
        parameters[0].Should().Be("1000");
        paramIndex.Should().Be(2);
    }
}
