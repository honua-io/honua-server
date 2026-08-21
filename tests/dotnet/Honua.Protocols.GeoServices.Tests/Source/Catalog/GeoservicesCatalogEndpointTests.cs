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
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
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
    [Endpoint("POST /services")]
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
            var description = payload.Descendants()
                .Where(element => element.Name.LocalName == "ServiceDescription")
                .First(element => element.Elements()
                    .Any(child => child.Name.LocalName == "Name" && child.Value == "test"));
            description.Elements()
                .Single(element => element.Name.LocalName == "Name")
                .Value.Should().Be("test");
            description.Elements()
                .Single(element => element.Name.LocalName == "Type")
                .Value.Should().Be("ImageServer");
            description.Elements()
                .Single(element => element.Name.LocalName == "Url")
                .Value.Should().EndWith("/services/test/ImageServer");
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
        const int storageLayerId = 9001;
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        var graph = new TestMetadataV2GraphBuilder()
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
                "svc-soap-storage",
                "storage-image",
                protocols: [ServiceProtocols.ImageServer],
                accessPolicy: anonymous)
            .AddPublication(
                "pub-soap-storage",
                "svc-soap-storage",
                "res-soap-storage",
                layerIndex: publicationLayerIndex,
                serviceLocalId: publicationLayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .Build();
        var provider = new TestMetadataV2GraphProvider(graph);
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.ListRastersAsync(storageLayerId, Arg.Any<CancellationToken>())
            .Returns([CreateSoapRaster(storageLayerId)]);

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
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
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
    [Operation(Operations.Export)]
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
        using var response = await _fixture.Client.PostAsync(
            route,
            new StringContent(request, Encoding.UTF8, "text/xml"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Malformed SOAP request");
    }

    private static IRasterStore CreateSoapRasterStore(int layerId = 0)
    {
        var raster = CreateSoapRaster(layerId);
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([raster]);
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

    private static RasterInfo CreateSoapRaster(int layerId)
        => new()
        {
            Id = 101,
            LayerId = layerId,
            Name = "soap-raster",
            Width = 360,
            Height = 180,
            BandCount = 3,
            PixelType = "8BUI",
            Srid = 4326,
            Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static Task<HttpResponseMessage> PostSoapAsync(HttpClient client, string route, string operation)
        => PostSoapOperationAsync(
            client,
            route,
            $"<{operation} xmlns=\"http://www.esri.com/schemas/ArcGIS/10.8\" />");

    private static Task<HttpResponseMessage> PostSoapOperationAsync(
        HttpClient client,
        string route,
        string operation)
    {
        var request = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>{operation}</soap:Body>
            </soap:Envelope>
            """;
        return client.PostAsync(route, new StringContent(request, Encoding.UTF8, "text/xml"));
    }
}
