// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Databricks.Features.Infrastructure;

namespace Honua.Databricks.Queries.Filters;

/// <summary>
/// Databricks SQL (Spark SQL) implementation of <see cref="ISqlDialect"/>.
/// </summary>
/// <remarks>
/// Identifiers are backtick-quoted with embedded backticks doubled. The Databricks
/// SQL Statement Execution API binds named markers using a leading colon
/// (<c>:name</c>), so <see cref="ParameterPrefix"/> is <c>:</c>.
/// </remarks>
internal sealed class DatabricksSqlDialect : ISqlDialect
{
    /// <summary>Singleton instance — the dialect carries no per-call state.</summary>
    public static readonly DatabricksSqlDialect Instance = new();

    /// <inheritdoc />
    public string Name => "databricks";

    /// <inheritdoc />
    public string ParameterPrefix => ":";

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return DatabricksIdentifier.Quote(identifier);
    }

    /// <inheritdoc />
    public string QuoteLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);

        // Databricks SQL doubles single quotes; backslash is also an escape character in
        // string literals, so double both to be safe regardless of session settings.
        var escaped = literal
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);
        return "'" + escaped + "'";
    }
}
