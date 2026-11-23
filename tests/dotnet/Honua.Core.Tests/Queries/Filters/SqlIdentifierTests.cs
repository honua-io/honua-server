// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters;

public sealed class SqlIdentifierTests
{
    [UnitTheory]
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

    [UnitTheory]
    [InlineData("name")]
    [InlineData("_private")]
    [InlineData("名前")]
    [InlineData("länge_straße")]
    [InlineData("ʻāina_inoa")]
    [InlineData("x\u0301y")]
    [InlineData("road2")]
    public void IsUnicodeIdentifier_AcceptsLettersOfAnyScript(string identifier)
        => Assert.True(SqlIdentifier.IsUnicodeIdentifier(identifier));

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2road")]
    [InlineData("field name")]
    [InlineData("名\"前")]
    [InlineData("field\n")]
    [InlineData("a-b")]
    [InlineData("eo:cloud_cover")]
    [InlineData("x\"; DROP TABLE parcels; --")]
    public void IsUnicodeIdentifier_RejectsQuotesPunctuationAndControls(string? identifier)
        => Assert.False(SqlIdentifier.IsUnicodeIdentifier(identifier));

    [UnitTest]
    public void IsUnicodeIdentifier_EnforcesThePostgresByteLimit()
    {
        Assert.True(SqlIdentifier.IsUnicodeIdentifier(new string('a', 63)));
        Assert.False(SqlIdentifier.IsUnicodeIdentifier(new string('a', 64)));
        Assert.True(SqlIdentifier.IsUnicodeIdentifier(new string('名', 21)));
        Assert.False(SqlIdentifier.IsUnicodeIdentifier(new string('名', 22)));
        Assert.True(SqlIdentifier.HasUnicodeIdentifierShape(new string('名', 22)));
    }
}
