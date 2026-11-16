// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Db.Postgres.Queries.Filters;

internal sealed class PostgresSqlDialect : ISqlDialect
{
    public static readonly PostgresSqlDialect Instance = new();

    public string Name => "postgres";

    public string ParameterPrefix => "@";

    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return SqlIdentifier.QuoteIdentifier(identifier);
    }
}
