// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Core.Tests.Queries.Filters;

public sealed class SqlIdentifierTests
{
    [Theory]
    [InlineData("parcels", "\"parcels\"")]
    [InlineData("Object ID", "\"Object ID\"")]
    [InlineData("schema.table", "\"schema.table\"")]
    [InlineData("名", "\"名\"")]
    [InlineData("x\"; DROP TABLE parcels; --", "\"x\"\"; DROP TABLE parcels; --\"")]
    [InlineData("\"quoted\"", "\"\"\"quoted\"\"\"")]
    [InlineData("", "\"\"")]
    [InlineData(" ", "\" \"")]
    public void QuoteIdentifier_KeepsInputInsideOneIdentifier(string input, string expected)
        => Assert.Equal(expected, SqlIdentifier.QuoteIdentifier(input));
}
