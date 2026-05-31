// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Oracle.Features.FeatureStore.Services;

namespace Honua.Oracle.Queries.Filters;

/// <summary>
/// Oracle implementation of <see cref="ISqlDialect"/>.
/// </summary>
/// <remarks>
/// Identifiers are double-quoted (<c>"…"</c>) and case-preserving. Configured identifiers are
/// restricted to the regular form (<c>[A-Za-z_][A-Za-z0-9_]*</c>) — see
/// <see cref="OracleIdentifier"/> — so configured names are unambiguous and SQL-injection-safe.
/// Parameters are colon-prefixed (ODP.NET named bind parameters with <c>BindByName = true</c>).
/// </remarks>
internal sealed class OracleSqlDialect : ISqlDialect
{
    /// <summary>Singleton instance — the dialect carries no per-call state.</summary>
    public static readonly OracleSqlDialect Instance = new();

    /// <inheritdoc />
    public string Name => "oracle";

    /// <inheritdoc />
    public string ParameterPrefix => ":";

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return OracleIdentifier.Quote(identifier);
    }

    /// <inheritdoc />
    public string QuoteLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return "'" + literal.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
