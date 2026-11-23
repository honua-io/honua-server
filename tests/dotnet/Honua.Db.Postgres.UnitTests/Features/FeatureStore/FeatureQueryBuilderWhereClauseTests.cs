// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[global::Xunit.Trait("Tier", "Fast")]
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
        parameters.Should().HaveCount(1);
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
        parameters.Should().HaveCount(1);
        parameters[0].Should().Be("1000");
        paramIndex.Should().Be(2);
    }

    [Fact]
    public void ParseAndParameterizeWhereClause_StacCandidateInWhere_UsesPostgresParameters()
    {
        var parameters = new List<object>();
        var paramIndex = 1;

        var sql = FeatureQueryBuilder.ParseAndParameterizeWhereClause(
            "stac_id IN ('first', 'second')", ref paramIndex, parameters);

        sql.Should().Contain("attributes->>'stac_id'");
        sql.Should().Contain("IN ($1, $2)");
        parameters.Should().Equal("first", "second");
        paramIndex.Should().Be(3);
    }

    [Fact]
    public void ParseAndParameterizeWhereClause_CompoundAnd_ParameterizesEveryTerm()
    {
        var parameters = new List<object>();
        var paramIndex = 1;

        var sql = FeatureQueryBuilder.ParseAndParameterizeWhereClause(
            "category = 'drainage' AND status <> 'exercise_closed' and priority <= 94", ref paramIndex, parameters);

        sql.Should().Contain("attributes->>'category'")
            .And.Contain("attributes->>'status'")
            .And.Contain("attributes->>'priority'");
        parameters.Should().Equal("drainage", "exercise_closed", 94m);
        paramIndex.Should().Be(4);
    }

    [Fact]
    public void ParseAndParameterizeWhereClause_AndInsideLiteral_StaysInOneTerm()
    {
        var parameters = new List<object>();
        var paramIndex = 1;

        FeatureQueryBuilder.ParseAndParameterizeWhereClause(
            "name = 'Salt AND Pepper' AND status = 'open'", ref paramIndex, parameters);

        parameters.Should().Equal("Salt AND Pepper", "open");
    }
}
