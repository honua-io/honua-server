// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

[Trait("Tier", "Fast")]
public sealed class MigrationBoundingBoxesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(new double[0])]
    [InlineData(new double[] { 1, 2, 3 })]
    [InlineData(new double[] { 1, 2, 3, 4, 5 })]
    public void Normalize_MissingOrMalformedExtent_ReturnsNull(double[]? extent)
    {
        MigrationBoundingBoxes.Normalize(extent).Should().BeNull();
    }

    [Fact]
    public void Normalize_ReversedCorners_ReturnsOrderedCopyWithoutChangingObservation()
    {
        double[] observation = [10, 20, -5, -3];

        var normalized = MigrationBoundingBoxes.Normalize(observation);

        normalized.Should().Equal(-5, -3, 10, 20);
        normalized.Should().NotBeSameAs(observation);
        observation.Should().Equal(10, 20, -5, -3);
    }

    [Fact]
    public void Normalize_NonFiniteCoordinates_PreservesExistingExtentSemantics()
    {
        var normalized = MigrationBoundingBoxes.Normalize([double.NaN, double.PositiveInfinity, 5, double.NegativeInfinity]);

        normalized.Should().NotBeNull();
        double.IsNaN(normalized![0]).Should().BeTrue();
        double.IsNaN(normalized[2]).Should().BeTrue();
        normalized[1].Should().Be(double.NegativeInfinity);
        normalized[3].Should().Be(double.PositiveInfinity);
        MigrationBoundingBoxes.Overlap(normalized, [0, 0, 10, 10]).Should().BeFalse();
    }

    [Theory]
    [InlineData(new double[] { 10, 0, 20, 10 })]
    [InlineData(new double[] { 10, 10, 20, 20 })]
    [InlineData(new double[] { 5, 5, 5, 5 })]
    public void Overlap_TouchingEdgeCornerOrDegenerateExtent_IsIncluded(double[] target)
    {
        MigrationBoundingBoxes.Overlap([0, 0, 10, 10], target).Should().BeTrue();
        MigrationBoundingBoxes.Overlap(target, [0, 0, 10, 10]).Should().BeTrue();
    }

    [Theory]
    [InlineData(new double[] { 10.0001, 0, 20, 10 })]
    [InlineData(new double[] { 0, 10.0001, 10, 20 })]
    public void Overlap_SeparatedExtent_IsExcluded(double[] target)
    {
        MigrationBoundingBoxes.Overlap([0, 0, 10, 10], target).Should().BeFalse();
        MigrationBoundingBoxes.Overlap(target, [0, 0, 10, 10]).Should().BeFalse();
    }

    [Fact]
    public void Format_WithDecimalCommaCulture_UsesInvariantCoordinates()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            MigrationBoundingBoxes.Format([-1.25, 2.5, 3.75, 4.125])
                .Should().Be("[-1.25,2.5,3.75,4.125]");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
