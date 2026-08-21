// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GeoservicesCatalog)]
public sealed class GeoservicesCatalogEndpointTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public GeoservicesCatalogEndpointTests(WebAppFixture fixture) => _fixture = fixture;

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_DefaultFormat_ReturnsCatalogPayload()
    {
        var response = await _fixture.Client.GetAsync("/rest/services");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Honua does not advertise an ArcGIS Server version (see NoArcGisServerVersionTests).
        payload.RootElement.TryGetProperty("currentVersion", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("fullVersion", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("folders", out var folders).Should().BeTrue();
        folders.ValueKind.Should().Be(JsonValueKind.Array);
        payload.RootElement.TryGetProperty("services", out var services).Should().BeTrue();
        services.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_PjsonFormat_ReturnsCatalogPayload()
    {
        var response = await _fixture.Client.GetAsync("/rest/services?f=pjson");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_InvalidFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/rest/services?f=xml");

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/info")]
    public async Task GetRestInfo_DefaultFormat_ReturnsRootInfo()
    {
        var response = await _fixture.Client.GetAsync("/rest/info");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Honua does not advertise an ArcGIS Server version (see NoArcGisServerVersionTests).
        payload.RootElement.TryGetProperty("currentVersion", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("fullVersion", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("authInfo", out var authInfo).Should().BeTrue();
        authInfo.TryGetProperty("isTokenBasedSecurity", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_IncludesServicesWithExpectedStructure()
    {
        var response = await _fixture.Client.GetAsync("/rest/services?f=json");

        response.Be200Ok();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;

        root.TryGetProperty("services", out var services).Should().BeTrue();
        services.ValueKind.Should().Be(JsonValueKind.Array);

        if (services.GetArrayLength() > 0)
        {
            var firstService = services[0];
            firstService.TryGetProperty("name", out _).Should().BeTrue();
            firstService.TryGetProperty("type", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_IncludesVectorTileServerEntry()
    {
        var response = await _fixture.Client.GetAsync("/rest/services?f=json");

        response.Be200Ok();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var vectorTileEntries = payload.RootElement
            .GetProperty("services")
            .EnumerateArray()
            .Where(service =>
                service.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "VectorTileServer", StringComparison.Ordinal))
            .ToArray();

        // The default test graph seeds an EsriVectorTileLayer publication on the "test"
        // service, so the catalog must advertise a VectorTileServer entry with a
        // service-name-scoped URL, matching every other service type.
        vectorTileEntries.Should().NotBeEmpty();
        vectorTileEntries.Should().OnlyContain(service =>
            service.GetProperty("url").GetString()!.EndsWith(
                "/rest/services/test/VectorTileServer", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_ImageServerEntriesUseServiceScopedUrls()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new[]
            {
                new RasterInfo
                {
                    Id = 1,
                    LayerId = callInfo.ArgAt<int>(0),
                    Name = "test-raster",
                    Width = 256,
                    Height = 256,
                    BandCount = 3,
                    PixelType = "8BUI",
                    Srid = 4326,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            }));

        var fixture = new WebAppFixture()
            .ConfigureServices(services => services.AddSingleton(rasterStore));

        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.Client.GetAsync("/rest/services?f=json");

            response.Be200Ok();

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var imageServerUrls = payload.RootElement
                .GetProperty("services")
                .EnumerateArray()
                .Where(service =>
                    service.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "ImageServer", StringComparison.Ordinal))
                .Select(service => service.GetProperty("url").GetString())
                .ToArray();

            imageServerUrls.Should().NotBeEmpty();
            // ImageServer URLs are service-name scoped (canonical ArcGIS addressing,
            // matching every other service type), not numeric-layer-id scoped.
            imageServerUrls.Should().OnlyContain(url =>
                !string.IsNullOrWhiteSpace(url) &&
                System.Text.RegularExpressions.Regex.IsMatch(url, @".*/rest/services/[^/]+/ImageServer$"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetServiceDescriptions")]
    [Endpoint("POST /services")]
    [Endpoint("POST /services/{serviceName}/ImageServer")]
    public async Task PostSoapCatalog_GetServiceDescriptions_AdvertisesImageServer()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new[]
            {
                new RasterInfo
                {
                    Id = 1,
                    LayerId = callInfo.ArgAt<int>(0),
                    Name = "test-raster",
                    Width = 256,
                    Height = 256,
                    BandCount = 3,
                    PixelType = "8BUI",
                    Srid = 4326,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            }));

        var fixture = new WebAppFixture()
            .ConfigureServices(services => services.AddSingleton(rasterStore));

        await fixture.InitializeAsync();
        try
        {
            const string soapRequest = """
                <?xml version="1.0" encoding="utf-8"?>
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <GetServiceDescriptions xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
                  </soap:Body>
                </soap:Envelope>
                """;
            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");

            var response = await fixture.Client.PostAsync("/services", content);

            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");

            var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
            payload.Descendants()
                .Should().ContainSingle(element => element.Name.LocalName == "GetServiceDescriptionsResult");
            var description = payload.Descendants()
                .Where(element => element.Name.LocalName == "ServiceDescription")
                .First(element => element.Elements().Any(child =>
                    child.Name.LocalName == "Name" && child.Value == "test"));
            description.Elements()
                .Single(element => element.Name.LocalName == "Name")
                .Value.Should().Be("test");
            description.Elements()
                .Single(element => element.Name.LocalName == "Type")
                .Value.Should().Be("ImageServer");
            description.Elements()
                .Single(element => element.Name.LocalName == "Url")
                .Value.Should().EndWith("/services/test/ImageServer");

            var advertisedUrl = description.Elements()
                .Single(element => element.Name.LocalName == "Url")
                .Value;
            const string serviceRequest = """
                <?xml version="1.0" encoding="utf-8"?>
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <GetMessageVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
                  </soap:Body>
                </soap:Envelope>
                """;
            using var serviceContent = new StringContent(serviceRequest, Encoding.UTF8, "text/xml");
            var serviceResponse = await fixture.Client.PostAsync(new Uri(advertisedUrl).PathAndQuery, serviceContent);
            serviceResponse.Be200Ok();
            var servicePayload = XDocument.Parse(await serviceResponse.Content.ReadAsStringAsync());
            servicePayload.Descendants()
                .Single(element => element.Name.LocalName == "GetMessageVersionResult")
                .Value.Should().Be("esriArcGISVersion108");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetServiceDescriptions")]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetServiceDescriptionsEx")]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetFolders")]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetMessageVersion")]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetMessageFormats")]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetTokenServiceURL")]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "RequiresTokens")]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_SupportedOperations_ReturnExpectedResultWrapper()
    {
        var operations = new (string Operation, string ExpectedResult)[]
        {
            ("GetServiceDescriptions", "GetServiceDescriptionsResult"),
            ("GetServiceDescriptionsEx", "GetServiceDescriptionsExResult"),
            ("GetFolders", "GetFoldersResult"),
            ("GetMessageVersion", "GetMessageVersionResult"),
            ("GetMessageFormats", "GetMessageFormatsResult"),
            ("GetTokenServiceURL", "GetTokenServiceURLResult"),
            ("RequiresTokens", "RequiresTokensResult")
        };

        foreach (var (operation, expectedResult) in operations)
        {
            var soapRequest = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <{operation} xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
                  </soap:Body>
                </soap:Envelope>
                """;
            using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");

            var response = await _fixture.Client.PostAsync("/services", content);

            response.Be200Ok();
            var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
            payload.Descendants().Should().ContainSingle(element => element.Name.LocalName == expectedResult);
        }
    }
}
