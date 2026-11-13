// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Npgsql;
using Xunit;

namespace Honua.Db.Postgres.Tests.Queries.Filters;

[Trait("Tier", "Fast")]
public sealed class SqlIdentifierCompatibilityTests
{
    [Theory]
    [InlineData("parcels")]
    [InlineData("Object ID")]
    [InlineData("schema.table")]
    [InlineData("名")]
    [InlineData("x\"; DROP TABLE parcels; --")]
    [InlineData("\"quoted\"")]
    [InlineData("")]
    [InlineData(" ")]
    public void QuoteIdentifier_PreservesNpgsqlEncoding(string input)
    {
        using var previous = new NpgsqlCommandBuilder();
        Assert.Equal(previous.QuoteIdentifier(input), SqlIdentifier.QuoteIdentifier(input));
    }
}
