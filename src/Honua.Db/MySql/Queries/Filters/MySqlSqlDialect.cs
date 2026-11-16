// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Db.MySql.Features.Infrastructure;

namespace Honua.Db.MySql.Queries.Filters;

internal sealed class MySqlSqlDialect : ISqlDialect
{
    public static readonly MySqlSqlDialect Instance = new();

    public string Name => "mysql";

    public string ParameterPrefix => "@";

    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return MySqlIdentifier.Quote(identifier);
    }

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
