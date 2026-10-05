// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.GeoServicesSql;

namespace Honua.Core.Tests.Features.FeatureStore;

/// <summary>
/// The shared field-security seam decides whether a <c>where</c> clause consumes a masked
/// field from the field references of the parsed filter, so every operator of the filter
/// grammar (negated forms included) is covered by the same rule. Text that the shared
/// grammar does not parse can still reach a provider's own WHERE parser, so its identifier
/// tokens are checked as well (SEC-11).
/// </summary>
public sealed class MaskedFieldPredicateAstTests
{
    public static TheoryData<string, string> MaskedWhereClauses() => new()
    {
        { "equality", "secret = 'alpha'" },
        { "inequality", "secret <> 'alpha'" },
        { "like", "secret LIKE 'a%'" },
        { "not like", "secret NOT LIKE 'a%'" },
        { "not like lowercase", "secret not like 'a%'" },
        { "in", "secret IN ('alpha', 'beta')" },
        { "not in", "secret NOT IN ('alpha', 'beta')" },
        { "is null", "secret IS NULL" },
        { "is not null", "secret IS NOT NULL" },
        { "between", "secret BETWEEN 'a' AND 'b'" },
        { "not between", "secret NOT BETWEEN 'a' AND 'b'" },
        { "greater than", "secret > 'a'" },
        { "less or equal", "secret <= 'a'" },
        { "literal on the left", "'alpha' = secret" },
        { "negated predicate", "NOT (secret LIKE 'a%')" },
        { "function argument", "UPPER(secret) LIKE 'A%'" },
        { "second conjunct", "name = 'x' AND secret NOT LIKE 'a%'" },
        { "disjunction", "name = 'x' OR secret NOT LIKE 'a%'" },
        { "quoted identifier", "\"secret\" NOT LIKE 'a%'" },
        { "different case", "SECRET NOT LIKE 'a%'" },
        { "json accessor", "attributes->>'secret' NOT LIKE 'a%'" },
        { "json accessor spaced", "attributes ->> 'secret' NOT LIKE 'a%'" },
    };

    [Theory]
    [MemberData(nameof(MaskedWhereClauses))]
    public void WhereReferencingMaskedFieldIsRejectedForEveryOperator(string operatorFamily, string where)
    {
        operatorFamily.Should().NotBeNullOrWhiteSpace();
        var query = new FeatureQuery
        {
            Where = where,
            EnforcedMaskedFields = ImmutableArray.Create("secret")
        };

        var act = () => FeatureQuerySecurity.Validate(query);

        act.Should().Throw<ArgumentException>().WithMessage("*secret*masked*");
    }

    [Theory]
    [InlineData("name NOT LIKE 'secret%'")]
    [InlineData("name = 'secret'")]
    [InlineData("secretary NOT LIKE 'a%'")]
    [InlineData("name IN ('secret', 'other') AND secret_count > 3")]
    [InlineData("1=1")]
    public void WhereNotReferencingMaskedFieldIsAccepted(string where)
    {
        var query = new FeatureQuery
        {
            Where = where,
            EnforcedMaskedFields = ImmutableArray.Create("secret")
        };

        var act = () => FeatureQuerySecurity.Validate(query);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("\"secret\" NOT LIKE @p0")]
    [InlineData("\"secret\" NOT IN (@p0, @p1)")]
    [InlineData("\"secret\" BETWEEN @p0 AND @p1")]
    [InlineData("secret NOT LIKE @p0")]
    [InlineData("secret BETWEEN @p0 AND @p1")]
    public void TranslatedSqlFilterReferencingMaskedColumnIsRejected(string sql)
    {
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment(sql, ["a%", "b"]),
            EnforcedMaskedFields = ImmutableArray.Create("secret")
        };

        var act = () => FeatureQuerySecurity.Validate(query);

        act.Should().Throw<ArgumentException>().WithMessage("*secret*masked*");
    }

    [Theory]
    [MemberData(nameof(MaskedWhereClauses))]
    public void ParsedExpressionReferencingMaskedFieldIsRejected(string operatorFamily, string where)
    {
        operatorFamily.Should().NotBeNullOrWhiteSpace();
        FilterExpression expression;
        try
        {
            expression = new GeoServicesSqlParser().Parse(where);
        }
        catch (ArgumentException)
        {
            // Accessor syntax is provider-specific and outside the shared grammar; the
            // FeatureQuery.Where cases above cover it.
            return;
        }

        var act = () => FeatureQuerySecurity.ValidateFilterExpression(expression, ["secret"], "where");

        act.Should().Throw<ArgumentException>().WithMessage("*secret*masked*");
    }

    [Fact]
    public void ParsedExpressionWithoutMasksIsAccepted()
    {
        var expression = new GeoServicesSqlParser().Parse("secret NOT LIKE 'a%'");

        var act = () => FeatureQuerySecurity.ValidateFilterExpression(expression, [], "where");

        act.Should().NotThrow();
    }
}
