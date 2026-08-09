// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Queries.Filters;
using Xunit;

namespace Honua.Postgres.Tests.Queries.Filters;

/// <summary>
/// Pins the PostgreSQL <see cref="ISqlDialect"/> identifier-quoting and parameter-prefix
/// surface. Identifier quoting drift between providers was the SQL-injection boundary
/// risk audit C4 was closing — these tests are the regression net.
/// </summary>
public sealed class PostgresSqlDialectTests
{
    // Deliberately typed through the interface — these tests assert the ISqlDialect contract
    // (audit C4: identifier-quoting boundary) and must not silently fall back to concrete
    // members if the interface surface drifts. Suppress CA1859 here on purpose.
#pragma warning disable CA1859
    private readonly ISqlDialect _dialect = PostgresSqlDialect.Instance;
#pragma warning restore CA1859

    [Fact]
    public void Name_IsStable()
        => _dialect.Name.Should().Be("postgres");

    [Fact]
    public void ParameterPrefix_IsAtSign()
        => _dialect.ParameterPrefix.Should().Be("@");

    [Theory]
    [InlineData("geometry", "\"geometry\"")]
    [InlineData("Object ID", "\"Object ID\"")]
    public void QuoteIdentifier_WrapsInDoubleQuotes(string input, string expected)
        => _dialect.QuoteIdentifier(input).Should().Be(expected);

    [Fact]
    public void QuoteIdentifier_DoublesEmbeddedDoubleQuotes()
    {
        // Adversarial input: someone passes a configured column name containing a
        // double quote. Doubling it keeps the resulting SQL syntactically valid and
        // closes the injection path.
        _dialect.QuoteIdentifier("evil\"name").Should().Be("\"evil\"\"name\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void QuoteIdentifier_RejectsEmptyInput(string input)
    {
        var act = () => _dialect.QuoteIdentifier(input);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void QuoteLiteral_DoublesSingleQuotes()
        => _dialect.QuoteLiteral("O'Brien").Should().Be("'O''Brien'");
}
