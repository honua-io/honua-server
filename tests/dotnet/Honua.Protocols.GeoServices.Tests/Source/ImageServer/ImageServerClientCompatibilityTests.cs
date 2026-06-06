// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Client-shaped ImageServer coverage over real HTTP routes with a deterministic
/// raster store. This fills the gap between handler tests and external clients.
/// </summary>
[Collection("Database.GeoServicesParallel2")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerClientCompatibilityTests
{
    private const int TestLayerId = 0;

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Endpoint("GET /rest/services/{id}/ImageServer/query")]
    [Endpoint("GET /rest/services/{id}/ImageServer/legend")]
    public async Task TypedClient_ReadsMetadataCatalogAndLegend()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var client = new ImageServerClient(fixture.Client, TestLayerId);

            var metadata = await client.GetServiceInfoAsync();
            var catalogCount = await client.QueryCatalogCountAsync();
            var legendCount = await client.GetLegendClassCountAsync();

            metadata.CurrentVersion.Should().Be(10.81);
            metadata.BandCount.Should().Be(1);
            metadata.PixelType.Should().Be("U8");
            metadata.Capabilities.Should().Contain("Image");
            catalogCount.Should().Be(1);
            legendCount.Should().BeGreaterThan(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task TypedClient_ExportsInlinePngImage()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var client = new ImageServerClient(fixture.Client, TestLayerId);

            var export = await client.ExportInlinePngAsync();

            export.ContentType.Should().Be("image/png");
            export.Data.Take(8).Should().Equal([(byte)0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task TypedClient_ReceivesEsriErrorShapeForInvalidRequests()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var client = new ImageServerClient(fixture.Client, TestLayerId);

            var metadataError = await client.GetInvalidFormatErrorAsync();
            var exportError = await client.ExportInvalidFormatErrorAsync();

            metadataError.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            metadataError.Code.Should().Be(400);
            metadataError.Message.Should().Be("Bad Request");
            metadataError.Details.Should().Contain(detail => detail.Contains("Only JSON format is supported", StringComparison.Ordinal));

            exportError.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            exportError.Code.Should().Be(400);
            exportError.Details.Should().Contain(detail => detail.Contains("Only JSON and image formats are supported", StringComparison.Ordinal));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<WebAppFixture> CreateFixtureAsync()
    {
        var rasterStore = CreateRasterStore();
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRasterStore>();
                services.AddSingleton(rasterStore);
            });

        await fixture.InitializeAsync();
        return fixture;
    }

    private static IRasterStore CreateRasterStore()
    {
        var extent = new RasterExtent
        {
            XMin = -180,
            YMin = -90,
            XMax = 180,
            YMax = 90,
            Srid = 4326,
        };

        var rasterInfo = new RasterInfo
        {
            Id = 100,
            LayerId = TestLayerId,
            Name = "client-raster",
            Width = 256,
            Height = 256,
            BandCount = 1,
            PixelType = "8BUI",
            Srid = 4326,
            GeoTransform = [-180, 1.40625, 0, 90, 0, -0.703125],
            Extent = extent,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var store = Substitute.For<IRasterStore>();
        store.GetPrimaryRasterInfoAsync(TestLayerId, Arg.Any<CancellationToken>())
            .Returns(rasterInfo);
        store.GetRasterInfoAsync(TestLayerId, 100, Arg.Any<CancellationToken>())
            .Returns(rasterInfo);
        store.ListRastersAsync(TestLayerId, Arg.Any<CancellationToken>())
            .Returns([rasterInfo]);
        store.QueryRastersAsync(TestLayerId, Arg.Any<RasterSelectionQuery>(), Arg.Any<CancellationToken>())
            .Returns([rasterInfo]);
        store.GetExtentAsync(TestLayerId, 100, Arg.Any<CancellationToken>())
            .Returns(extent);
        store.ExportImageAsync(TestLayerId, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                ContentType = "image/png",
                Width = 64,
                Height = 64,
                Srid = 4326,
                Extent = extent,
                BandCount = 1,
                PixelType = "8BUI",
            });
        store.GetStatisticsAsync(Arg.Is(TestLayerId), Arg.Is<long>(100), Arg.Any<int[]?>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new RasterStatistics
                {
                    Band = 1,
                    MinValue = 0,
                    MaxValue = 255,
                    MeanValue = 128,
                    StandardDeviation = 45,
                    ValidPixelCount = 65_536,
                    NoDataPixelCount = 0,
                },
            ]);
        store.GetHistogramsAsync(Arg.Is(TestLayerId), Arg.Is<long>(100), Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new RasterHistogram
                {
                    Band = 1,
                    BinCount = 4,
                    Min = 0,
                    Max = 255,
                    Counts = [10, 20, 30, 40],
                },
            ]);

        return store;
    }

    private sealed class ImageServerClient(HttpClient httpClient, int layerId)
    {
        public async Task<ImageServiceInfo> GetServiceInfoAsync()
        {
            using var response = await httpClient.GetAsync($"/rest/services/{layerId}/ImageServer?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = await ReadJsonAsync(response);
            var root = doc.RootElement;
            return new ImageServiceInfo(
                root.GetProperty("currentVersion").GetDouble(),
                root.GetProperty("bandCount").GetInt32(),
                root.GetProperty("pixelType").GetString()!,
                root.GetProperty("capabilities").GetString()!);
        }

        public async Task<int> QueryCatalogCountAsync()
        {
            using var response = await httpClient.GetAsync($"/rest/services/{layerId}/ImageServer/query?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = await ReadJsonAsync(response);
            return doc.RootElement.GetProperty("features").GetArrayLength();
        }

        public async Task<int> GetLegendClassCountAsync()
        {
            using var response = await httpClient.GetAsync($"/rest/services/{layerId}/ImageServer/legend?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = await ReadJsonAsync(response);
            return doc.RootElement.GetProperty("layers")[0].GetProperty("legend").GetArrayLength();
        }

        public async Task<ImageBytes> ExportInlinePngAsync()
        {
            using var response = await httpClient.GetAsync(
                $"/rest/services/{layerId}/ImageServer/exportImage?bbox=-180,-90,180,90&size=64,64&format=png&f=image");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return new ImageBytes(
                response.Content.Headers.ContentType?.MediaType ?? string.Empty,
                await response.Content.ReadAsByteArrayAsync());
        }

        public Task<EsriError> GetInvalidFormatErrorAsync()
            => ReadEsriErrorAsync($"/rest/services/{layerId}/ImageServer?f=xml");

        public Task<EsriError> ExportInvalidFormatErrorAsync()
            => ReadEsriErrorAsync($"/rest/services/{layerId}/ImageServer/exportImage?f=xml&bbox=-180,-90,180,90");

        private async Task<EsriError> ReadEsriErrorAsync(string path)
        {
            using var response = await httpClient.GetAsync(path);
            using var doc = await ReadJsonAsync(response);
            var error = doc.RootElement.GetProperty("error");
            return new EsriError(
                response.StatusCode,
                error.GetProperty("code").GetInt32(),
                error.GetProperty("message").GetString()!,
                error.GetProperty("details").EnumerateArray().Select(detail => detail.GetString()!).ToArray());
        }

        private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private sealed record ImageServiceInfo(double CurrentVersion, int BandCount, string PixelType, string Capabilities);

    private sealed record ImageBytes(string ContentType, byte[] Data);

    private sealed record EsriError(HttpStatusCode StatusCode, int Code, string Message, string[] Details);
}
