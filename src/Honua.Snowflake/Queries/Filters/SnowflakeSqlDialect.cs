// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Snowflake.Features.FeatureStore.Services;

namespace Honua.Snowflake.Queries.Filters;

/// <summary>
/// Snowflake implementation of <see cref="ISqlDialect"/>.
/// </summary>
/// <remarks>
/// Identifiers are double-quoted (<c>"…"</c>) with embedded double-quotes doubled. The provider
/// deliberately restricts identifiers to the regular form (<c>[A-Za-z_][A-Za-z0-9_]*</c>) — see
/// <see cref="SnowflakeIdentifier"/> — so that configured names are unambiguous and
/// SQL-injection-safe. Snowflake binds parameters positionally with <c>?</c>; the
/// <see cref="ParameterPrefix"/> is reported as <c>?</c> for callers that inspect it, though the
/// provider's query builder emits positional placeholders directly.
/// </remarks>
internal sealed class SnowflakeSqlDialect : ISqlDialect
{
    /// <summary>Singleton instance — the dialect carries no per-call state.</summary>
    public static readonly SnowflakeSqlDialect Instance = new();

    /// <inheritdoc />
    public string Name => "snowflake";

    /// <inheritdoc />
    public string ParameterPrefix => "?";

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return SnowflakeIdentifier.Quote(identifier);
    }
}
