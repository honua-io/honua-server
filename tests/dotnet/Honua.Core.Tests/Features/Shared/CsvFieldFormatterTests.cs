// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.SharedServices;

public sealed class CsvFieldFormatterTests
{
    [UnitTheory]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("  =A1", "'  =A1")]
    [InlineData("=HYPERLINK(\"https://example.invalid\",\"open\")", "\"'=HYPERLINK(\"\"https://example.invalid\"\",\"\"open\"\")\"")]
    [InlineData("=A1,B1", "\"'=A1,B1\"")]
    public void Escape_FormulaField_PrefixesSingleQuote(string value, string expected)
    {
        CsvFieldFormatter.Escape(value).Should().Be(expected);
    }

    [UnitTheory]
    [InlineData("-12.5", "-12.5")]
    [InlineData("+3", "+3")]
    [InlineData("1e-3", "1e-3")]
    [InlineData("-3.4028235E+38", "-3.4028235E+38")]
    [InlineData("-1E+30", "-1E+30")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("", "\"\"")]
    [InlineData("   ", "   ")]
    public void Escape_NonFormulaField_KeepsValue(string value, string expected)
    {
        CsvFieldFormatter.Escape(value).Should().Be(expected);
    }

    [UnitTest]
    public void Escape_Null_ReturnsEmpty()
    {
        CsvFieldFormatter.Escape(null).Should().BeEmpty();
    }
}
