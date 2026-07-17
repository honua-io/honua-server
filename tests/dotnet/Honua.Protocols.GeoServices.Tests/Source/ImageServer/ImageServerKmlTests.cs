// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerKmlTests
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/kml/image.kmz")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetKml_SeededMosaic_ReturnsKmzGroundOverlayOverServiceExtent()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/kml/image.kmz");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType
                .Should().Be("application/vnd.google-earth.kmz");

            var document = await ReadKmlFromKmzAsync(response);

            var groundOverlay = document.Descendants(Kml + "GroundOverlay").Should().ContainSingle().Subject;

            var latLonBox = groundOverlay.Element(Kml + "LatLonBox");
            latLonBox.Should().NotBeNull();

            // Seeded mosaic (SeedIssue522MosaicAsync) aggregates to x[0,4], y[0,2] in WGS84.
            ParseCoordinate(latLonBox!, "north").Should().BeApproximately(2.0, 1e-6);
            ParseCoordinate(latLonBox!, "south").Should().BeApproximately(0.0, 1e-6);
            ParseCoordinate(latLonBox!, "east").Should().BeApproximately(4.0, 1e-6);
            ParseCoordinate(latLonBox!, "west").Should().BeApproximately(0.0, 1e-6);

            var href = groundOverlay.Element(Kml + "Icon")?.Element(Kml + "href")?.Value;
            href.Should().NotBeNullOrWhiteSpace();
            href.Should().Contain("/ImageServer/exportImage?");
            href.Should().Contain("bbox=0,0,4,2");
            href.Should().Contain("bboxSR=4326");
            href.Should().Contain("imageSR=4326");
            href.Should().Contain("f=image");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/kml/image.kmz")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetKml_UnknownLayer_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                "/rest/services/99999/ImageServer/kml/image.kmz");

            // GeoServices convention (PA-070/PA-117): the transport is HTTP 200; the not-found
            // code lives in the Esri error envelope. The unknown layer must NOT fabricate a KMZ.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType
                .Should().NotBe("application/vnd.google-earth.kmz");

            using var json = System.Text.Json.JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("error").GetProperty("code").GetInt32()
                .Should().Be(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static double ParseCoordinate(XElement latLonBox, string name)
        => double.Parse(
            latLonBox.Element(Kml + name)!.Value,
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<XDocument> ReadKmlFromKmzAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var buffer = new MemoryStream(bytes);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        var entry = archive.GetEntry("doc.kml");
        entry.Should().NotBeNull("the KMZ must carry a doc.kml entry");

        using var entryStream = entry!.Open();
        return await XDocument.LoadAsync(entryStream, LoadOptions.None, CancellationToken.None);
    }

    private static async Task<WebAppFixture> CreateFixtureAsync()
    {
        var fixture = new WebAppFixture();
        await fixture.InitializeAsync();
        await RasterIntegrationTestData.SeedIssue522MosaicAsync(fixture);
        return fixture;
    }
}
