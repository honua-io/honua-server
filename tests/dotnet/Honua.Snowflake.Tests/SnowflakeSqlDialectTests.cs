// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Snowflake.Features.FeatureStore.Services;
using Honua.Snowflake.Queries.Filters;

namespace Honua.Snowflake.Tests;

/// <summary>
/// Unit tests for the Snowflake SQL dialect and identifier quoting. Confirms double-quoted,
/// case-preserving identifiers, positional parameter prefix, literal escaping, and rejection of
/// identifiers outside the safe allow-list.
/// </summary>
public class SnowflakeSqlDialectTests
{
    [Fact]
    public void Dialect_ReportsSnowflakeNameAndPositionalPrefix()
    {
        var dialect = SnowflakeSqlDialect.Instance;

        Assert.Equal("snowflake", dialect.Name);
        Assert.Equal("?", dialect.ParameterPrefix);
    }

    [Fact]
    public void QuoteIdentifier_DoubleQuotesAndPreservesCase()
    {
        var dialect = SnowflakeSqlDialect.Instance;

        Assert.Equal("\"PARCELS\"", dialect.QuoteIdentifier("PARCELS"));
        Assert.Equal("\"parcels\"", dialect.QuoteIdentifier("parcels"));
    }

    [Fact]
    public void QuoteIdentifier_EmptyOrWhitespace_Throws()
    {
        var dialect = SnowflakeSqlDialect.Instance;

        Assert.Throws<ArgumentException>(() => dialect.QuoteIdentifier(""));
        Assert.Throws<ArgumentException>(() => dialect.QuoteIdentifier("   "));
    }

    [Fact]
    public void QuoteLiteral_EscapesSingleQuotes()
    {
        // QuoteLiteral is a default interface member on ISqlDialect; call through the interface
        // to verify the default implementation is exercised correctly.
#pragma warning disable CA1859 // Use concrete type — intentional: verifying default interface member dispatch
        ISqlDialect dialect = SnowflakeSqlDialect.Instance;
#pragma warning restore CA1859

        Assert.Equal("'O''Brien'", dialect.QuoteLiteral("O'Brien"));
    }

    [Theory]
    [InlineData("OBJECTID", true)]
    [InlineData("_internal", true)]
    [InlineData("col_1", true)]
    [InlineData("1col", false)]
    [InlineData("col; DROP", false)]
    [InlineData("col-name", false)]
    [InlineData("", false)]
    public void Identifier_IsValid_MatchesAllowList(string value, bool expected)
    {
        Assert.Equal(expected, SnowflakeIdentifier.IsValid(value));
    }

    [Fact]
    public void Identifier_EnsureValid_RejectsInjection()
    {
        Assert.Throws<ArgumentException>(() => SnowflakeIdentifier.EnsureValid("x\"; DROP TABLE y", "column"));
    }
}
