// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Unit tests for <see cref="ImageServerMosaicRule"/> parsing and ordering resolution, with a
/// focus on the non-date <c>esriMosaicByAttribute</c> support added in #1870.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerMosaicRuleTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Operation(Operations.Export)]
    [InlineData("OBJECTID", "id")]
    [InlineData("BandCount", "band_count")]
    [InlineData("num_bands", "band_count")]
    [InlineData("width", "width")]
    [InlineData("height", "height")]
    [InlineData("SRID", "srid")]
    public void TryParse_ByAttributeAllowlistedColumn_MapsToPhysicalColumn(string sortField, string expectedColumn)
    {
        var ok = ImageServerMosaicRule.TryParse(
            $"{{\"mosaicMethod\":\"esriMosaicByAttribute\",\"sortField\":\"{sortField}\"}}",
            out var rule, out var error, out var notImplemented);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        notImplemented.Should().BeFalse();
        rule.Method.Should().Be(MosaicMethod.Attribute);
        rule.AttributeSortColumn.Should().Be(expectedColumn);
        rule.ToOrdering().Should().Be(RasterMosaicOrdering.Attribute);

        var sort = rule.ToAttributeSort();
        sort.Should().NotBeNull();
        sort!.Value.Column.Should().Be(expectedColumn);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void TryParse_ByAttributeAscending_SetsAscendingSort()
    {
        var ok = ImageServerMosaicRule.TryParse(
            "{\"mosaicMethod\":\"esriMosaicByAttribute\",\"sortField\":\"BandCount\",\"ascending\":true}",
            out var rule, out _, out _);

        ok.Should().BeTrue();
        rule.Method.Should().Be(MosaicMethod.Attribute);
        rule.Ascending.Should().BeTrue();
        rule.ToAttributeSort()!.Value.Ascending.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void TryParse_ByAttributeUnknownField_StaysUnsupported()
    {
        var ok = ImageServerMosaicRule.TryParse(
            "{\"mosaicMethod\":\"esriMosaicByAttribute\",\"sortField\":\"SensorAzimuth\"}",
            out var rule, out _, out _);

        ok.Should().BeTrue();
        rule.Method.Should().Be(MosaicMethod.Unsupported);
        rule.AttributeSortColumn.Should().BeNull();
        rule.ToAttributeSort().Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void TryParse_ByAttributeDateField_UsesAcquisitionOrdering()
    {
        var ok = ImageServerMosaicRule.TryParse(
            "{\"mosaicMethod\":\"esriMosaicByAttribute\",\"sortField\":\"AcquisitionDate\"}",
            out var rule, out _, out _);

        ok.Should().BeTrue();
        rule.Method.Should().Be(MosaicMethod.ByDate);
        rule.ToOrdering().Should().Be(RasterMosaicOrdering.AcquisitionNewest);
        rule.ToAttributeSort().Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void TryParse_NadirMethod_ResolvesToNadirOrdering()
    {
        var ok = ImageServerMosaicRule.TryParse(
            "{\"mosaicMethod\":\"esriMosaicNadir\"}",
            out var rule, out var error, out var notImplemented);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        notImplemented.Should().BeFalse();
        rule.Method.Should().Be(MosaicMethod.Nadir);
        rule.ToOrdering().Should().Be(RasterMosaicOrdering.Nadir);
        // Nadir ranks by sensor off-nadir angle in the store, not by an allowlisted attribute sort.
        rule.ToAttributeSort().Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void TryParse_CenterMethod_StaysUnsupported()
    {
        var ok = ImageServerMosaicRule.TryParse(
            "{\"mosaicMethod\":\"esriMosaicCenter\"}",
            out var rule, out _, out _);

        ok.Should().BeTrue();
        rule.Method.Should().Be(MosaicMethod.Unsupported);
        rule.ToAttributeSort().Should().BeNull();
    }

    // #4063: service metadata advertises exactly the methods the parser executes. Every advertised
    // token, sent back as its esriMosaic* rule, must classify as executable; the Esri methods the
    // service cannot execute (501 on a multi-raster request) must not be advertised.
    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public void AllowedMosaicMethods_AdvertisesExactlyTheExecutableEsriMethods()
    {
        var advertised = ImageServerMosaicRule.AllowedMosaicMethods.Split(',');
        advertised.Should().Equal("None", "NorthWest", "LockRaster", "ByAttribute", "Nadir", "Seamline");

        foreach (var token in advertised)
        {
            var json = token == "LockRaster"
                ? "{\"mosaicMethod\":\"esriMosaicLockRaster\",\"lockRasterIds\":[1]}"
                : $"{{\"mosaicMethod\":\"esriMosaic{token}\"}}";

            var ok = ImageServerMosaicRule.TryParse(json, out var rule, out var error, out var notImplemented);

            ok.Should().BeTrue($"advertised method {token} must parse: {error}");
            notImplemented.Should().BeFalse();
            rule.Method.Should().NotBe(MosaicMethod.Unsupported, $"advertised method {token} must be executable");
        }

        foreach (var token in new[] { "Center", "Viewpoint" })
        {
            ImageServerMosaicRule.TryParse($"{{\"mosaicMethod\":\"esriMosaic{token}\"}}", out var rule, out _, out _)
                .Should().BeTrue();
            rule.Method.Should().Be(MosaicMethod.Unsupported);
            advertised.Should().NotContain(token);
        }
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public void DefaultMosaicMethod_DescribesTheOrderingOfARuleLessRequest()
    {
        ImageServerMosaicRule.TryParse(null, out var ruleLess, out _, out _).Should().BeTrue();

        var ok = ImageServerMosaicRule.TryParse(
            $"{{\"mosaicMethod\":\"esriMosaic{ImageServerMosaicRule.DefaultMosaicMethod}\",\"sortField\":\"{ImageServerMosaicRule.DefaultSortField}\"}}",
            out var advertisedDefault, out _, out _);

        ok.Should().BeTrue();
        advertisedDefault.Method.Should().Be(MosaicMethod.ByDate);
        advertisedDefault.ToOrdering().Should().Be(RasterMosaicOrdering.AcquisitionNewest);
        advertisedDefault.ToOrdering().Should().Be(ruleLess.ToOrdering());
    }
}
