// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

// Identifier quoting is the injection boundary for the callers that cannot bind a parameter.
public interface ISqlDialect
{
    string Name { get; }

    string ParameterPrefix { get; }

    string QuoteIdentifier(string identifier);

    string QuoteLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return "'" + literal.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
