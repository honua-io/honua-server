// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Db.Databricks.Features.Infrastructure;

namespace Honua.Db.Databricks.Queries.Filters;

internal sealed class DatabricksSqlDialect : ISqlDialect
{
    public static readonly DatabricksSqlDialect Instance = new();

    public string Name => "databricks";

    public string ParameterPrefix => ":";

    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return DatabricksIdentifier.Quote(identifier);
    }

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
