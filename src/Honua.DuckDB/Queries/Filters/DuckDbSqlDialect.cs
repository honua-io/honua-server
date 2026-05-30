// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.DuckDB.Features.Infrastructure;

namespace Honua.DuckDB.Queries.Filters;

/// <summary>
/// DuckDB implementation of <see cref="ISqlDialect"/>.
/// </summary>
/// <remarks>
/// DuckDB follows the ANSI convention: identifiers are double-quoted with embedded
/// double quotes doubled. Parameters use <c>$</c>-prefixed positional placeholders
/// (DuckDB.NET binds them by 1-based index).
/// </remarks>
internal sealed class DuckDbSqlDialect : ISqlDialect
{
    /// <summary>Singleton instance — the dialect carries no per-call state.</summary>
    public static readonly DuckDbSqlDialect Instance = new();

    /// <inheritdoc />
    public string Name => "duckdb";

    /// <inheritdoc />
    public string ParameterPrefix => "$";

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        // Route through the existing helper so any call-site quirks live in one place.
        return DuckDBExternalSourceSql.QuoteIdentifier(identifier);
    }

    /// <inheritdoc />
    public string QuoteLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return DuckDBExternalSourceSql.QuoteLiteral(literal);
    }
}
