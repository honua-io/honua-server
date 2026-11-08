// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Xml.Linq;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Xunit;
using NSubstitute;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class WmsCapabilitiesExtentFailureTests
{
    [Theory]
    [Trait("Tier", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    public async Task GetCapabilities_ExtentReadAvailability_AdvertisesOnlyHealthyLayers(bool extentReadFails)
    {
        var transform = new CapabilitiesExtentFixture.ExtentTransformProbe(extentReadFails);
        await using var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ReplaceService<IMetadataV2GraphProvider>(CapabilitiesExtentFixture.PublishedLayers(
                MetadataV2ServiceProtocols.Wms, MetadataV2PublicationType.WmsLayer))
            .ReplaceService<ICoordinateTransformService>(transform);
        await fixture.InitializeAsync();

        using var response = await fixture.Client.GetAsync("/rest/services/capabilities_extents/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(1, transform.ExtentReads);
        Assert.Equal((CapabilitiesExtentFixture.ProjectedSrid, 4326), transform.LastCrs);
        Assert.True(response.StatusCode == HttpStatusCode.OK, content);
        var titles = LayerTitles(content);
        string[] expectedTitles = extentReadFails ? ["healthy_extent"] : ["healthy_extent", "projected_extent"];
        Assert.Equal(expectedTitles, titles);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    public async Task GetCapabilities_StyleReadFails_OmitsOnlyFailedLayer()
    {
        var styles = Substitute.For<ILayerStyleCatalog>();
        styles.GetLayerStyleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LayerStyleDefinition?>(null));
        styles.GetLayerStyleAsync(2, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<LayerStyleDefinition?>(new IOException("private style failure details")));
        await using var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ReplaceService<IMetadataV2GraphProvider>(CapabilitiesExtentFixture.PublishedLayers(
                MetadataV2ServiceProtocols.Wms, MetadataV2PublicationType.WmsLayer))
            .ReplaceService<ICoordinateTransformService>(new CapabilitiesExtentFixture.ExtentTransformProbe(false))
            .ReplaceService<ILayerStyleCatalog>(styles);
        await fixture.InitializeAsync();

        using var response = await fixture.Client.GetAsync("/rest/services/capabilities_extents/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0");
        var content = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, content);
        Assert.Equal(["healthy_extent"], LayerTitles(content));
        Assert.DoesNotContain("private style failure details", content, StringComparison.Ordinal);
        await styles.Received().GetLayerStyleAsync(2, Arg.Any<CancellationToken>());
    }

    private static string[] LayerTitles(string content) => XDocument.Parse(content).Descendants()
            .Where(element => element.Name.LocalName == "Layer"
                && element.Elements().Any(child => child.Name.LocalName == "Name"))
            .Select(element => element.Elements().Single(child => child.Name.LocalName == "Title").Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
