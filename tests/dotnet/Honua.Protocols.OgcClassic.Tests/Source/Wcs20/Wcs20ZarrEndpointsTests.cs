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

[Collection("Database")]
[Protocol(TestProtocols.Wcs201)]
public sealed class Wcs20ZarrEndpointsTests : IAsyncLifetime
{
    private const long RasterId = 901;
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private WebAppFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        const string root = "stores/wcs-vertical";
        var rangeReader = new WcsFixtureRangeReader(BuildVerticalStore(root, levels: 4, rows: 4, columns: 4));
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(rangeReader, "bucket", root);
        var registration = new ZarrRegistration
        {
            Id = 2696,
            LayerId = WebAppFixture.TestLayerId,
            Name = "wcs-vertical",
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            RootPath = root,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var zarrStore = Substitute.For<IZarrStore>();
        zarrStore.ListByLayerAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns([registration]);
        var sliceReader = new UnavailableSelectionReader(
            new ZarrRasterSliceReader(zarrStore, new ZarrSubsetReader(), [rangeReader]));

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
    public async Task Wcs_GetCoverage_NumericImageServerRoute_ReturnsRegisteredZarrSlicePng()
    {
        var response = await _fixture.Client.GetAsync(BuildRequest(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS"));

        await AssertPngAsync(response);
        await _rasterStore.DidNotReceiveWithAnyArgs()
            .ExportImageAsync(default, default, default, default);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs_GetCoverage_ServiceRoute_ReturnsRegisteredZarrSlicePng()
    {
        var response = await _fixture.Client.GetAsync(BuildRequest(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs"));

        await AssertPngAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_OutOfRangeZarrCoordinate_ReturnsInvalidSubsetting()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS" +
            "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0" +
            "&FORMAT=image/png&SUBSET=elevation(2000)");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, content);
        content.Should().Contain("exceptionCode=\"InvalidSubsetting\"");
        content.Should().Contain("locator=\"SUBSET\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_ZarrSliceWithTiff_ReturnsOperationNotSupported()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS" +
            "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0" +
            "&FORMAT=image/tiff&SUBSET=elevation(333.3333)");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, content);
        content.Should().Contain("exceptionCode=\"OperationNotSupported\"");
        content.Should().Contain("locator=\"FORMAT\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_UnavailableZarrReader_ReturnsOperationNotSupported()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS" +
            "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0" +
            "&FORMAT=image/png&SUBSET=elevation(666)");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, content);
        content.Should().Contain("exceptionCode=\"OperationNotSupported\"");
        content.Should().Contain("locator=\"SUBSET\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_OversizeZarrSlice_ReturnsInvalidParameterValue()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS" +
            "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0" +
            "&FORMAT=image/png&SUBSET=elevation(333.3333)&SCALESIZE=x(4097),y(1)");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"SCALESIZE\"");
    }

    private static string BuildRequest(string path)
        => path + "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0" +
            "&FORMAT=image/png&SUBSET=Long(-180,180)&SUBSET=Lat(-90,90)" +
            "&SUBSET=elevation(333.3333)&SCALESIZE=x(4),y(3)";

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
            Name = "wcs-zarr-raster",
            Width = 4,
            Height = 4,
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
        for (var level = 0; level < levels; level++)
        {
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var offset = ((level * rows + row) * columns + column) * sizeof(float);
                    // Widen to long before the narrowing multiplications so the analyzer (and any future
                    // caller with larger levels/rows) can't see an int overflow before the cast to float.
                    var value = (float)((long)level * 1000 + (long)row * 10 + column);
                    Buffer.BlockCopy(BitConverter.GetBytes(value), 0, values, offset, sizeof(float));
                }
            }
        }
        objects[root + "/temperature/0.0.0"] = values;
        return objects;
    }

    private sealed class WcsFixtureRangeReader(Dictionary<string, byte[]> objects) : ICloudRangeReader
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

    private sealed class UnavailableSelectionReader(IZarrRasterSliceReader inner) : IZarrRasterSliceReader
    {
        public Task<ZarrRasterSliceReadResult> ReadAsync(
            int layerId,
            ZarrRasterSliceReadRequest request,
            CancellationToken cancellationToken = default)
            // Exact equality is intentional: both sides are integer-valued doubles (the request
            // literal "666" is parsed straight to 666.0 with no arithmetic in between), so this is
            // a deterministic sentinel match, not a genuine floating-point precision comparison.
            => request.Selections.Any(static selection => selection.Coordinate == 666)
                ? Task.FromResult(new ZarrRasterSliceReadResult(
                    ZarrRasterSliceReadStatus.ReaderUnavailable,
                    null,
                    "temperature",
                    request.Selections.Count,
                    "No configured range reader is available for the registered multidimensional coverage."))
                : inner.ReadAsync(layerId, request, cancellationToken);
    }
}
