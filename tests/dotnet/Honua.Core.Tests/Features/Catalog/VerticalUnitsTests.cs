// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

/// <summary>
/// Unit coverage for <see cref="MetadataV2VerticalUnits.TryNormalize"/>. The canonical
/// vertical-unit token participates in the extrusion metadata wire contract,
/// so case-folding and the meters-default fallback need pinned behaviour
/// (#1144).
/// </summary>
public sealed class MetadataV2VerticalUnitsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void TryNormalize_NullOrBlank_ReturnsMeters(string? input)
    {
        var ok = MetadataV2VerticalUnits.TryNormalize(input, out var normalized);

        ok.Should().BeTrue();
        normalized.Should().Be(MetadataV2VerticalUnits.Meters);
    }

    [Theory]
    [InlineData("meters", "meters")]
    [InlineData("METERS", "meters")]
    [InlineData("Meters", "meters")]
    [InlineData(" meters ", "meters")]
    public void TryNormalize_Meters_NormalizesToCanonical(string input, string expected)
    {
        MetadataV2VerticalUnits.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("feet", "feet")]
    [InlineData("FEET", "feet")]
    [InlineData("Feet", "feet")]
    public void TryNormalize_Feet_NormalizesToCanonical(string input, string expected)
    {
        MetadataV2VerticalUnits.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("usSurveyFeet", "usSurveyFeet")]
    [InlineData("ussurveyfeet", "usSurveyFeet")]
    [InlineData("USSURVEYFEET", "usSurveyFeet")]
    public void TryNormalize_UsSurveyFeet_NormalizesToCanonical(string input, string expected)
    {
        MetadataV2VerticalUnits.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("metres")]   // British spelling not accepted; MetadataV2ExtrusionValidator surfaces this.
    [InlineData("m")]         // Symbol form not accepted.
    [InlineData("yards")]
    [InlineData("kilometres")]
    [InlineData("ussurvey-feet")]
    public void TryNormalize_UnknownToken_ReturnsFalseAndEmptyString(string input)
    {
        var ok = MetadataV2VerticalUnits.TryNormalize(input, out var normalized);

        ok.Should().BeFalse();
        normalized.Should().BeEmpty();
    }

    [UnitTest]
    public void CanonicalConstants_AreStable()
    {
        // The wire form is part of the public extrusion metadata contract.
        MetadataV2VerticalUnits.Meters.Should().Be("meters");
        MetadataV2VerticalUnits.Feet.Should().Be("feet");
        MetadataV2VerticalUnits.UsSurveyFeet.Should().Be("usSurveyFeet");
    }
}
