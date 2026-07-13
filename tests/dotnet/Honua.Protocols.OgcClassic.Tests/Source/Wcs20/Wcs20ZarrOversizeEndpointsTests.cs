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
/// Covers the WCS 2.0 GetCoverage Zarr slice oversize guard (#2796). The coverage's
/// native grid exceeds the per-axis pixel limit and the request supplies no scaling
/// operator, so the guard is reached on the base-grid path and must report a truthful
/// locator (<c>COVERAGEID</c>, not a <c>SCALESIZE</c> the request never carried).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wcs201)]
public sealed class Wcs20ZarrOversizeEndpointsTests : IAsyncLifetime
{
    private const long RasterId = 903;
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private WebAppFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        const string root = "stores/wcs-oversize";
        var rangeReader = new OversizeFixtureRangeReader(BuildVerticalStore(root, levels: 4, rows: 4, columns: 4));
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(rangeReader, "bucket", root);
        var registration = new ZarrRegistration
        {
            Id = 2796,
            LayerId = WebAppFixture.TestLayerId,
            Name = "wcs-oversize",
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            RootPath = root,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var zarrStore = Substitute.For<IZarrStore>();
        zarrStore.ListByLayerAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns([registration]);

        ConfigureRasterStore();
        _fixture = new WebAppFixture()
            .ReplaceService(_rasterStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IZarrStore>();
                services.RemoveAll<IZarrRasterSliceReader>();
                services.AddSingleton<IZarrStore>(zarrStore);
                // The oversize guard fires before any slice read; this reader must never
                // be invoked, so fail loudly if the guard ever regresses.
                services.AddSingleton<IZarrRasterSliceReader>(new UnreachableSliceReader());
            });
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_OversizeNativeGridWithoutScaling_ReturnsInvalidParameterValueWithCoverageIdLocator()
    {
        // No scaling operator: the base grid (8192 px wide) itself exceeds the limit.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS" +
            "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0" +
            "&FORMAT=image/png&SUBSET=elevation(333.3333)");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"COVERAGEID\"");
    }

    private void ConfigureRasterStore()
    {
        var raster = new RasterInfo
        {
            Id = RasterId,
            LayerId = WebAppFixture.TestLayerId,
            Name = "wcs-oversize-raster",
            Width = 8192,
            Height = 1,
            BandCount = 1,
            Srid = 4326,
            PixelType = "32BF",
            Extent = new RasterExtent
            {
                XMin = -180,
                YMin = -90,
                XMax = 180,
                YMax = 90,
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
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [root + "/.zgroup"] = Encoding.UTF8.GetBytes("{\"zarr_format\":2}"),
            [root + "/.zattrs"] = Encoding.UTF8.GetBytes(
                "{\"variables\":[\"temperature\"],\"primary_variable\":\"temperature\"," +
                "\"crs_wkid\":4326,\"extent\":[-180,-90,180,90]," +
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
        objects[root + "/temperature/0.0.0"] = values;
        return objects;
    }

    private sealed class OversizeFixtureRangeReader(Dictionary<string, byte[]> objects) : ICloudRangeReader
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

    private sealed class UnreachableSliceReader : IZarrRasterSliceReader
    {
        public Task<ZarrRasterSliceReadResult> ReadAsync(
            int layerId,
            ZarrRasterSliceReadRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "The oversize guard must reject the request before any Zarr slice read.");
    }
}
