// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Redshift.Features.FeatureStore.Services;

namespace Honua.Redshift.Queries.Filters;

/// <summary>
/// Amazon Redshift implementation of <see cref="ISqlDialect"/>.
/// </summary>
/// <remarks>
/// Redshift speaks the PostgreSQL wire protocol and uses ANSI double-quote (<c>"…"</c>)
/// identifier quoting with embedded quotes doubled. The provider deliberately restricts
/// identifiers to the regular form (<c>[A-Za-z_][A-Za-z0-9_]*</c>) — see
/// <see cref="RedshiftIdentifier"/> — so configured names are unambiguous and
/// SQL-injection-safe. Parameters are <c>@</c>-prefixed (Npgsql named parameters).
/// </remarks>
internal sealed class RedshiftSqlDialect : ISqlDialect
{
    /// <summary>Singleton instance — the dialect carries no per-call state.</summary>
    public static readonly RedshiftSqlDialect Instance = new();

    /// <inheritdoc />
    public string Name => "redshift";

    /// <inheritdoc />
    public string ParameterPrefix => "@";

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return RedshiftIdentifier.Quote(identifier);
    }
}
