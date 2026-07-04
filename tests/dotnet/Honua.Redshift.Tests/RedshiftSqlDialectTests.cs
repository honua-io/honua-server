// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Redshift.Queries.Filters;

namespace Honua.Redshift.Tests;

/// <summary>
/// Ungated unit tests for the Redshift SQL dialect and provider-name normalization.
/// </summary>
public class RedshiftSqlDialectTests
{
    [Fact]
    public void Dialect_ReportsStableNameAndParameterPrefix()
    {
        var dialect = RedshiftSqlDialect.Instance;

        Assert.Equal("redshift", dialect.Name);
        Assert.Equal("@", dialect.ParameterPrefix);
    }

    [Fact]
    public void Dialect_QuoteIdentifier_DoubleQuotes()
    {
        Assert.Equal("\"name\"", RedshiftSqlDialect.Instance.QuoteIdentifier("name"));
    }

    [Fact]
    public void Dialect_QuoteLiteral_EscapesSingleQuotes()
    {
        // QuoteLiteral is a default interface member on ISqlDialect; call through the interface.
        ISqlDialect dialect = RedshiftSqlDialect.Instance;
        Assert.Equal("'O''Brien'", dialect.QuoteLiteral("O'Brien"));
    }

    [Theory]
    [InlineData("redshift")]
    [InlineData("Redshift")]
    [InlineData("amazon-redshift")]
    [InlineData("amazon_redshift")]
    [InlineData("redshiftdb")]
    [InlineData("aws-redshift")]
    public void Normalize_RedshiftAliases_ResolveToCanonicalName(string alias)
    {
        Assert.Equal(DataProviderNames.Redshift, DataProviderNames.Normalize(alias));
    }
}
