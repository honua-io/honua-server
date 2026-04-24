// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.ImageServer;

/// <summary>
/// API surface integration tests for the ImageServer endpoints added in #520
/// (catalog query, computeStatisticsHistograms, legend, computeClassStatistics).
/// Each test exercises the actual route through <see cref="WebAppFixture"/> so
/// route binding, telemetry, JSON formatting, and error handling are all covered
/// per ADR-0011 API Surface Coverage.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ImageServer)]
public class ImageServerEndpointsTests
{
    private const int TestLayerId = 0;

    private static IRasterStore CreateRasterStoreSubstitute(int bandCount = 1)
    {
        var store = Substitute.For<IRasterStore>();
        var rasterInfo = new RasterInfo
        {
            Id = 100,
            LayerId = TestLayerId,
            Name = "test-raster",
            Width = 256,
            Height = 256,
            BandCount = bandCount,
            PixelType = "8BUI",
            Srid = 4326,
            GeoTransform = [-180, 1.40625, 0, 90, 0, -0.703125],
            Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            CreatedAt = DateTimeOffset.UtcNow,
        };

        store.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(rasterInfo);
        store.GetRasterInfoAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(rasterInfo);
        store.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([rasterInfo]);
        store.GetStatisticsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int[]?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterStatistics
                {
                    Band = 1,
                    MinValue = 0,
                    MaxValue = 255,
                    MeanValue = 128,
                    StandardDeviation = 45,
                    ValidPixelCount = 65536,
                    NoDataPixelCount = 0,
                },
            });
        store.GetHistogramsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterHistogram
                {
                    Band = 1,
                    BinCount = 4,
                    Min = 0,
                    Max = 255,
                    Counts = [10, 20, 30, 40],
                },
            });

        return store;
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(IRasterStore rasterStore)
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(services => services.AddSingleton(rasterStore));
        await fixture.InitializeAsync();
        return fixture;
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_Get_ReturnsFeatureCollection()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/query?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("geometryType").GetString().Should().Be("esriGeometryPolygon");
            json.RootElement.GetProperty("features").GetArrayLength().Should().Be(1);
            json.RootElement.GetProperty("features")[0]
                .GetProperty("attributes")
                .GetProperty("OBJECTID").GetInt64().Should().Be(100);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_Post_FormBody_ReturnsFeatureCollection()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("returnGeometry", "false"),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/query",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("features")[0]
                .TryGetProperty("geometry", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_NonExistentLayer_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                "/rest/services/99999/ImageServer/query?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeStatisticsHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_Get_WithoutGeometry_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeStatisticsHistograms?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/computeStatisticsHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_Post_WithGeometry_ReturnsNotImplemented()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("geometryType", "esriGeometryEnvelope"),
                new KeyValuePair<string, string>("geometry", "{\"xmin\":-180,\"ymin\":-90,\"xmax\":180,\"ymax\":90,\"spatialReference\":{\"wkid\":4326}}"),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeStatisticsHistograms",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/legend")]
    [Operation(Operations.Metadata)]
    public async Task GetLegend_Get_ReturnsSwatches()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/legend?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.TryGetProperty("layers", out var layers).Should().BeTrue();
            layers.GetArrayLength().Should().BeGreaterThan(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/legend")]
    [Operation(Operations.Metadata)]
    public async Task GetLegend_InvalidFormat_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/legend?f=xml");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeClassStatistics")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClassStatistics_Get_WithClassDescriptions_ReturnsNotImplemented()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string classDescriptions = """{"classes":[{"id":1,"name":"water","geometry":{"rings":[[[-1,-1],[-1,1],[1,1],[1,-1],[-1,-1]]]}}]}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClassStatistics?f=json&classDescriptions={Uri.EscapeDataString(classDescriptions)}");

            response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/computeClassStatistics")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClassStatistics_Post_FormBody_ReturnsNotImplemented()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("classDescriptions", """{"classes":[{"id":1,"name":"water","geometry":{"rings":[[[-1,-1],[-1,1],[1,1],[1,-1],[-1,-1]]]}}]}"""),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClassStatistics",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeClassStatistics")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClassStatistics_MissingClassDescriptions_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClassStatistics?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
