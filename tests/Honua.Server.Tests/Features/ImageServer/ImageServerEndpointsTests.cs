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
/// (catalog query, computeStatisticsHistograms, legend, computeClass).
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
        store.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([rasterInfo]);
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
    public async Task ComputeStatisticsHistograms_Get_ReturnsStatisticsAndHistograms()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeStatisticsHistograms?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("statistics").GetArrayLength().Should().Be(1);
            json.RootElement.GetProperty("histograms").GetArrayLength().Should().Be(1);
            json.RootElement.GetProperty("statistics")[0].GetProperty("min").GetDouble().Should().Be(0);
            json.RootElement.GetProperty("statistics")[0].GetProperty("max").GetDouble().Should().Be(255);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/computeStatisticsHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_Post_FormBody_ReturnsStatistics()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("histogramParameters", "{\"size\":32}"),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeStatisticsHistograms",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("statistics").GetArrayLength().Should().Be(1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeStatisticsHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_RasterIdsAsCatalogIds_LooksUpRaster()
    {
        // Regression: rasterIds in the Esri spec are catalog object IDs (long),
        // not band indices. The handler must look the raster up via the catalog
        // and never fall back to GetPrimaryRasterInfoAsync.
        var rasterStore = CreateRasterStoreSubstitute();
        var fixture = await CreateFixtureAsync(rasterStore);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeStatisticsHistograms?f=json&rasterIds=100");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            await rasterStore.Received().GetRasterInfoAsync(TestLayerId, 100L, Arg.Any<CancellationToken>());
            await rasterStore.DidNotReceive().GetPrimaryRasterInfoAsync(TestLayerId, Arg.Any<CancellationToken>());
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
    [Endpoint("GET /rest/services/{id}/ImageServer/computeClass")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClass_Get_ReturnsAnalyzedFunctionChain()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string renderingRule = """{"rasterFunction":"Identity"}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClass?f=json&renderingRule={Uri.EscapeDataString(renderingRule)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("rasterFunction").GetString().Should().Be("Identity");
            json.RootElement.GetProperty("status").GetString().Should().Be("success");
            json.RootElement.GetProperty("chainDepth").GetInt32().Should().BeGreaterThan(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/computeClass")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClass_Post_FormBody_ReturnsAnalyzedFunctionChain()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("renderingRule", """{"rasterFunction":"Identity"}"""),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClass",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("rasterFunction").GetString().Should().Be("Identity");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeClass")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClass_MissingRenderingRule_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClass?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
