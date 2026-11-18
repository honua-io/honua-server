// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Formats;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Coverages;

// GDAL's OGC API driver opens a coverage only through a GeoTIFF coverage link, sizes it from the
// domainset, and reads 256-pixel blocks as subset=Lon(..),Lat(..)&scaleSize=Lon(w),Lat(h).
[Collection("Database.OgcApiData")]
public sealed class OgcCoveragesZarrGeoTiffTests
{
    private const string Root = "coverage-geotiff";

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiCoverages)]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}")]
    public async Task Collection_ZarrBacked_AdvertisesGeoTiffCoverageDomainSetAndRangeType()
    {
        await using var fixture = await CreateZarrFixtureAsync();
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(CollectionPath);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("links").EnumerateArray().Should().Contain(link =>
            link.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/coverage" &&
            link.GetProperty("type").GetString() == "image/tiff; application=geotiff" &&
            link.GetProperty("href").GetString()!.EndsWith("/coverage?f=geotiff", StringComparison.Ordinal));
        var grid = root.GetProperty("domainset").GetProperty("generalGrid");
        grid.GetProperty("axisLabels").EnumerateArray().Select(label => label.GetString()).Should().Equal("Lon", "Lat");
        var xAxis = grid.GetProperty("axis")[0];
        xAxis.GetProperty("lowerBound").GetDouble().Should().Be(-1);
        xAxis.GetProperty("upperBound").GetDouble().Should().Be(1);
        xAxis.GetProperty("resolution").GetDouble().Should().Be(1);
        var field = root.GetProperty("rangetype").GetProperty("field").EnumerateArray().Should().ContainSingle().Subject;
        field.GetProperty("name").GetString().Should().Be("temperature");
        field.GetProperty("definition").GetString().Should().Be("FLOAT32");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiCoverages)]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_GdalBlockRequest_ReturnsGeoTiffOfTheRequestedSizeAndCells()
    {
        await using var fixture = await CreateZarrFixtureAsync();
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            CollectionPath + "/coverage?subset=Lon(-1:1),Lat(-1:1)&scaleSize=Lon(2),Lat(2)");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("image/tiff;application=geotiff"));

        using var response = await client.SendAsync(request);
        var tiff = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(tiff));
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/tiff");
        response.Content.Headers.GetValues("Content-Bbox").Should().ContainSingle().Which.Should().Be("-1,-1,1,1");
        tiff[..4].Should().Equal(0x49, 0x49, 42, 0);
        ReadFloatPixels(tiff, 4).Should().Equal(0f, 1f, 2f, 3f);

        using var edge = await client.GetAsync(CollectionPath + "/coverage?f=geotiff&subset=Lon(0:1),Lat(-1:0)");
        edge.StatusCode.Should().Be(HttpStatusCode.OK, await edge.Content.ReadAsStringAsync());
        ReadFloatPixels(await edge.Content.ReadAsByteArrayAsync(), 1).Should().Equal(3f);

        using var defaultFormat = await client.GetAsync(CollectionPath + "/coverage");
        defaultFormat.Content.Headers.ContentType!.MediaType.Should().Be("application/prs.coverage+json");
    }

    [IntegrationTheory]
    [InlineData("f=geotiff&subset=Lon(1:0)")]
    [InlineData("f=geotiff&subset=Lon(-1:0),Lon(0:1)")]
    [InlineData("f=geotiff&scaleSize=Lon(0),Lat(2)")]
    [InlineData("f=geotiff&scaleSize=Lon(2)")]
    [InlineData("f=geotiff&scaleSize=Lon(9000),Lat(2)")]
    [InlineData("f=geotiff&bbox=-1,-1,1,1")]
    [InlineData("f=geotiff&subset=nope(0)")]
    [InlineData("f=geotiff&datetime=2026-01-01T00:00:00Z")]
    [InlineData("f=png")]
    [Protocol(TestProtocols.OgcApiCoverages)]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_InvalidGeoTiffRequest_Returns400(string query)
    {
        await using var fixture = await CreateZarrFixtureAsync();
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(CollectionPath + "/coverage?" + query);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    private static string CollectionPath => $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}";

    private static async Task<WebAppFixture> CreateZarrFixtureAsync()
    {
        var reader = new InMemoryZarrRangeReader(ZarrFixtureBuilder.BuildGroupedZlib(
            Root, 2, 2, 2, 2, (row, column) => (row * 2f) + column, 4326, -1, -1, 1, 1));
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", Root);
        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>()).Returns([
            new ZarrRegistration
            {
                Id = 8462, LayerId = WebAppFixture.TestLayerId, Name = Root,
                Provider = CloudStorageProvider.AwsS3, Bucket = "bucket", RootPath = Root,
                Metadata = metadata, CreatedAt = DateTimeOffset.UtcNow
            }]);
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((RasterInfo?)null);

        return new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ReplaceService(rasterStore).ConfigureServices(services =>
        {
            services.RemoveAll<IZarrStore>();
            services.AddSingleton(store);
            services.RemoveAll<ICloudRangeReader>();
            services.AddSingleton<ICloudRangeReader>(reader);
        });
    }

    private static float[] ReadFloatPixels(byte[] tiff, int count)
    {
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd));
        var stripOffset = Enumerable.Range(0, entries)
            .Select(index => tiff.AsSpan(ifd + 2 + (index * 12), 12).ToArray())
            .Where(entry => BinaryPrimitives.ReadUInt16LittleEndian(entry) == 273)
            .Select(entry => (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(8)))
            .Single();
        return Enumerable.Range(0, count)
            .Select(index => BinaryPrimitives.ReadSingleLittleEndian(tiff.AsSpan(stripOffset + (index * 4))))
            .ToArray();
    }
}
