// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Db.Redshift.Features.FeatureStore.Services;

namespace Honua.Db.Redshift.Queries.Filters;

internal sealed class RedshiftSqlDialect : ISqlDialect
{
    public static readonly RedshiftSqlDialect Instance = new();

    public string Name => "redshift";

    public string ParameterPrefix => "@";

    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be empty or whitespace.", nameof(identifier));
        }

        return RedshiftIdentifier.Quote(identifier);
    }
}
