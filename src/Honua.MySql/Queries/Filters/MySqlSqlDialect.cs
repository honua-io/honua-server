// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.MySql.Features.Infrastructure;

namespace Honua.MySql.Queries.Filters;

/// <summary>
/// MySQL / MariaDB implementation of <see cref="ISqlDialect"/>.
/// </summary>
/// <remarks>
/// Identifiers are backtick-quoted with embedded backticks doubled. Parameters are
/// <c>@</c>-prefixed (MySqlConnector named parameters). Quoting routes through the
/// existing <see cref="MySqlIdentifier"/> helper so all in-tree call sites converge on
/// one implementation.
/// </remarks>
internal sealed class MySqlSqlDialect : ISqlDialect
{
    /// <summary>Singleton instance — the dialect carries no per-call state.</summary>
    public static readonly MySqlSqlDialect Instance = new();

    /// <inheritdoc />
    public string Name => "mysql";

    /// <inheritdoc />
    public string ParameterPrefix => "@";

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return MySqlIdentifier.Quote(identifier);
    }

    /// <inheritdoc />
    public string QuoteLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        // MySQL also treats backslash as an escape unless NO_BACKSLASH_ESCAPES is set;
        // double both characters to be safe regardless of session SQL mode.
        var escaped = literal
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);
        return "'" + escaped + "'";
    }
}
