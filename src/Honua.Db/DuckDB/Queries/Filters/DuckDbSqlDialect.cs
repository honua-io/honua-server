// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Db.DuckDB.Features.Infrastructure;

namespace Honua.Db.DuckDB.Queries.Filters;

internal sealed class DuckDbSqlDialect : ISqlDialect
{
    public static readonly DuckDbSqlDialect Instance = new();

    public string Name => "duckdb";

    public string ParameterPrefix => "$";

    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        // Route through the existing helper so any call-site quirks live in one place.
        return DuckDBExternalSourceSql.QuoteIdentifier(identifier);
    }

    public string QuoteLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return DuckDBExternalSourceSql.QuoteLiteral(literal);
    }
}
