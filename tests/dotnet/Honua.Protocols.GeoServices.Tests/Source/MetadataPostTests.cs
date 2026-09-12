// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Regression coverage for #1298: Esri clients (ArcGIS Pro, ArcGIS Python SDK)
// hydrate service/layer metadata by POSTing {"f":"json"} to the REST resource
// roots. Honua previously returned 405 on those POSTs, breaking SDK hydration.
// These tests assert each metadata root now accepts POST and returns the same
// payload as the GET form.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

/// <summary>
/// Verifies the GeoServices metadata endpoints accept POST (Esri SDK hydration
/// path) and return metadata identical to the GET form.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class MetadataPostTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    public MetadataPostTests(WebAppFixture fixture) => _fixture = fixture;

    private static FormUrlEncodedContent EmptyJsonForm()
        => new(new[] { new KeyValuePair<string, string>("f", "json") });

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServer_ServiceMetadata_Post_ReturnsSameAsGet()
    {
        var getResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer?f=json");
        var postResponse = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer", EmptyJsonForm());

        postResponse.Be200Ok();
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var getBody = await getResponse.Content.ReadAsStringAsync();
        var postBody = await postResponse.Content.ReadAsStringAsync();

        using var getDoc = JsonDocument.Parse(getBody);
        using var postDoc = JsonDocument.Parse(postBody);
        postDoc.RootElement.GetProperty("serviceName").GetString().Should().Be(TestServiceId);
        // Honua does not advertise an ArcGIS Server version (see NoArcGisServerVersionTests).
        postDoc.RootElement.TryGetProperty("currentVersion", out _).Should().BeFalse();
        postBody.Should().Be(getBody);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task FeatureServer_LayerMetadata_Post_ReturnsSameAsGet()
    {
        var getResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}?f=json");
        var postResponse = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}", EmptyJsonForm());

        postResponse.Be200Ok();
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var getBody = await getResponse.Content.ReadAsStringAsync();
        var postBody = await postResponse.Content.ReadAsStringAsync();

        using var postDoc = JsonDocument.Parse(postBody);
        postDoc.RootElement.GetProperty("id").GetInt32().Should().Be(TestLayerId);
        postBody.Should().Be(getBody);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer")]
    public async Task MapServer_ServiceMetadata_Post_ReturnsSameAsGet()
    {
        var getResponse = await _fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer?f=json");
        var postResponse = await _fixture.Client.PostAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer", EmptyJsonForm());

        var postBody = await postResponse.Content.ReadAsStringAsync();
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, postBody);
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var getBody = await getResponse.Content.ReadAsStringAsync();
        using var postDoc = JsonDocument.Parse(postBody);
        postDoc.RootElement.GetProperty("mapName").GetString().Should().NotBeNullOrWhiteSpace();
        postBody.Should().Be(getBody);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task MapServer_LayerMetadata_Post_ReturnsSameAsGet()
    {
        var getResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}?f=json");
        var postResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}", EmptyJsonForm());

        var postBody = await postResponse.Content.ReadAsStringAsync();
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, postBody);
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var getBody = await getResponse.Content.ReadAsStringAsync();
        using var postDoc = JsonDocument.Parse(postBody);
        postDoc.RootElement.GetProperty("id").GetInt32().Should().Be(WebAppFixture.TestLayerId);
        postBody.Should().Be(getBody);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /rest/services/{id}/ImageServer")]
    public async Task ImageServer_ServiceInfo_Post_MatchesGet()
    {
        using var factory = CreateImageMetadataFactory(out _);
        using var reader = ServiceRbacTestFixture.CreateClient(factory, "raster-reader");
        await AssertImageMetadataAsync(reader, "0");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer")]
    public async Task ImageServer_ServiceInfoByService_Post_MatchesGet()
    {
        using var factory = CreateImageMetadataFactory(out _);
        using var reader = ServiceRbacTestFixture.CreateClient(factory, "raster-reader");
        await AssertImageMetadataAsync(reader, "test");
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /rest/services/{id}/ImageServer")]
    public async Task ImageServer_ServiceInfo_Post_DeniedPrincipalsDiscloseNothingAndChangeNothing()
        => await AssertImageMetadataDeniedAsync("0");

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer")]
    public async Task ImageServer_ServiceInfoByService_Post_DeniedPrincipalsDiscloseNothingAndChangeNothing()
        => await AssertImageMetadataDeniedAsync("test");

    private static async Task AssertImageMetadataAsync(HttpClient reader, string serviceId)
    {
        using var getResponse = await reader.GetAsync($"/rest/services/{serviceId}/ImageServer?f=json");
        using var form = EmptyJsonForm();
        using var postResponse = await reader.PostAsync($"/rest/services/{serviceId}/ImageServer", form);
        var postBody = await postResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, postBody);
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        postBody.Should().Be(await getResponse.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(postBody);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(postBody);
        root.GetProperty("name").GetString().Should().Be("Evidence raster");
        root.GetProperty("bandCount").GetInt32().Should().Be(1);
        root.GetProperty("pixelType").GetString().Should().Be("U8");
        root.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        var extent = root.GetProperty("extent");
        extent.GetProperty("xmin").GetDouble().Should().Be(-160);
        extent.GetProperty("ymin").GetDouble().Should().Be(18);
        extent.GetProperty("xmax").GetDouble().Should().Be(-154);
        extent.GetProperty("ymax").GetDouble().Should().Be(23);
        root.GetProperty("minValues").EnumerateArray().Select(value => value.GetDouble()).Should().Equal(7);
        root.GetProperty("maxValues").EnumerateArray().Select(value => value.GetDouble()).Should().Equal(91);
        root.GetProperty("meanValues").EnumerateArray().Select(value => value.GetDouble()).Should().Equal(42);
    }

    private static async Task AssertImageMetadataDeniedAsync(string serviceId)
    {
        using var factory = CreateImageMetadataFactory(out var rasterStore);
        using var reader = ServiceRbacTestFixture.CreateClient(factory, "raster-reader");
        await AssertImageMetadataAsync(reader, serviceId);
        var provider = factory.Services.GetRequiredService<IMetadataV2GraphProvider>();
        var before = await provider.GetCurrentAsync();
        rasterStore.ClearReceivedCalls();
        using var anonymous = factory.CreateClient();
        using var wrongRole = ServiceRbacTestFixture.CreateClient(factory, "other-role");
        foreach (var (client, code) in new[] { (anonymous, 499), (wrongRole, 403) })
        {
            using var form = EmptyJsonForm();
            using var response = await client.PostAsync($"/rest/services/{serviceId}/ImageServer", form);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            document.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal("error");
            document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(code);
            body.Should().NotContain("Evidence raster").And.NotContain("evidence-pixels");
        }

        rasterStore.ReceivedCalls().Should().BeEmpty("denied metadata requests must neither read nor mutate raster storage");
        (await provider.GetCurrentAsync()).Should().BeEquivalentTo(before);
        await AssertImageMetadataAsync(reader, serviceId);
    }

    private static WebApplicationFactory<Program> CreateImageMetadataFactory(out IRasterStore rasterStore)
    {
        var store = Substitute.For<IRasterStore>();
        var raster = new RasterInfo
        {
            Id = 73,
            LayerId = 0,
            Name = "evidence-pixels",
            Width = 6,
            Height = 5,
            BandCount = 1,
            PixelType = "8BUI",
            Srid = 4326,
            GeoTransform = [-160, 1, 0, 23, 0, -1],
            Extent = new RasterExtent { XMin = -160, YMin = 18, XMax = -154, YMax = 23, Srid = 4326 },
            CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
        };
        store.ListRastersAsync(0, Arg.Any<CancellationToken>()).Returns([raster]);
        store.GetPrimaryRasterInfoAsync(0, Arg.Any<CancellationToken>()).Returns(raster);
        store.GetStatisticsAsync(0, 73, Arg.Any<int[]?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns([new RasterStatistics { Band = 1, MinValue = 7, MaxValue = 91, MeanValue = 42, StandardDeviation = 3, ValidPixelCount = 30 }]);
        rasterStore = store;
        return ServiceRbacTestFixture.CreateFactory(() => new ImageMetadataCatalog(), services => services.AddSingleton(store));
    }

    private sealed class ImageMetadataCatalog : ITestMetadataV2GraphSource
    {
        public TestMetadataV2GraphProvider BuildProvider()
        {
            var policy = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["raster-reader"]);
            return new TestMetadataV2GraphBuilder()
                .AddResource("evidence-raster", "Evidence raster", MetadataV2ResourceType.RasterDataset, accessPolicy: policy)
                .AddStorageBinding("evidence-binding", "evidence-raster", "raster_data", storageLayerId: 0)
                .AddService("evidence-service", "test", protocols: [ServiceProtocols.ImageServer], accessPolicy: policy)
                .AddPublication("evidence-publication", "evidence-service", "evidence-raster", layerIndex: 0,
                    storageBindingId: "evidence-binding", serviceLocalId: "Evidence raster", publicationType: MetadataV2PublicationType.EsriImageLayer)
                .BuildProvider();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer")]
    public async Task GeometryServer_Info_Post_ReturnsSameAsGet()
    {
        var getResponse = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer");
        var postResponse = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer", EmptyJsonForm());

        postResponse.Be200Ok();
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var getBody = await getResponse.Content.ReadAsStringAsync();
        var postBody = await postResponse.Content.ReadAsStringAsync();
        using var postDoc = JsonDocument.Parse(postBody);
        // Honua does not advertise an ArcGIS Server version (see NoArcGisServerVersionTests).
        postDoc.RootElement.TryGetProperty("currentVersion", out _).Should().BeFalse();
        postBody.Should().Be(getBody);
    }
}
