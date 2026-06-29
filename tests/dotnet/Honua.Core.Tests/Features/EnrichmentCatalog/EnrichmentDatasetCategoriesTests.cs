// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Core.Tests.Features.EnrichmentCatalog;

/// <summary>
/// Unit tests for the enrichment-dataset category taxonomy (#2280).
/// </summary>
public sealed class EnrichmentDatasetCategoriesTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("boundary", true)]
    [InlineData("demographic", true)]
    [InlineData("poi", true)]
    [InlineData("Boundary", true)]
    [InlineData("POI", true)]
    [InlineData("city", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_RecognisesKnownCategories(string? value, bool expected)
        => EnrichmentDatasetCategories.IsValid(value).Should().Be(expected);

    [UnitTest]
    public void Constants_AreLowercaseTaxonomy()
    {
        EnrichmentDatasetCategories.Boundary.Should().Be("boundary");
        EnrichmentDatasetCategories.Demographic.Should().Be("demographic");
        EnrichmentDatasetCategories.Poi.Should().Be("poi");
    }
}
