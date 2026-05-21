// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Spec.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Spec.Domain;

/// <summary>
/// Unit coverage for <see cref="SpecGrammarVersion.TryParse"/> and the shipped
/// version constants. These values participate in compatibility checks and the
/// content-hash cache key, so the parser must not silently accept malformed
/// version literals (#1144).
/// </summary>
public sealed class SpecGrammarVersionTests
{
    [Theory]
    [InlineData("v1.0", 1, 0)]
    [InlineData("v2.3", 2, 3)]
    [InlineData("V10.42", 10, 42)]
    [InlineData("v0.1", 0, 1)]
    public void TryParse_ValidVersion_ReturnsTrueAndComponents(string input, int expectedMajor, int expectedMinor)
    {
        var ok = SpecGrammarVersion.TryParse(input, out var major, out var minor);

        ok.Should().BeTrue();
        major.Should().Be(expectedMajor);
        minor.Should().Be(expectedMinor);
    }

    [UnitTest]
    public void TryParse_TolerantOfSurroundingWhitespace()
    {
        var ok = SpecGrammarVersion.TryParse("  v1.0\t", out var major, out var minor);

        ok.Should().BeTrue();
        major.Should().Be(1);
        minor.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void TryParse_NullOrBlank_ReturnsFalse(string? input)
    {
        SpecGrammarVersion.TryParse(input, out var major, out var minor).Should().BeFalse();
        major.Should().Be(0);
        minor.Should().Be(0);
    }

    [Theory]
    [InlineData("1.0")]      // missing v prefix
    [InlineData("ver1.0")]   // wrong prefix
    [InlineData("v.0")]      // missing major
    [InlineData("v1.")]      // trailing dot
    [InlineData("vfoo.bar")] // non-numeric major
    [InlineData("vv1.0")]    // double prefix
    public void TryParse_MalformedInput_ReturnsFalse(string input)
    {
        var ok = SpecGrammarVersion.TryParse(input, out _, out _);

        ok.Should().BeFalse();
    }

    [UnitTest]
    public void TryParse_NonNumericMinor_ReturnsFalse()
    {
        // Short-circuit means major may already be assigned before parsing
        // fails on the minor component — only the boolean result is contractual.
        SpecGrammarVersion.TryParse("v1.x", out _, out _).Should().BeFalse();
    }

    [UnitTest]
    public void Current_MatchesMajorMinorConstants()
    {
        // The string and integer constants must agree — downstream cache keys
        // mix the two and silently drifting them would corrupt cached output.
        SpecGrammarVersion.TryParse(SpecGrammarVersion.Current, out var major, out var minor)
            .Should().BeTrue();
        major.Should().Be(SpecGrammarVersion.CurrentMajor);
        minor.Should().Be(SpecGrammarVersion.CurrentMinor);
    }

    [UnitTest]
    public void SchemaUrl_IsStableAcrossReleases()
    {
        // Schema URL participates in canonical-spec output and external schema
        // discovery. A silent change breaks tooling that resolves the URL.
        SpecGrammarVersion.SchemaUrl.Should()
            .Be("https://honua.io/spec/grammar/v1.0/spec.json");
    }

    [UnitTest]
    public void CurrentOperatorCapability_IsParseableVersion()
    {
        SpecGrammarVersion.TryParse(SpecGrammarVersion.CurrentOperatorCapability, out _, out _)
            .Should().BeTrue();
    }

    [UnitTest]
    public void TryParse_VeryShortInput_ReturnsFalse()
    {
        SpecGrammarVersion.TryParse("v1", out _, out _).Should().BeFalse();
        SpecGrammarVersion.TryParse("v", out _, out _).Should().BeFalse();
    }
}
