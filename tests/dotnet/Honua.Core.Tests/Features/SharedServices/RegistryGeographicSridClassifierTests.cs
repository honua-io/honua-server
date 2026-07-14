// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Shared.Services;
using Moq;

namespace Honua.Core.Tests.Features.SharedServices;

/// <summary>
/// Pins the registry-backed geographic-SRID classification seam (#2794): the registry is
/// authoritative when it answers, and the static <see cref="GeographicSridClassifier"/> allowlist is
/// the fallback when no registry is configured or the SRID is unknown to it.
/// </summary>
public sealed class RegistryGeographicSridClassifierTests
{
    private const string EpsgUri = "http://www.opengis.net/def/crs/EPSG/0/";

    [Fact]
    public async Task IsGeographicAsync_WhenRegistryReportsGeographic_ReturnsTrue()
    {
        // EPSG:4312 (MGI, Austria) is a genuine geographic CRS that is NOT on the static 21-code
        // allowlist. The registry knows it from spatial_ref_sys WKT; the seam must trust it.
        var classifier = CreateClassifier(4312, isGeographic: true);

        var result = await classifier.IsGeographicAsync(4312);

        result.Should().BeTrue("the registry classifies EPSG:4312 as geographic even though it is off the static list");
        GeographicSridClassifier.IsGeographicSrid(4312).Should().BeFalse("baseline: the static list misses EPSG:4312");
    }

    [Fact]
    public async Task IsGeographicAsync_WhenRegistryReportsProjected_ReturnsFalse()
    {
        // A geocentric code in the 4000-4999 block that the static range heuristic does not exclude
        // (EPSG:4964, ITRF2005 geocentric). The registry classifies it as projected from its WKT.
        var classifier = CreateClassifier(4964, isGeographic: false);

        var result = await classifier.IsGeographicAsync(4964);

        result.Should().BeFalse("the registry classifies the geocentric code as projected");
    }

    [Fact]
    public async Task IsGeographicForMeasurementAsync_WhenRegistryReportsProjectedGeocentric_ReturnsFalse()
    {
        // The static measurement heuristic would sweep 4964 into the geographic bucket via the
        // 4000-4999 range (it is not in the conservative geocentric-exclusion subset); the
        // registry answer must supersede it.
        var classifier = CreateClassifier(4964, isGeographic: false);

        var viaRegistry = await classifier.IsGeographicForMeasurementAsync(4964);
        var viaStatic = GeographicSridClassifier.IsGeographicOrUnlistedGeographicRangeSrid(4964);

        viaRegistry.Should().BeFalse("the registry authoritatively classifies EPSG:4964 as geocentric/projected");
        viaStatic.Should().BeTrue("baseline: the static range heuristic mis-sweeps EPSG:4964 into the geographic bucket");
    }

    [Fact]
    public async Task IsGeographicAsync_WhenRegistryHasNoAnswer_FallsBackToStaticList()
    {
        var registry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        registry
            .Setup(r => r.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CrsDefinition?)null);
        var classifier = new RegistryGeographicSridClassifier(registry.Object);

        (await classifier.IsGeographicAsync(4326)).Should().BeTrue("static fallback: EPSG:4326 is on the allowlist");
        (await classifier.IsGeographicAsync(3857)).Should().BeFalse("static fallback: EPSG:3857 is projected");
    }

    [Fact]
    public async Task IsGeographicForMeasurementAsync_WhenRegistryHasNoAnswer_FallsBackToRangeHeuristic()
    {
        var registry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        registry
            .Setup(r => r.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CrsDefinition?)null);
        var classifier = new RegistryGeographicSridClassifier(registry.Object);

        // EPSG:4301 (Tokyo) is off the static list but inside the 4000-4999 geographic block.
        (await classifier.IsGeographicForMeasurementAsync(4301)).Should().BeTrue(
            "static fallback: the range heuristic treats unlisted 4000-4999 codes as geographic for measurement");
        (await classifier.IsGeographicForMeasurementAsync(4978)).Should().BeFalse(
            "static fallback: EPSG:4978 (WGS 84 geocentric) is excluded from the range heuristic");
    }

    [Fact]
    public async Task Classifier_WithNoRegistry_UsesStaticListsExclusively()
    {
        // Read-only providers (DuckDB/MySQL) register no ICrsRegistry; the classifier must still
        // resolve and degrade to the static behaviour.
        var classifier = new RegistryGeographicSridClassifier(registry: null);

        (await classifier.IsGeographicAsync(4326)).Should().BeTrue();
        (await classifier.IsGeographicAsync(3857)).Should().BeFalse();
        (await classifier.IsGeographicForMeasurementAsync(4301)).Should().BeTrue();
    }

    [Fact]
    public async Task IsGeographicAsync_WithNonPositiveSrid_DoesNotConsultRegistry()
    {
        var registry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        var classifier = new RegistryGeographicSridClassifier(registry.Object);

        // 0 short-circuits to the static list without a registry round-trip (strict mock would throw
        // if consulted). The static classifier treats 0 as not-geographic.
        (await classifier.IsGeographicAsync(0)).Should().BeFalse();
        registry.Verify(
            r => r.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static RegistryGeographicSridClassifier CreateClassifier(int srid, bool isGeographic)
    {
        var registry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        var definition = new CrsDefinition(
            $"{EpsgUri}{srid.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            srid,
            isGeographic ? AxisOrder.NorthEast : AxisOrder.EastNorth,
            isGeographic);
        registry
            .Setup(r => r.ResolveBySridAsync(srid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        return new RegistryGeographicSridClassifier(registry.Object);
    }
}
