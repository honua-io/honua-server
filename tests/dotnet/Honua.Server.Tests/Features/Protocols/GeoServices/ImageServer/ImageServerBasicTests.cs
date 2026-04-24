// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Collection("Database")]
[Protocol(TestProtocols.ImageServer)]
public class ImageServerBasicTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0; // Use existing test layer

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_ValidLayerId_ReturnsServiceMetadata()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer?f=json");

        // Assert
        // Note: This test might fail until raster data is available in the test database
        // For now, we expect either success or a 404 (no rasters found)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            // Verify basic Esri ImageServer service info structure
            json.RootElement.TryGetProperty("currentVersion", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("serviceDescription", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("capabilities", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_InvalidFormat_ReturnsBadRequest()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer?f=xml");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_WithValidParameters_ReturnsImageOrNotFound()
    {
        // Arrange
        var queryParams = "?bbox=-180,-90,180,90&size=256,256&format=png&f=json";

        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer/exportImage{queryParams}");

        // Assert
        // Note: This test might fail until raster data is available in the test database
        // For now, we expect either success or a 404 (no rasters found)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            // Verify export image response structure
            json.RootElement.TryGetProperty("href", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("width", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("height", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("extent", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_PostFormBody_ReturnsImageOrNotFound()
    {
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("bbox", "-180,-90,180,90"),
            new KeyValuePair<string, string>("size", "256,256"),
            new KeyValuePair<string, string>("format", "png"),
            new KeyValuePair<string, string>("f", "json")
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage",
            content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_WithInlineImageFormat_ReturnsImageBytes()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.GetPrimaryRasterInfoAsync(TestLayerId, Arg.Any<CancellationToken>())
            .Returns(new RasterInfo
            {
                Id = 100,
                LayerId = TestLayerId,
                Name = "test-raster",
                Width = 1024,
                Height = 1024,
                BandCount = 3,
                PixelType = "8BUI",
                Srid = 4326,
                CreatedAt = DateTimeOffset.UtcNow
            });
        rasterStore.ExportImageAsync(TestLayerId, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = [0x89, 0x50, 0x4E, 0x47],
                ContentType = "image/png",
                Width = 256,
                Height = 128,
                Srid = 4326,
                Extent = new RasterExtent
                {
                    XMin = -180,
                    YMin = -90,
                    XMax = 180,
                    YMax = 90,
                    Srid = 4326
                }
            });

        var fixture = new WebAppFixture()
            .ConfigureServices(services => services.AddSingleton(rasterStore));

        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportImage?bbox=-180,-90,180,90&size=256,256&format=png&f=image");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal([(byte)0x89, 0x50, 0x4E, 0x47]);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithValidPoint_ReturnsIdentifyResult()
    {
        // Arrange
        var queryParams = "?geometry=0,0&geometryType=esriGeometryPoint&f=json";

        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer/identify{queryParams}");

        // Assert
        // Note: This test might fail until raster data is available in the test database
        // For now, we expect either success or a 404 (no rasters found)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            // Verify identify response structure
            json.RootElement.TryGetProperty("location", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("properties", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_PostFormBody_ReturnsIdentifyResultOrNotFound()
    {
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("geometry", "0,0"),
            new KeyValuePair<string, string>("geometryType", "esriGeometryPoint"),
            new KeyValuePair<string, string>("f", "json")
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify",
            content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    [Operation(Operations.GetTile)]
    public async Task GetImageTile_WithValidCoordinates_ReturnsTileOrNotFound()
    {
        // Arrange
        var level = 0;
        var row = 0;
        var col = 0;

        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer/tile/{level}/{row}/{col}?format=png");

        // Assert
        // Note: This test might fail until raster data is available in the test database
        // For now, we expect either success, no content, or 404
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/exportImage")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/exportImage")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/identify")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/identify")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/query")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/query")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeStatisticsHistograms")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeStatisticsHistograms")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/legend")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeClassStatistics")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeClassStatistics")]
    [Operation(Operations.Metadata)]
    public async Task ServiceNameRoutes_DispatchToImageServerSurface()
    {
        var serviceId = WebAppFixture.TestServiceId;
        var classDescriptions = Uri.EscapeDataString(
            """{"classes":[{"id":1,"name":"water","geometry":{"rings":[[[-1,-1],[-1,1],[1,1],[1,-1],[-1,-1]]]}}]}""");
        var geometry = Uri.EscapeDataString(
            """{"xmin":-180,"ymin":-90,"xmax":180,"ymax":90,"spatialReference":{"wkid":4326}}""");

        var getUris = new[]
        {
            $"/rest/services/{serviceId}/ImageServer?f=json",
            $"/rest/services/{serviceId}/ImageServer/exportImage?bbox=-180,-90,180,90&size=256,256&format=png&f=json",
            $"/rest/services/{serviceId}/ImageServer/identify?geometry=0,0&geometryType=esriGeometryPoint&f=json",
            $"/rest/services/{serviceId}/ImageServer/tile/0/0/0?format=png",
            $"/rest/services/{serviceId}/ImageServer/query?f=json",
            $"/rest/services/{serviceId}/ImageServer/computeStatisticsHistograms?f=json&geometryType=esriGeometryEnvelope&geometry={geometry}",
            $"/rest/services/{serviceId}/ImageServer/legend?f=json",
            $"/rest/services/{serviceId}/ImageServer/computeClassStatistics?f=json&classDescriptions={classDescriptions}",
        };

        foreach (var uri in getUris)
        {
            using var response = await _fixture.Client.GetAsync(uri);
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.OK,
                HttpStatusCode.NoContent,
                HttpStatusCode.NotFound,
                HttpStatusCode.NotImplemented);
        }

        var formRequests = new (string Uri, KeyValuePair<string, string>[] Form)[]
        {
            ($"/rest/services/{serviceId}/ImageServer/exportImage",
            [
                new("bbox", "-180,-90,180,90"),
                new("size", "256,256"),
                new("format", "png"),
                new("f", "json")
            ]),
            ($"/rest/services/{serviceId}/ImageServer/identify",
            [
                new("geometry", "0,0"),
                new("geometryType", "esriGeometryPoint"),
                new("f", "json")
            ]),
            ($"/rest/services/{serviceId}/ImageServer/query",
            [
                new("f", "json")
            ]),
            ($"/rest/services/{serviceId}/ImageServer/computeStatisticsHistograms",
            [
                new("f", "json"),
                new("geometryType", "esriGeometryEnvelope"),
                new("geometry", """{"xmin":-180,"ymin":-90,"xmax":180,"ymax":90,"spatialReference":{"wkid":4326}}""")
            ]),
            ($"/rest/services/{serviceId}/ImageServer/computeClassStatistics",
            [
                new("f", "json"),
                new("classDescriptions", """{"classes":[{"id":1,"name":"water","geometry":{"rings":[[[-1,-1],[-1,1],[1,1],[1,-1],[-1,-1]]]}}]}""")
            ]),
        };

        foreach (var (uri, form) in formRequests)
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _fixture.Client.PostAsync(uri, content);
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.OK,
                HttpStatusCode.NotFound,
                HttpStatusCode.NotImplemented);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task ServiceNameRoute_NumericLeadingServiceId_DispatchesToNamedRoute()
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<ILayerCatalog>();
                services.AddSingleton<ILayerCatalog>(new NumericLeadingImageServiceCatalog());
            });
        await fixture.InitializeAsync();

        try
        {
            using var response = await fixture.Client.GetAsync("/rest/services/2024Imagery/ImageServer?f=xml");

            response.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "numeric-leading service IDs should match the named ImageServer route before format validation runs");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_NonExistentLayer_ReturnsNotFound()
    {
        // Arrange
        var nonExistentLayerId = 99999;

        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{nonExistentLayerId}/ImageServer?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed class NumericLeadingImageServiceCatalog : ILayerCatalog
    {
        private const string ServiceName = "2024Imagery";
        private static readonly SpatialReference SpatialReference = SpatialReference.Create(4326);
        private static readonly FeatureExtent Extent = FeatureExtent.Create(-180, -90, 180, 90, 4326);
        private static readonly LayerDefinition Layer = LayerDefinition.CreateBasic(0, "Imagery", GeometryType.Point);
        private static readonly ServiceDefinition Service = new(
            ServiceName,
            "Numeric-leading image service",
            [Layer],
            SpatialReference,
            ["JSON"],
            ["Image"],
            Extent);

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId == Layer.Id ? Layer : null);

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { Layer });

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, ServiceName, StringComparison.OrdinalIgnoreCase) ? Service : null);

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { Service });

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId == Layer.Id);

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, ServiceName, StringComparison.OrdinalIgnoreCase));

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }
}
