// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Db.Postgres.Queries.Filters;

internal partial class PostgresSqlFilterTranslator
{
    protected override string TranslateLiteral(Literal literal)
    {
        // Numeric text avoids a lossy binary-float conversion before PostgreSQL evaluates it.
        return literal is { Type: LiteralType.Number, Value: string text }
            ? $"CAST({AddParameter(text)} AS numeric)"
            : base.TranslateLiteral(literal);
    }
}
