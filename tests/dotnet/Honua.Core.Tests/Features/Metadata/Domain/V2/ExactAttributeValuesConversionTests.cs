// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class ExactAttributeValuesConversionTests
{
    [UnitTest]
    public void ToFiniteDouble_ParsesTextInvariantlyWhateverTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            ExactAttributeValues.ToFiniteDouble("1234.50").Should().Be(1234.5);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [UnitTest]
    public void ToFiniteDouble_AcceptsClrAndJsonNumbers()
    {
        ExactAttributeValues.ToFiniteDouble(12.25m).Should().Be(12.25);
        ExactAttributeValues.ToFiniteDouble(JsonSerializer.SerializeToElement(7)).Should().Be(7);
        ExactAttributeValues.ToFiniteDouble(JsonSerializer.SerializeToElement("-0.5")).Should().Be(-0.5);
    }

    [UnitTheory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e400")]
    [InlineData("twelve")]
    public void ToFiniteDouble_RefusesNonFiniteOrNonNumericText(string text)
    {
        ExactAttributeValues.ToFiniteDouble(text).Should().BeNull();
    }

    [UnitTheory]
    [InlineData("0")]
    [InlineData("-12.50")]
    [InlineData("123456789012345678901234567890.1")]
    public void IsCanonicalDecimalText_AcceptsExactDecimalGrammar(string text)
    {
        ExactAttributeValues.IsCanonicalDecimalText(text).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("1e3")]
    [InlineData("01")]
    [InlineData(".5")]
    [InlineData(null)]
    public void IsCanonicalDecimalText_RefusesOtherNumberForms(string? text)
    {
        ExactAttributeValues.IsCanonicalDecimalText(text).Should().BeFalse();
    }

    [UnitTest]
    public void FormatDecimalNumber_ExponentThatFits_BecomesPlainText()
    {
        ExactAttributeValues.FormatDecimalNumber(1e-7d).Should().Be("0.0000001");
        ExactAttributeValues.FormatDecimalNumber(JsonDocument.Parse("1E-07").RootElement).Should().Be("0.0000001");
    }

    [UnitTest]
    public void FormatDecimalNumber_ExponentThatDecimalCannotHold_ReturnsNull()
    {
        ExactAttributeValues.FormatDecimalNumber(JsonDocument.Parse("1.5e-30").RootElement).Should().BeNull();
    }

    [UnitTest]
    public void ToFiniteDouble_OutOfRangeJsonNumber_ReturnsNull()
    {
        using var document = JsonDocument.Parse("1e400");

        ExactAttributeValues.ToFiniteDouble(document.RootElement).Should().BeNull();
    }
}
