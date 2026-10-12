// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.Ogc.Api.Features;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcCrsResolverTests
{
    [UnitTest]
    public void TryResolveCrs_WithCustomEpsg_ReturnsDefinition()
    {
        var supportedCrs = new Dictionary<string, CrsDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [OgcFeaturesUtilities.Crs84Uri] = new CrsDefinition(
                OgcFeaturesUtilities.Crs84Uri,
                4326,
                AxisOrder.EastNorth,
                true),
            ["http://www.opengis.net/def/crs/EPSG/0/26910"] = new CrsDefinition(
                "http://www.opengis.net/def/crs/EPSG/0/26910",
                26910,
                AxisOrder.EastNorth,
                false)
        };

        OgcFeaturesUtilities.TryResolveCrs("EPSG:26910", supportedCrs, out var definition, out var error)
            .Should()
            .BeTrue(error);
        definition.Srid.Should().Be(26910);
        definition.Uri.Should().Be("http://www.opengis.net/def/crs/EPSG/0/26910");
        definition.AxisOrder.Should().Be(AxisOrder.EastNorth);
    }

    [Theory]
    [InlineData("CRS84")]
    [InlineData("OGC:CRS84")]
    [InlineData("[CRS84]")]
    [InlineData("[OGC:CRS84]")]
    [InlineData("<OGC:CRS84>")]
    [Trait("Category", "Unit")]
    public void TryResolveCrs_WithCrs84Aliases_ReturnsDefinition(string alias)
    {
        var supportedCrs = new Dictionary<string, CrsDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [OgcFeaturesUtilities.Crs84Uri] = new CrsDefinition(
                OgcFeaturesUtilities.Crs84Uri,
                4326,
                AxisOrder.EastNorth,
                true)
        };

        OgcFeaturesUtilities.TryResolveCrs(alias, supportedCrs, out var definition, out var error)
            .Should()
            .BeTrue(error);
        definition.Uri.Should().Be(OgcFeaturesUtilities.Crs84Uri);
        definition.Srid.Should().Be(4326);
        definition.AxisOrder.Should().Be(AxisOrder.EastNorth);
    }

    [Theory]
    [InlineData("[EPSG:26910]")]
    [InlineData("<EPSG:26910>")]
    [InlineData("[urn:ogc:def:crs:EPSG::26910]")]
    [Trait("Category", "Unit")]
    public void TryResolveCrs_WithWrappedEpsgAliases_ReturnsDefinition(string alias)
    {
        var supportedCrs = new Dictionary<string, CrsDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [OgcFeaturesUtilities.Crs84Uri] = new CrsDefinition(
                OgcFeaturesUtilities.Crs84Uri,
                4326,
                AxisOrder.EastNorth,
                true),
            ["http://www.opengis.net/def/crs/EPSG/0/26910"] = new CrsDefinition(
                "http://www.opengis.net/def/crs/EPSG/0/26910",
                26910,
                AxisOrder.EastNorth,
                false)
        };

        OgcFeaturesUtilities.TryResolveCrs(alias, supportedCrs, out var definition, out var error)
            .Should()
            .BeTrue(error);
        definition.Uri.Should().Be("http://www.opengis.net/def/crs/EPSG/0/26910");
        definition.Srid.Should().Be(26910);
        definition.AxisOrder.Should().Be(AxisOrder.EastNorth);
    }

    // BH7-008 regression: when the CRS registry is degraded and the default CRS84
    // entry is absent, TryResolveCrs must return false with a descriptive error
    // rather than throwing KeyNotFoundException (which was previously silently
    // swallowed as a misleading HTTP 400 "Invalid filter parameters").
    [UnitTest]
    public void TryResolveCrs_WithNullCrs_WhenCrs84Absent_ReturnsFalseWithError()
    {
        // Simulate a degraded CRS registry that did not resolve CRS84 at startup
        // (e.g., PROJ misconfiguration or transient registry error).
        var supportedCrs = new Dictionary<string, CrsDefinition>(StringComparer.OrdinalIgnoreCase);

        // Must not throw and must return false with a descriptive error.
        var succeeded = OgcFeaturesUtilities.TryResolveCrs(null, supportedCrs, out _, out var error);

        succeeded.Should().BeFalse("CRS84 is absent from a degraded registry");
        error.Should().NotBeNullOrWhiteSpace("a descriptive error must be returned");
        error.Should().Contain("CRS84", "the error must identify the missing default CRS");
    }

    [UnitTest]
    public void TryResolveCrs_WithEmptyCrs_WhenCrs84Absent_ReturnsFalseWithError()
    {
        // Same degraded-registry scenario but triggered by an empty string (equivalent
        // to null — the common case when a client sends no crs= parameter).
        var supportedCrs = new Dictionary<string, CrsDefinition>(StringComparer.OrdinalIgnoreCase);

        var succeeded = OgcFeaturesUtilities.TryResolveCrs(string.Empty, supportedCrs, out _, out var error);

        succeeded.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("CRS84");
    }
}
