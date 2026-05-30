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
}
