// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using SkiaSharp;

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
        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new RasterInfo
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
            });

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
        var rasterStore = CreateSoapRasterStore();

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
            description.Elements()
                .Single(element => element.Name.LocalName == "Capabilities")
                .Value.Should().Be("Image,Metadata");

            var advertisedUrl = description.Elements()
                .Single(element => element.Name.LocalName == "Url")
                .Value;
            const string serviceRequest = """
                <?xml version="1.0" encoding="utf-8"?>
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <GetVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
                  </soap:Body>
                </soap:Envelope>
                """;
            using var serviceContent = new StringContent(serviceRequest, Encoding.UTF8, "text/xml");
            var serviceResponse = await fixture.Client.PostAsync(new Uri(advertisedUrl).PathAndQuery, serviceContent);
            serviceResponse.Be200Ok();
            var servicePayload = XDocument.Parse(await serviceResponse.Content.ReadAsStringAsync());
            servicePayload.Descendants()
                .Single(element => element.Name.LocalName == "GetVersionResponse")
                .Descendants()
                .Single(element => element.Name.LocalName == "Result")
                .Value.Should().Be("10.8");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_AllRasterProbesFail_ReturnsServerFault()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RasterInfo[]>(new InvalidOperationException("raster store unavailable")));
        var fixture = new WebAppFixture().ConfigureServices(services => services.AddSingleton(rasterStore));
        await fixture.InitializeAsync();
        try
        {
            using var response = await PostSoapAsync(fixture.Client, "/services", "GetServiceDescriptions");

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
            var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
            payload.Descendants().Single(element => element.Name.LocalName == "faultcode")
                .Value.Should().Be("soap:Server");
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

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetServiceDescriptionsEx")]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_GetServiceDescriptionsEx_AppliesFolderAndRejectsUnknownArguments()
    {
        const string nonRootFolderRequest = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetServiceDescriptionsEx xmlns="http://www.esri.com/schemas/ArcGIS/10.8">
                  <folderName>not-a-honua-folder</folderName>
                </GetServiceDescriptionsEx>
              </soap:Body>
            </soap:Envelope>
            """;
        using var folderContent = new StringContent(nonRootFolderRequest, Encoding.UTF8, "text/xml");
        var folderResponse = await _fixture.Client.PostAsync("/services", folderContent);

        folderResponse.Be200Ok();
        var folderPayload = XDocument.Parse(await folderResponse.Content.ReadAsStringAsync());
        folderPayload.Descendants().Should().ContainSingle(
            element => element.Name.LocalName == "GetServiceDescriptionsExResult");
        folderPayload.Descendants().Should().NotContain(
            element => element.Name.LocalName == "ServiceDescription");

        const string unsupportedArgumentRequest = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetServiceDescriptionsEx xmlns="http://www.esri.com/schemas/ArcGIS/10.8">
                  <serviceType>MapServer</serviceType>
                </GetServiceDescriptionsEx>
              </soap:Body>
            </soap:Envelope>
            """;
        using var unsupportedContent = new StringContent(unsupportedArgumentRequest, Encoding.UTF8, "text/xml");
        var unsupportedResponse = await _fixture.Client.PostAsync("/services", unsupportedContent);

        unsupportedResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var unsupportedPayload = XDocument.Parse(await unsupportedResponse.Content.ReadAsStringAsync());
        unsupportedPayload.Descendants().Should().ContainSingle(element => element.Name.LocalName == "Fault");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetMessageVersion")]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_Soap12_UsesSoap12EnvelopeAndContentType()
    {
        const string request = """
            <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope">
              <soap:Body>
                <GetMessageVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
              </soap:Body>
            </soap:Envelope>
            """;

        using var content = new StringContent(request, Encoding.UTF8, "application/soap+xml");
        var response = await _fixture.Client.PostAsync("/services", content);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/soap+xml");
        var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.Root!.Name.NamespaceName.Should().Be("http://www.w3.org/2003/05/soap-envelope");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_MalformedAndAmbiguousBodies_ReturnSoapFaults()
    {
        var requests = new[]
        {
            "<not-xml",
            """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetFolders xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
                <GetMessageVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
              </soap:Body>
            </soap:Envelope>
            """,
            """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetFolders xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
              </soap:Body>
              <soap:Body>
                <GetMessageVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
              </soap:Body>
            </soap:Envelope>
            """
        };

        foreach (var request in requests)
        {
            using var content = new StringContent(request, Encoding.UTF8, "text/xml");
            var response = await _fixture.Client.PostAsync("/services", content);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
            var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
            payload.Descendants().Should().ContainSingle(element => element.Name.LocalName == "Fault");
        }

        const string validEnvelope = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><GetFolders xmlns="http://www.esri.com/schemas/ArcGIS/10.8" /></soap:Body>
            </soap:Envelope>
            """;
        using var invalidMediaContent = new StringContent(validEnvelope, Encoding.UTF8, "application/json");
        using var invalidMediaResponse = await _fixture.Client.PostAsync("/services", invalidMediaContent);

        invalidMediaResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.UnsupportedMediaType);
        XDocument.Parse(await invalidMediaResponse.Content.ReadAsStringAsync())
            .Descendants().Should().ContainSingle(element => element.Name.LocalName == "Fault");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_OperationOutsideArcGisNamespace_ReturnsSoapFault()
    {
        const string request = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetMessageVersion xmlns="urn:not-arcgis" />
              </soap:Body>
            </soap:Envelope>
            """;

        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        var response = await _fixture.Client.PostAsync("/services", content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.Descendants().Should().ContainSingle(element => element.Name.LocalName == "Fault");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_UnknownService_ReturnsNotFoundFault()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[]
            {
                new RasterInfo { Id = 1, LayerId = 0, Name = "raster", Width = 1, Height = 1, BandCount = 1, PixelType = "8BUI", Srid = 4326 }
            }));
        var fixture = new WebAppFixture().ConfigureServices(services => services.AddSingleton(rasterStore));
        await fixture.InitializeAsync();
        try
        {
            const string request = """
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <GetVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
                  </soap:Body>
                </soap:Envelope>
                """;
            using var content = new StringContent(request, Encoding.UTF8, "text/xml");
            var response = await fixture.Client.PostAsync("/services/not-test/ImageServer", content);

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
            var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
            payload.Descendants().Should().ContainSingle(element => element.Name.LocalName == "Fault");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_RasterProbeFailure_ReturnsSoapFault()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RasterInfo?>(new InvalidOperationException("probe failed")));
        var fixture = new WebAppFixture().ConfigureServices(services => services.AddSingleton(rasterStore));
        await fixture.InitializeAsync();
        try
        {
            using var response = await PostSoapAsync(
                fixture.Client,
                $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                "GetVersion");

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.InternalServerError);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
            var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
            payload.Descendants().Should().ContainSingle(element => element.Name.LocalName == "Fault");
            payload.Descendants().Single(element => element.Name.LocalName == "faultcode")
                .Value.Should().Be("soap:Server");

        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_MetadataProviderFailure_ReturnsSoapFault()
    {
        var provider = Substitute.For<IMetadataV2GraphProvider>();
#pragma warning disable CA2012 // NSubstitute consumes this ValueTask configuration once per invocation.
        provider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<MetadataV2GraphSnapshot>(
                new InvalidOperationException("catalog failed")));
#pragma warning restore CA2012
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.AddSingleton(provider);
        });
        await fixture.InitializeAsync();
        try
        {
            using var response = await PostSoapAsync(fixture.Client, "/services", "GetServiceDescriptions");

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
            var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
            payload.Descendants().Should().ContainSingle(element => element.Name.LocalName == "Fault");
            payload.Descendants().Single(element => element.Name.LocalName == "faultcode")
                .Value.Should().Be("soap:Server");

            const string soap12Request = """
                <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope">
                  <soap:Body>
                    <GetServiceDescriptions xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
                  </soap:Body>
                </soap:Envelope>
                """;
            using var soap12Content = new StringContent(soap12Request, Encoding.UTF8, "application/soap+xml");
            using var soap12Response = await fixture.Client.PostAsync("/services", soap12Content);

            soap12Response.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
            var soap12Payload = XDocument.Parse(await soap12Response.Content.ReadAsStringAsync());
            soap12Payload.Descendants().Single(element => element.Name.LocalName == "Value")
                .Value.Should().Be("soap:Receiver");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_MultipleOperations_ReturnsClientFault()
    {
        const string soapRequest = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetFolders xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
                <GetMessageVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
              </soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(soapRequest, Encoding.UTF8, "text/xml");

        var response = await _fixture.Client.PostAsync("/services", content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.Descendants().Single(element => element.Name.LocalName == "faultstring")
            .Value.Should().Contain("exactly one");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_RasterProbeUsesStorageBindingLayerId()
    {
        const int publicationLayerIndex = 7;
        const int competingStorageLayerId = 9000;
        const int storageLayerId = 9001;
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource(
                "res-soap-competing",
                "Competing SOAP raster",
                MetadataV2ResourceType.RasterDataset,
                accessPolicy: anonymous)
            .AddStorageBinding(
                "binding-soap-competing",
                "res-soap-competing",
                "competing_raster_data",
                storageType: MetadataV2StorageType.RelationalTable,
                storageLayerId: competingStorageLayerId)
            .AddService(
                "competing-image",
                "competing-image",
                protocols: [ServiceProtocols.ImageServer],
                accessPolicy: anonymous)
            .AddPublication(
                "pub-soap-competing",
                "competing-image",
                "res-soap-competing",
                layerIndex: publicationLayerIndex,
                serviceLocalId: publicationLayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .AddResource(
                "res-soap-storage",
                "SOAP storage raster",
                MetadataV2ResourceType.RasterDataset,
                accessPolicy: anonymous)
            .AddStorageBinding(
                "binding-soap-storage",
                "res-soap-storage",
                "raster_data",
                storageType: MetadataV2StorageType.RelationalTable,
                storageLayerId: storageLayerId)
            .AddService(
                "storage-image",
                "storage-image",
                protocols: [ServiceProtocols.ImageServer],
                accessPolicy: anonymous)
            .AddPublication(
                "pub-soap-storage",
                "storage-image",
                "res-soap-storage",
                layerIndex: publicationLayerIndex,
                serviceLocalId: publicationLayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .Build();
        var provider = new TestMetadataV2GraphProvider(graph);
        var rasterStore = Substitute.For<IRasterStore>();
        var competingRaster = CreateSoapRaster(competingStorageLayerId, id: 100);
        var raster = CreateSoapRaster(storageLayerId);
        rasterStore.ListRastersAsync(competingStorageLayerId, Arg.Any<CancellationToken>())
            .Returns([competingRaster]);
        rasterStore.ListRastersAsync(storageLayerId, Arg.Any<CancellationToken>()).Returns([raster]);
        rasterStore.GetPrimaryRasterInfoAsync(competingStorageLayerId, Arg.Any<CancellationToken>())
            .Returns(competingRaster);
        rasterStore.GetPrimaryRasterInfoAsync(storageLayerId, Arg.Any<CancellationToken>()).Returns(raster);
        rasterStore.QueryRastersAsync(
                competingStorageLayerId,
                Arg.Any<RasterSelectionQuery>(),
                Arg.Any<CancellationToken>())
            .Returns([competingRaster]);
        rasterStore.QueryRastersAsync(
                storageLayerId,
                Arg.Any<RasterSelectionQuery>(),
                Arg.Any<CancellationToken>())
            .Returns([raster]);
        rasterStore.ExportImageAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var query = callInfo.ArgAt<RasterQuery>(2);
                return new RasterResult
                {
                    Data = CreateSoapPng(query.OutputWidth ?? 16, query.OutputHeight ?? 8),
                    ContentType = "image/png",
                    Width = query.OutputWidth ?? 16,
                    Height = query.OutputHeight ?? 8,
                    Srid = 4326,
                    Extent = raster.Extent
                };
            });

        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.RemoveAll<IMetadataV2GraphStore>();
            services.AddSingleton<IMetadataV2GraphProvider>(provider);
            services.AddSingleton<IMetadataV2GraphStore>(provider);
            services.AddSingleton(rasterStore);
        });
        await fixture.InitializeAsync();
        try
        {
            using var response = await PostSoapAsync(fixture.Client, "/services", "GetServiceDescriptions");

            response.Be200Ok();
            (await response.Content.ReadAsStringAsync()).Should().Contain("storage-image");
            await rasterStore.Received().ListRastersAsync(storageLayerId, Arg.Any<CancellationToken>());
            await rasterStore.DidNotReceive().ListRastersAsync(publicationLayerIndex, Arg.Any<CancellationToken>());

            const string exportOperation = """
                <ExportImage xmlns="http://www.esri.com/schemas/ArcGIS/10.8"
                             xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <ImageDescription xsi:type="GeoImageDescription">
                    <Extent xsi:type="EnvelopeN">
                      <XMin>-180</XMin><YMin>-90</YMin><XMax>180</XMax><YMax>90</YMax>
                    </Extent>
                    <Width>16</Width><Height>8</Height>
                  </ImageDescription>
                  <ImageType xsi:type="ImageType">
                    <ImageFormat>esriImagePNG</ImageFormat>
                    <ImageReturnType>esriImageReturnURL</ImageReturnType>
                  </ImageType>
                </ExportImage>
                """;
            using var exportResponse = await PostSoapOperationAsync(
                fixture.Client,
                "/services/storage-image/ImageServer",
                exportOperation);

            exportResponse.Be200Ok();
            await rasterStore.Received().ExportImageAsync(
                storageLayerId,
                Arg.Any<long>(),
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>());
            await rasterStore.DidNotReceive().ExportImageAsync(
                competingStorageLayerId,
                Arg.Any<long>(),
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_SelectsRasterBearingPublicationAndUsesReferencePixelSize()
    {
        const int emptyStorageLayerId = 9101;
        const int rasterStorageLayerId = 9102;
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-soap-empty", "Empty raster", MetadataV2ResourceType.RasterDataset, accessPolicy: anonymous)
            .AddStorageBinding(
                "binding-soap-empty",
                "res-soap-empty",
                "empty_raster_data",
                storageType: MetadataV2StorageType.RelationalTable,
                storageLayerId: emptyStorageLayerId)
            .AddResource("res-soap-data", "Raster data", MetadataV2ResourceType.RasterDataset, accessPolicy: anonymous)
            .AddStorageBinding(
                "binding-soap-data",
                "res-soap-data",
                "raster_data",
                storageType: MetadataV2StorageType.RelationalTable,
                storageLayerId: rasterStorageLayerId)
            .AddService(
                "selection-image",
                "selection-image",
                protocols: [ServiceProtocols.ImageServer],
                accessPolicy: anonymous)
            .AddPublication(
                "pub-soap-empty",
                "selection-image",
                "res-soap-empty",
                layerIndex: 0,
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .AddPublication(
                "pub-soap-data",
                "selection-image",
                "res-soap-data",
                layerIndex: 1,
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .Build();
        var provider = new TestMetadataV2GraphProvider(graph);
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.GetPrimaryRasterInfoAsync(emptyStorageLayerId, Arg.Any<CancellationToken>())
            .Returns((RasterInfo?)null);
        var primaryRaster = CreateSoapRaster(
            rasterStorageLayerId,
            id: 201,
            width: 180,
            extent: new RasterExtent { XMin = -180, YMin = -90, XMax = 0, YMax = 90, Srid = 4326 });
        rasterStore.GetPrimaryRasterInfoAsync(rasterStorageLayerId, Arg.Any<CancellationToken>())
            .Returns(primaryRaster);
        rasterStore.ListRastersAsync(rasterStorageLayerId, Arg.Any<CancellationToken>())
            .Returns([
                primaryRaster,
                CreateSoapRaster(
                    rasterStorageLayerId,
                    id: 202,
                    width: 90,
                    extent: new RasterExtent { XMin = 0, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 })
            ]);

        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.RemoveAll<IMetadataV2GraphStore>();
            services.AddSingleton<IMetadataV2GraphProvider>(provider);
            services.AddSingleton<IMetadataV2GraphStore>(provider);
            services.AddSingleton(rasterStore);
        });
        await fixture.InitializeAsync();
        try
        {
            using var response = await PostSoapAsync(
                fixture.Client,
                "/services/selection-image/ImageServer",
                "GetServiceInfo");

            response.Be200Ok();
            var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
            payload.Descendants().Single(element => element.Name.LocalName == "PixelSizeX")
                .Value.Should().Be("1");
            await rasterStore.Received().GetPrimaryRasterInfoAsync(emptyStorageLayerId, Arg.Any<CancellationToken>());
            await rasterStore.Received().GetPrimaryRasterInfoAsync(rasterStorageLayerId, Arg.Any<CancellationToken>());
            await rasterStore.Received().ListRastersAsync(rasterStorageLayerId, Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetVersion")]
    [InterfaceOperation(TestProtocols.ImageServer, "IsFixedScaleImage")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetServiceInfo")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetFields")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetKeyProperties")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetMetadata")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_MetadataFieldsAndVersion_AreArcGisShaped()
    {
        var rasterStore = CreateSoapRasterStore();
        var fixture = new WebAppFixture().ConfigureServices(services => services.AddSingleton(rasterStore));
        await fixture.InitializeAsync();
        try
        {
            using var metadata = await PostSoapAsync(
                fixture.Client,
                $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                "GetServiceInfo");
            metadata.Be200Ok();
            var metadataXml = XDocument.Parse(await metadata.Content.ReadAsStringAsync());
            var result = metadataXml.Descendants().Single(element => element.Name.LocalName == "Result");
            XNamespace arcGis = "http://www.esri.com/schemas/ArcGIS/10.8";
            result.Name.Should().Be(arcGis + "Result");
            result.Descendants().Should().OnlyContain(element => element.Name.Namespace == arcGis);
            result.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?
                .Value.Should().Be("tns:ImageServiceInfo");
            result.Elements().Single(element => element.Name.LocalName == "Name")
                .Value.Should().Be(WebAppFixture.TestServiceId);
            result.Elements().Single(element => element.Name.LocalName == "BandCount")
                .Value.Should().Be("3");
            result.Elements().Single(element => element.Name.LocalName == "AllowedCompressions")
                .Value.Should().Be("None");
            result.Elements().Single(element => element.Name.LocalName == "SupportBSQ")
                .Value.Should().Be("false");
            result.Descendants().Single(element => element.Name.LocalName == "WKID")
                .Value.Should().Be("4326");

            using var fields = await PostSoapAsync(
                fixture.Client,
                $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                "GetFields");
            fields.Be200Ok();
            var fieldsXml = XDocument.Parse(await fields.Content.ReadAsStringAsync());
            fieldsXml.Descendants().Where(element => element.Name.LocalName == "Name")
                .Select(element => element.Value)
                .Should().Contain(["OBJECTID", "Shape", "Name"]);

            using var version = await PostSoapAsync(
                fixture.Client,
                $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                "GetVersion");
            version.Be200Ok();
            var versionXml = XDocument.Parse(await version.Content.ReadAsStringAsync());
            versionXml.Descendants().Single(element => element.Name.LocalName == "Result")
                .Value.Should().Be("10.8");

            using var fixedScale = await PostSoapAsync(
                fixture.Client,
                $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                "IsFixedScaleImage");
            fixedScale.Be200Ok();
            var fixedScaleXml = XDocument.Parse(await fixedScale.Content.ReadAsStringAsync());
            fixedScaleXml.Descendants()
                .Should().ContainSingle(element => element.Name.LocalName == "IsFixedScaleImageResponse");
            fixedScaleXml.Descendants()
                .Should().NotContain(element => element.Name.LocalName == "IsFixedScaleMapResponse");

            foreach (var operation in new[] { "GetKeyProperties", "GetMetadata" })
            {
                using var companion = await PostSoapAsync(
                    fixture.Client,
                    $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                    operation);
                companion.Be200Ok();
                XDocument.Parse(await companion.Content.ReadAsStringAsync())
                    .Descendants().Should().Contain(element => element.Name.LocalName == "Result");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_GetServiceInfo_MapsLowBitPixelTypes()
    {
        foreach (var (postgisPixelType, esriPixelType) in new[]
                 {
                     ("1BB", "U1"),
                     ("2BUI", "U2"),
                     ("4BUI", "U4"),
                 })
        {
            var rasterStore = CreateSoapRasterStore(pixelType: postgisPixelType);
            var fixture = new WebAppFixture().ConfigureServices(services => services.AddSingleton(rasterStore));
            await fixture.InitializeAsync();
            try
            {
                using var response = await PostSoapAsync(
                    fixture.Client,
                    $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                    "GetServiceInfo");

                response.Be200Ok();
                var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
                payload.Descendants().Single(element => element.Name.LocalName == "PixelType")
                    .Value.Should().Be(esriPixelType);
            }
            finally
            {
                await fixture.DisposeAsync();
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Operation(Operations.Export)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_UsesOperationAwareAuthorizationAndMapsTimeouts()
    {
        var resolver = Substitute.For<IImageServerLayerResolver>();
        resolver.ResolveFirstAccessibleLayerAsync(
                Arg.Any<string>(),
                Arg.Any<HttpContext>(),
                Arg.Any<Honua.Core.Features.Authorization.Domain.AuthorizationOperation>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => string.Equals(callInfo.ArgAt<string>(0), "timeout", StringComparison.Ordinal)
                ? Task.FromException<ImageServerLayerResolution>(new OperationCanceledException("server timeout"))
                : Task.FromResult(new ImageServerLayerResolution(0, null, null, Results.NotFound())));
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IImageServerLayerResolver>();
            services.AddSingleton(resolver);
        });
        await fixture.InitializeAsync();
        try
        {
            using var metadataResponse = await PostSoapAsync(
                fixture.Client,
                "/services/metadata/ImageServer",
                "GetVersion");
            using var exportResponse = await PostSoapAsync(
                fixture.Client,
                "/services/export/ImageServer",
                "ExportImage");
            using var timeoutResponse = await PostSoapAsync(
                fixture.Client,
                "/services/timeout/ImageServer",
                "GetVersion");

            metadataResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
            exportResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
            timeoutResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
            XDocument.Parse(await timeoutResponse.Content.ReadAsStringAsync())
                .Descendants().Single(element => element.Name.LocalName == "faultcode")
                .Value.Should().Be("soap:Server");
            await resolver.Received().ResolveFirstAccessibleLayerAsync(
                "metadata",
                Arg.Any<HttpContext>(),
                Honua.Core.Features.Authorization.Domain.AuthorizationOperation.Metadata,
                Arg.Any<CancellationToken>());
            await resolver.Received().ResolveFirstAccessibleLayerAsync(
                "export",
                Arg.Any<HttpContext>(),
                Honua.Core.Features.Authorization.Domain.AuthorizationOperation.Export,
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.ImageServer, "ExportImage")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_ExportImage_DelegatesToCanonicalRasterExport()
    {
        var rasterStore = CreateSoapRasterStore();
        var fixture = new WebAppFixture().ConfigureServices(services => services.AddSingleton(rasterStore));
        await fixture.InitializeAsync();
        try
        {
            const string operation = """
                <ExportImage xmlns="http://www.esri.com/schemas/ArcGIS/10.8"
                             xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <ImageDescription xsi:type="GeoImageDescription">
                    <Extent xsi:type="EnvelopeN">
                      <XMin>-180</XMin><YMin>-90</YMin><XMax>180</XMax><YMax>90</YMax>
                      <SpatialReference xsi:type="GeographicCoordinateSystem"><WKID>4326</WKID></SpatialReference>
                    </Extent>
                    <Width>128</Width><Height>64</Height>
                  </ImageDescription>
                  <ImageType xsi:type="ImageType">
                    <ImageFormat>esriImagePNG</ImageFormat>
                    <ImageReturnType>esriImageReturnURL</ImageReturnType>
                  </ImageType>
                </ExportImage>
                """;
            using var response = await PostSoapOperationAsync(
                fixture.Client,
                $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                operation);

            response.Be200Ok();
            var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
            var result = payload.Descendants().Single(element => element.Name.LocalName == "Result");
            result.Elements().Single(element => element.Name.LocalName == "ImageURL")
                .Value.Should().StartWith("http://localhost/temp/");
            result.Elements().Single(element => element.Name.LocalName == "ImageWidth")
                .Value.Should().Be("128");
            result.Elements().Single(element => element.Name.LocalName == "ImageHeight")
                .Value.Should().Be("64");
            result.Elements().Single(element => element.Name.LocalName == "ImageType")
                .Value.Should().Be("esriImagePNG");
            await rasterStore.Received().ExportImageAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Is<RasterQuery>(query => query.OutputWidth == 128 && query.OutputHeight == 64),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetImage")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_GetImage_ReturnsBipPixelsFollowedByPackedNoDataMask()
    {
        var rasterStore = CreateSoapRasterStore();
        var fixture = new WebAppFixture().ConfigureServices(services => services.AddSingleton(rasterStore));
        await fixture.InitializeAsync();
        try
        {
            const string operation = """
                <GetImage xmlns="http://www.esri.com/schemas/ArcGIS/10.8"
                          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <ImageDescription xsi:type="GeoImageDescription">
                    <Extent xsi:type="EnvelopeN"><XMin>-180</XMin><YMin>-90</YMin><XMax>180</XMax><YMax>90</YMax></Extent>
                    <Width>16</Width><Height>8</Height>
                  </ImageDescription>
                </GetImage>
                """;
            using var response = await PostSoapOperationAsync(
                fixture.Client,
                $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                operation);

            response.Be200Ok();
            var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
            var result = Convert.FromBase64String(
                payload.Descendants().Single(element => element.Name.LocalName == "Result").Value);
            var pixelByteCount = 16 * 8 * 3;
            result.Should().HaveCount(pixelByteCount + ((16 * 8 + 7) / 8));
            result.AsSpan(0, 6).ToArray().Should().Equal(17, 34, 51, 17, 34, 51);
            result.AsSpan(pixelByteCount).ToArray().Should().OnlyContain(value => value == 0xff);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_GetImage_RejectsMultispectralPngProjection()
    {
        var rasterStore = CreateSoapRasterStore(bandCount: 4);
        var fixture = new WebAppFixture().ConfigureServices(services => services.AddSingleton(rasterStore));
        await fixture.InitializeAsync();
        try
        {
            const string operation = """
                <GetImage xmlns="http://www.esri.com/schemas/ArcGIS/10.8"
                          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <ImageDescription xsi:type="GeoImageDescription">
                    <Extent xsi:type="EnvelopeN"><XMin>-180</XMin><YMin>-90</YMin><XMax>180</XMax><YMax>90</YMax></Extent>
                    <Width>16</Width><Height>8</Height>
                  </ImageDescription>
                </GetImage>
                """;
            using var response = await PostSoapOperationAsync(
                fixture.Client,
                $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                operation);

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotImplemented);
            (await response.Content.ReadAsStringAsync()).Should().Contain("raw-sample renderer");
            await rasterStore.DidNotReceive().ExportImageAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_MultipleOperations_ReturnsClientFault()
    {
        const string operation = """
            <GetVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
            <GetFields xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
            """;
        using var response = await PostSoapOperationAsync(
            _fixture.Client,
            $"/services/{WebAppFixture.TestServiceId}/ImageServer",
            operation);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("exactly one");

        const string validEnvelope = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><GetVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" /></soap:Body>
            </soap:Envelope>
            """;
        using var invalidMediaContent = new StringContent(validEnvelope, Encoding.UTF8, "application/json");
        using var invalidMediaResponse = await _fixture.Client.PostAsync(
            $"/services/{WebAppFixture.TestServiceId}/ImageServer",
            invalidMediaContent);

        invalidMediaResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.UnsupportedMediaType);
        XDocument.Parse(await invalidMediaResponse.Content.ReadAsStringAsync())
            .Descendants().Should().ContainSingle(element => element.Name.LocalName == "Fault");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_Soap12_ReturnsSoap12Response()
    {
        var rasterStore = CreateSoapRasterStore();
        var fixture = new WebAppFixture().ConfigureServices(services => services.AddSingleton(rasterStore));
        await fixture.InitializeAsync();
        try
        {
            const string request = """
                <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope">
                  <soap:Body>
                    <GetVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />
                  </soap:Body>
                </soap:Envelope>
                """;
            using var content = new StringContent(request, Encoding.UTF8, "application/soap+xml");
            using var response = await fixture.Client.PostAsync(
                $"/services/{WebAppFixture.TestServiceId}/ImageServer",
                content);

            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/soap+xml");
            XDocument.Parse(await response.Content.ReadAsStringAsync()).Root?.Name.NamespaceName
                .Should().Be("http://www.w3.org/2003/05/soap-envelope");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_DuplicateBodies_ReturnsClientFault()
    {
        const string request = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><GetVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8" /></soap:Body>
              <soap:Body><GetFields xmlns="http://www.esri.com/schemas/ArcGIS/10.8" /></soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        using var response = await _fixture.Client.PostAsync(
            $"/services/{WebAppFixture.TestServiceId}/ImageServer",
            content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("exactly one Body");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoapImageServer_NonArcGisOperationNamespace_ReturnsClientFault()
    {
        const string request = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><GetVersion xmlns="urn:not-arcgis" /></soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        using var response = await _fixture.Client.PostAsync(
            $"/services/{WebAppFixture.TestServiceId}/ImageServer",
            content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("operation namespace");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    public Task SoapCatalog_DtdPayload_ReturnsClientFault()
        => AssertDtdPayloadRejectedAsync("/services");

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public Task SoapImageServer_DtdPayload_ReturnsClientFault()
        => AssertDtdPayloadRejectedAsync("/services/test/ImageServer");

    private async Task AssertDtdPayloadRejectedAsync(string route)
    {
        const string request = """
            <?xml version="1.0"?>
            <!DOCTYPE soap:Envelope [<!ENTITY probe "forbidden">]>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><GetVersion xmlns="http://www.esri.com/schemas/ArcGIS/10.8">&probe;</GetVersion></soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        using var response = await _fixture.Client.PostAsync(route, content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Malformed SOAP request");
    }

    private static IRasterStore CreateSoapRasterStore(int layerId = 0, int bandCount = 3, string pixelType = "8BUI")
    {
        var raster = CreateSoapRaster(layerId, bandCount: bandCount, pixelType: pixelType);
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([raster]);
        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(raster);
        rasterStore.QueryRastersAsync(Arg.Any<int>(), Arg.Any<RasterSelectionQuery>(), Arg.Any<CancellationToken>())
            .Returns([raster]);
        rasterStore.ExportImageAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var query = callInfo.ArgAt<RasterQuery>(2);
                return new RasterResult
                {
                    Data = CreateSoapPng(query.OutputWidth ?? 400, query.OutputHeight ?? 400),
                    ContentType = "image/png",
                    Width = query.OutputWidth ?? 400,
                    Height = query.OutputHeight ?? 400,
                    Srid = 4326,
                    Extent = raster.Extent
                };
            });
        return rasterStore;
    }

    private static byte[] CreateSoapPng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(new SKColor(17, 34, 51, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static RasterInfo CreateSoapRaster(
        int layerId,
        long id = 101,
        int width = 360,
        int height = 180,
        int bandCount = 3,
        RasterExtent? extent = null,
        string pixelType = "8BUI")
        => new()
        {
            Id = id,
            LayerId = layerId,
            Name = "soap-raster",
            Width = width,
            Height = height,
            BandCount = bandCount,
            PixelType = pixelType,
            Srid = 4326,
            Extent = extent ?? new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static Task<HttpResponseMessage> PostSoapAsync(HttpClient client, string route, string operation)
        => PostSoapOperationAsync(
            client,
            route,
            $"<{operation} xmlns=\"http://www.esri.com/schemas/ArcGIS/10.8\" />");

    private static async Task<HttpResponseMessage> PostSoapOperationAsync(
        HttpClient client,
        string route,
        string operation)
    {
        var request = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>{operation}</soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        return await client.PostAsync(route, content).ConfigureAwait(false);
    }
}
