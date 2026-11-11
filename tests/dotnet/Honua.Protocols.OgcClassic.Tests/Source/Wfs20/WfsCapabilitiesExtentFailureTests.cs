// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Xml.Linq;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Xunit;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsCapabilitiesExtentFailureTests
{
    [Theory]
    [Trait("Tier", "Integration")]
    [InlineData("2.0.0", false, 4326)]
    [InlineData("2.0.0", true, 4326)]
    [InlineData("2.0.0", true, CapabilitiesExtentFixture.ProjectedSrid)]
    [InlineData("1.1.0", false, 4326)]
    [InlineData("1.1.0", true, 4326)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task GetCapabilities_ExtentReadAvailability_AdvertisesOnlyHealthyLayers(
        string version, bool extentReadFails, int healthyLayerSrid)
    {
        var transform = new CapabilitiesExtentFixture.ExtentTransformProbe(extentReadFails);
        await using var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ReplaceService<IMetadataV2GraphProvider>(CapabilitiesExtentFixture.PublishedLayers(
                MetadataV2ServiceProtocols.Wfs20, MetadataV2PublicationType.WfsFeatureType, healthyLayerSrid))
            .ReplaceService<ICoordinateTransformService>(transform);
        await fixture.InitializeAsync();

        using var response = await fixture.Client.GetAsync($"/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION={version}");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(healthyLayerSrid == CapabilitiesExtentFixture.ProjectedSrid ? 2 : 1, transform.ExtentReads);
        Assert.Equal((CapabilitiesExtentFixture.ProjectedSrid, 4326), transform.LastCrs);
        Assert.True(response.StatusCode == HttpStatusCode.OK, content);
        var xml = XDocument.Parse(content);
        var titles = xml.Descendants()
            .Where(element => element.Name.LocalName == "FeatureType")
            .Select(element => element.Elements().Single(child => child.Name.LocalName == "Title").Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedTitles = extentReadFails
            ? healthyLayerSrid == 4326 ? ["healthy_extent"] : []
            : ["healthy_extent", "projected_extent"];
        Assert.Equal(expectedTitles, titles);
        if (expectedTitles.Length == 0)
        {
            Assert.DoesNotContain(xml.Descendants(), element => element.Name.LocalName == "FeatureTypeList");
        }
    }
}
