// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// Covers the WCS 2.0 GetCoverage Zarr slice spatial-trim semantics (#2796): an
/// over-extent trim clamps to the intersection with the coverage extent instead of
/// 404ing, matching the plain IRasterStore path. Uses a sub-global coverage extent so
/// the over-extent trim stays within valid geographic coordinates.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wcs201)]
public sealed class Wcs20ZarrTrimEndpointsTests : IAsyncLifetime
{
    private const long RasterId = 902;
    private const double ExtentMinX = 0;
    private const double ExtentMinY = 0;
    private const double ExtentMaxX = 10;
    private const double ExtentMaxY = 10;
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private WebAppFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        const string root = "stores/wcs-trim";
        var rangeReader = new TrimFixtureRangeReader(BuildVerticalStore(root, levels: 4, rows: 4, columns: 4));
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(rangeReader, "bucket", root);
        var registration = new ZarrRegistration
        {
            Id = 2796,
            LayerId = WebAppFixture.TestLayerId,
            Name = "wcs-trim",
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            RootPath = root,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var zarrStore = Substitute.For<IZarrStore>();
        zarrStore.ListByLayerAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns([registration]);
        var sliceReader = new ZarrRasterSliceReader(zarrStore, new ZarrSubsetReader(), [rangeReader]);

        ConfigureRasterStore();
        _fixture = new WebAppFixture()
            .ReplaceService(_rasterStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IZarrStore>();
                services.RemoveAll<IZarrRasterSliceReader>();
                services.AddSingleton<IZarrStore>(zarrStore);
                services.AddSingleton<IZarrRasterSliceReader>(sliceReader);
            });
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_OverExtentZarrTrim_ClampsToCoverageAndReturnsPng()
    {
        // The trim reaches past the coverage extent on every side; previously the reader
        // required full containment and 404ed, now it clamps to the intersection.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS" +
            "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0" +
            "&FORMAT=image/png&SUBSET=Long(-5,15)&SUBSET=Lat(-5,15)" +
            "&SUBSET=elevation(333.3333)&SCALESIZE=x(4),y(3)");

        await AssertPngAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_AdvertisedExtentZarrTrim_RoundTripsToPng()
    {
        // A client echoing the DescribeCoverage-advertised extent must round-trip.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS" +
            "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0" +
            "&FORMAT=image/png" +
            $"&SUBSET=Long({Fmt(ExtentMinX)},{Fmt(ExtentMaxX)})" +
            $"&SUBSET=Lat({Fmt(ExtentMinY)},{Fmt(ExtentMaxY)})" +
            "&SUBSET=elevation(333.3333)&SCALESIZE=x(4),y(3)");

        await AssertPngAsync(response);
    }

    private static string Fmt(double value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static async Task AssertPngAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        bytes.Should().StartWith([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    }

    private void ConfigureRasterStore()
    {
        var raster = new RasterInfo
        {
            Id = RasterId,
            LayerId = WebAppFixture.TestLayerId,
            Name = "wcs-trim-raster",
            Width = 4,
            Height = 4,
            BandCount = 1,
            Srid = 4326,
            PixelType = "32BF",
            Extent = new RasterExtent
            {
                XMin = ExtentMinX,
                YMin = ExtentMinY,
                XMax = ExtentMaxX,
                YMax = ExtentMaxY,
                Srid = 4326,
            },
            AcquisitionDate = DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            CreatedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        };
        _rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(raster);
        _rasterStore.GetPrimaryRasterInfoAsync(Arg.Is<int>(id => id != WebAppFixture.TestLayerId), Arg.Any<CancellationToken>())
            .Returns((RasterInfo?)null);
        _rasterStore.GetExtentAsync(WebAppFixture.TestLayerId, RasterId, Arg.Any<CancellationToken>())
            .Returns(raster.Extent);
    }

    private static Dictionary<string, byte[]> BuildVerticalStore(
        string root,
        int levels,
        int rows,
        int columns)
    {
        var extent = string.Format(
            CultureInfo.InvariantCulture,
            "[{0},{1},{2},{3}]",
            ExtentMinX,
            ExtentMinY,
            ExtentMaxX,
            ExtentMaxY);
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [root + "/.zgroup"] = Encoding.UTF8.GetBytes("{\"zarr_format\":2}"),
            [root + "/.zattrs"] = Encoding.UTF8.GetBytes(
                "{\"variables\":[\"temperature\"],\"primary_variable\":\"temperature\"," +
                "\"crs_wkid\":4326,\"extent\":" + extent + "," +
                "\"x_dimension\":\"x\",\"y_dimension\":\"y\"," +
                "\"axes\":[{\"name\":\"elevation\",\"unit\":\"m\",\"start\":0,\"end\":1000}]}"),
            [root + "/temperature/.zarray"] = Encoding.UTF8.GetBytes(
                "{\"chunks\":[" + levels + "," + rows + "," + columns +
                "],\"compressor\":null,\"dtype\":\"<f4\",\"fill_value\":0,\"filters\":null," +
                "\"order\":\"C\",\"shape\":[" + levels + "," + rows + "," + columns + "],\"zarr_format\":2}"),
            [root + "/temperature/.zattrs"] = Encoding.UTF8.GetBytes(
                "{\"_ARRAY_DIMENSIONS\":[\"elevation\",\"y\",\"x\"]}"),
        };
        var values = new byte[levels * rows * columns * sizeof(float)];
        for (var level = 0; level < levels; level++)
        {
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var offset = ((level * rows + row) * columns + column) * sizeof(float);
                    Buffer.BlockCopy(BitConverter.GetBytes(level * 1000f + row * 10f + column), 0, values, offset, sizeof(float));
                }
            }
        }
        objects[root + "/temperature/0.0.0"] = values;
        return objects;
    }

    private sealed class TrimFixtureRangeReader(Dictionary<string, byte[]> objects) : ICloudRangeReader
    {
        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<byte[]> ReadRangeAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            var data = Get(key);
            var count = Math.Min(length, data.Length - checked((int)offset));
            return Task.FromResult(data.AsSpan(checked((int)offset), count).ToArray());
        }

        public Task<Stream> ReadRangeStreamAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            var data = Get(key);
            // Ownership transfers to the returned Stream's caller, which disposes it after reading.
            return Task.FromResult<Stream>(new MemoryStream(
                data,
                checked((int)offset),
                Math.Min(length, data.Length - checked((int)offset))));
        }

        public Task<long> GetObjectSizeAsync(
            string bucket,
            string key,
            CancellationToken cancellationToken = default)
            => Task.FromResult((long)Get(key).Length);

        private byte[] Get(string key)
            => objects.TryGetValue(key, out var data) ? data : throw new FileNotFoundException(key);
    }
}
