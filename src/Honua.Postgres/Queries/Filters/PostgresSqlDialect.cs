// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Postgres.Queries.Filters;

/// <summary>
/// PostgreSQL / PostGIS implementation of <see cref="ISqlDialect"/>.
/// </summary>
/// <remarks>
/// Identifiers are double-quoted with embedded double quotes doubled, per the
/// ANSI / PostgreSQL convention. Parameters are <c>@</c>-prefixed (Npgsql named parameters).
/// </remarks>
internal sealed class PostgresSqlDialect : ISqlDialect
{
    /// <summary>Singleton instance — the dialect carries no per-call state.</summary>
    public static readonly PostgresSqlDialect Instance = new();

    /// <inheritdoc />
    public string Name => "postgres";

    /// <inheritdoc />
    public string ParameterPrefix => "@";

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <inheritdoc />
    public string QuoteLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return "'" + literal.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
