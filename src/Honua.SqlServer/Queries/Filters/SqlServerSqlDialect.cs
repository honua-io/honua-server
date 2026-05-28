// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.SqlServer.Features.FeatureStore.Services;

namespace Honua.SqlServer.Queries.Filters;

/// <summary>
/// SQL Server / Azure SQL implementation of <see cref="ISqlDialect"/>.
/// </summary>
/// <remarks>
/// Identifiers are bracket-quoted (<c>[…]</c>) with embedded closing brackets doubled.
/// The provider deliberately restricts identifiers to the regular form
/// (<c>[A-Za-z_][A-Za-z0-9_]*</c>) — see <see cref="SqlServerIdentifier"/> — so that
/// configured names are unambiguous and SQL-injection-safe. Parameters are
/// <c>@</c>-prefixed (Microsoft.Data.SqlClient named parameters).
/// </remarks>
internal sealed class SqlServerSqlDialect : ISqlDialect
{
    /// <summary>Singleton instance — the dialect carries no per-call state.</summary>
    public static readonly SqlServerSqlDialect Instance = new();

    /// <inheritdoc />
    public string Name => "sqlserver";

    /// <inheritdoc />
    public string ParameterPrefix => "@";

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return SqlServerIdentifier.Quote(identifier);
    }

    /// <inheritdoc />
    public string QuoteLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return "'" + literal.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
