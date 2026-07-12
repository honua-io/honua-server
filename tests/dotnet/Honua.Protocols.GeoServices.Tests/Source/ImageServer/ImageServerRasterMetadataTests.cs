// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// API surface integration tests for the read-only ImageServer raster metadata child
/// resources (statistics, histograms, rasterAttributeTable, rasterFunctionInfos). Each
/// test drives the real route through <see cref="WebAppFixture"/> per ADR-0011 API
/// surface coverage. Endpoint coverage is consolidated onto a small number of methods
/// (multiple <c>[Endpoint]</c> attributes per method) to keep the fixture-init count low.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public class ImageServerRasterMetadataTests
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
            NoDataValue = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        store.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rasterInfo);
        store.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([rasterInfo]);
        store.GetStatisticsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int[]?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
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
        store.GetHistogramsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
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
    [Endpoint("GET /rest/services/{id}/ImageServer/statistics")]
    [Endpoint("POST /rest/services/{id}/ImageServer/statistics")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/statistics")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/statistics")]
    [Operation(Operations.Metadata)]
    public async Task Statistics_GetAndPost_ReturnPerBandStatistics()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(bandCount: 1));
        try
        {
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/statistics?f=json");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            var stats = json.RootElement.GetProperty("statistics");
            stats.GetArrayLength().Should().Be(1);
            stats[0].GetProperty("min").GetDouble().Should().Be(0);
            stats[0].GetProperty("max").GetDouble().Should().Be(255);
            stats[0].GetProperty("mean").GetDouble().Should().Be(128);
            stats[0].GetProperty("standardDeviation").GetDouble().Should().Be(45);

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/statistics",
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("f", "json") }));
            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
            postJson.RootElement.GetProperty("statistics").GetArrayLength().Should().Be(1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/histograms")]
    [Endpoint("POST /rest/services/{id}/ImageServer/histograms")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/histograms")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/histograms")]
    [Operation(Operations.Metadata)]
    public async Task Histograms_GetAndPost_ReturnPerBandHistograms()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(bandCount: 1));
        try
        {
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/histograms?f=json");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            var histograms = json.RootElement.GetProperty("histograms");
            histograms.GetArrayLength().Should().Be(1);
            histograms[0].GetProperty("size").GetInt32().Should().Be(4);
            histograms[0].GetProperty("counts").GetArrayLength().Should().Be(4);

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/histograms",
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("f", "json") }));
            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
            postJson.RootElement.GetProperty("histograms").GetArrayLength().Should().Be(1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/rasterFunctionInfos")]
    [Endpoint("POST /rest/services/{id}/ImageServer/rasterFunctionInfos")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/rasterFunctionInfos")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/rasterFunctionInfos")]
    [Operation(Operations.Metadata)]
    public async Task RasterFunctionInfos_GetAndPost_ListSupportedFunctions()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/rasterFunctionInfos?f=json");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            var infos = json.RootElement.GetProperty("rasterFunctionInfos");
            infos.GetArrayLength().Should().BeGreaterThan(0);
            var names = infos.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
            names.Should().Contain("Stretch");
            names.Should().Contain("Clip");

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/rasterFunctionInfos",
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("f", "json") }));
            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/rasterAttributeTable")]
    [Endpoint("POST /rest/services/{id}/ImageServer/rasterAttributeTable")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/rasterAttributeTable")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/rasterAttributeTable")]
    [Operation(Operations.Metadata)]
    public async Task RasterAttributeTable_GetAndPost_ReturnSchemaWithNoRows()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/rasterAttributeTable?f=json");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("objectIdFieldName").GetString().Should().Be("OBJECTID");
            json.RootElement.GetProperty("fields").GetArrayLength().Should().BeGreaterThan(0);
            // Continuous rasters carry no value/attribute table, so features is empty but present.
            json.RootElement.GetProperty("features").GetArrayLength().Should().Be(0);

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/rasterAttributeTable",
                new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("f", "json") }));
            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/colormap")]
    [Endpoint("POST /rest/services/{id}/ImageServer/colormap")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/colormap")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/colormap")]
    [Operation(Operations.Metadata)]
    public async Task Colormap_WithColormapRenderingRule_ReturnsResolvedStops()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string renderingRule =
                """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colormap":[[0,0,0,0],[255,255,255,255]]}}""";
            var encoded = Uri.EscapeDataString(renderingRule);

            // Numeric-layer GET.
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/colormap?f=json&renderingRule={encoded}");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            var colormap = json.RootElement.GetProperty("colormap");
            colormap.GetArrayLength().Should().Be(2);
            var lastStop = colormap[colormap.GetArrayLength() - 1];
            colormap[0][0].GetInt32().Should().Be(0);
            colormap[0][1].GetInt32().Should().Be(0);
            lastStop[0].GetInt32().Should().Be(255);
            lastStop[1].GetInt32().Should().Be(255);
            // The colormap resource emits [value, r, g, b] stops (no alpha channel).
            lastStop.GetArrayLength().Should().Be(4);

            // Service-name GET resolves the same colormap.
            var byServiceResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/colormap?f=json&renderingRule={encoded}");
            byServiceResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            // POST mirror accepts the renderingRule via the form body.
            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/colormap",
                new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("renderingRule", renderingRule),
                }));
            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
            postJson.RootElement.GetProperty("colormap").GetArrayLength().Should().Be(2);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/colormap")]
    [Operation(Operations.Metadata)]
    public async Task Colormap_WithInlineColorrampRenderingRule_ReturnsResolvedStops()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string renderingRule =
                """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colorramp":{"type":"algorithmic","fromColor":[0,0,0],"toColor":[255,255,255],"algorithm":"esriHSVAlgorithm"}}}""";
            var encoded = Uri.EscapeDataString(renderingRule);

            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/colormap?f=json&renderingRule={encoded}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var colormap = json.RootElement.GetProperty("colormap");
            colormap.GetArrayLength().Should().BeGreaterThan(1);
            // Gradient spans black -> white across the display range.
            colormap[0][0].GetInt32().Should().Be(0);
            colormap[colormap.GetArrayLength() - 1][0].GetInt32().Should().Be(255);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/colormap")]
    [Operation(Operations.Metadata)]
    public async Task Colormap_WithoutRenderer_ReturnsNotAvailable()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            // No renderingRule: a continuous raster has no intrinsic colormap -> not available.
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/colormap?f=json");
            await response.AssertGeoServicesErrorAsync(400);

            // A supported renderingRule that carries no Colormap function is likewise not available.
            const string stretchOnly =
                """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}""";
            var stretchResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/colormap?f=json&renderingRule={Uri.EscapeDataString(stretchOnly)}");
            await stretchResponse.AssertGeoServicesErrorAsync(400);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/statistics")]
    [Operation(Operations.Metadata)]
    public async Task RasterMetadata_InvalidFormatAndMissingLayer_ReturnErrors()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var invalidFormat = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/statistics?f=xml");
            await invalidFormat.AssertGeoServicesErrorAsync(400);

            var missingLayer = await fixture.Client.GetAsync(
                "/rest/services/99999/ImageServer/histograms?f=json");
            await missingLayer.AssertGeoServicesErrorAsync(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
