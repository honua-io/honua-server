// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Db.SqlServer.Features.FeatureStore.Services;

namespace Honua.Db.SqlServer.Queries.Filters;

internal sealed class SqlServerSqlDialect : ISqlDialect
{
    public static readonly SqlServerSqlDialect Instance = new();

    public string Name => "sqlserver";

    public string ParameterPrefix => "@";

    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return SqlServerIdentifier.Quote(identifier);
    }
}
