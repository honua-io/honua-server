// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.DataEnrichment;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.DataEnrichment;

/// <summary>
/// Unit tests for the managed enrichment-dataset registration validation (#2280).
/// </summary>
public sealed class EnrichmentDatasetValidationTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("ne-admin-0-countries", true)]
    [InlineData("countries", true)]
    [InlineData("a1", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("-leading-hyphen", false)]
    [InlineData("Has Spaces", false)]
    [InlineData("UpperCase", false)]
    public void TryValidateId_EnforcesSlugRules(string id, bool expectedValid)
    {
        var valid = EnrichmentDatasetValidation.TryValidateId(id, out var error);

        valid.Should().Be(expectedValid);
        if (!expectedValid)
        {
            error.Should().NotBeNullOrEmpty();
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("boundary", true)]
    [InlineData("demographic", true)]
    [InlineData("poi", true)]
    [InlineData("BOUNDARY", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("nonsense", false)]
    public void TryValidateCategory_AcceptsKnownCategories(string? category, bool expectedValid)
    {
        var valid = EnrichmentDatasetValidation.TryValidateCategory(category, out var normalized, out _);

        valid.Should().Be(expectedValid);
        if (valid && !string.IsNullOrWhiteSpace(category))
        {
            normalized.Should().Be(category.ToLowerInvariant());
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("intersects", true)]
    [InlineData("contains", true)]
    [InlineData("within", true)]
    [InlineData("dwithin", true)]
    [InlineData(null, true)]
    [InlineData("touches", false)]
    public void TryValidatePredicate_AcceptsKnownPredicates(string? predicate, bool expectedValid)
    {
        var valid = EnrichmentDatasetValidation.TryValidatePredicate(predicate, out var normalized, out _);

        valid.Should().Be(expectedValid);
        if (valid && string.IsNullOrWhiteSpace(predicate))
        {
            normalized.Should().Be("intersects");
        }
    }

    [UnitTest]
    public void TryValidateMinimumEdition_ParsesEdition()
    {
        EnrichmentDatasetValidation.TryValidateMinimumEdition("Community", out var community, out _).Should().BeTrue();
        community.Should().Be(HonuaEdition.Community);

        EnrichmentDatasetValidation.TryValidateMinimumEdition(null, out var defaulted, out _).Should().BeTrue();
        defaulted.Should().Be(HonuaEdition.Pro);

        EnrichmentDatasetValidation.TryValidateMinimumEdition("Gold", out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(-1, false)]
    [InlineData(null, false)]
    public void TryValidateLayerId_RequiresNonNegative(int? layerId, bool expectedValid)
        => EnrichmentDatasetValidation.TryValidateLayerId(layerId, out _).Should().Be(expectedValid);
}
