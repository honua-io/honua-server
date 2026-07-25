// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
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

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Coverages;

[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiCoverages)]
public sealed class OgcCoveragesZarrEndpointsTests : IAsyncLifetime
{
    private const string RootPath = "stores/coverage-grid";
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private RecordingZarrRangeReader _rangeReader = null!;
    private WebAppFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _rangeReader = new RecordingZarrRangeReader(BuildStore());
        var metadata = await new ZarrMetadataExtractor()
            .ReadMetadataAsync(_rangeReader, "bucket", RootPath);
        _rangeReader.ClearRequests();

        var registration = new ZarrRegistration
        {
            Id = 2983,
            LayerId = WebAppFixture.TestLayerId,
            Name = "coverage-grid",
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            RootPath = RootPath,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var zarrStore = Substitute.For<IZarrStore>();
        zarrStore.ListByLayerAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(new[] { registration });
        zarrStore.ListByLayerAsync(
                Arg.Is<int>(layerId => layerId != WebAppFixture.TestLayerId),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ZarrRegistration>());

        _rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(null));

        _fixture = new WebAppFixture()
            .ReplaceService(_rasterStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IZarrStore>();
                services.RemoveAll<IZarrSubsetReader>();
                services.RemoveAll<ICloudRangeReader>();
                services.AddSingleton<IZarrStore>(zarrStore);
                services.AddSingleton<IZarrSubsetReader, ZarrSubsetReader>();
                services.AddSingleton<ICloudRangeReader>(_rangeReader);
            });
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverages_GetCoverage_ZarrSubsetReadsFourChunksAndReturnsCoverageJson()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage" +
            "?properties=temperature&subset=y(1:2)&subset=x(1:2)");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/prs.coverage+json");

        using var document = JsonDocument.Parse(content);
        var range = document.RootElement.GetProperty("ranges").GetProperty("temperature");
        range.GetProperty("shape").EnumerateArray().Select(value => value.GetInt32())
            .Should().Equal(2, 2);
        range.GetProperty("values").EnumerateArray().Select(value => value.GetSingle())
            .Should().Equal(101f, 102f, 201f, 202f);

        _rangeReader.Requests.Should().BeEquivalentTo(
            new[]
            {
                new RangeRequest($"{RootPath}/temperature/0.0", 0, 16),
                new RangeRequest($"{RootPath}/temperature/0.1", 0, 16),
                new RangeRequest($"{RootPath}/temperature/1.0", 0, 16),
                new RangeRequest($"{RootPath}/temperature/1.1", 0, 16)
            });
        await _rasterStore.DidNotReceiveWithAnyArgs()
            .ExportImageAsync(default, default, default, default);
    }

    private static Dictionary<string, byte[]> BuildStore()
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{RootPath}/.zgroup"] = Encoding.UTF8.GetBytes("""{"zarr_format":2}"""),
            [$"{RootPath}/.zattrs"] = Encoding.UTF8.GetBytes(
                """{"variables":["temperature"],"primary_variable":"temperature","crs_wkid":4326,"extent":[-180,-90,180,90],"x_dimension":"x","y_dimension":"y"}"""),
            [$"{RootPath}/temperature/.zarray"] = Encoding.UTF8.GetBytes(
                """{"chunks":[2,2],"compressor":null,"dtype":"<f4","fill_value":0,"filters":null,"order":"C","shape":[4,4],"zarr_format":2}"""),
            [$"{RootPath}/temperature/.zattrs"] = Encoding.UTF8.GetBytes(
                """{"_ARRAY_DIMENSIONS":["y","x"]}""")
        };

        for (var chunkRow = 0; chunkRow < 2; chunkRow++)
        {
            for (var chunkColumn = 0; chunkColumn < 2; chunkColumn++)
            {
                var chunk = new byte[2 * 2 * sizeof(float)];
                for (var row = 0; row < 2; row++)
                {
                    for (var column = 0; column < 2; column++)
                    {
                        var globalRow = (chunkRow * 2) + row;
                        var globalColumn = (chunkColumn * 2) + column;
                        var value = (globalRow * 100f) + globalColumn;
                        Buffer.BlockCopy(
                            BitConverter.GetBytes(value),
                            0,
                            chunk,
                            ((row * 2) + column) * sizeof(float),
                            sizeof(float));
                    }
                }

                objects[$"{RootPath}/temperature/{chunkRow}.{chunkColumn}"] = chunk;
            }
        }

        return objects;
    }

    private sealed record RangeRequest(string Key, long Offset, int Length);

    private sealed class RecordingZarrRangeReader(Dictionary<string, byte[]> objects) : ICloudRangeReader
    {
        private readonly ConcurrentQueue<RangeRequest> _requests = new();

        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public RangeRequest[] Requests => _requests.ToArray();

        public Task<byte[]> ReadRangeAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Enqueue(new RangeRequest(key, offset, length));
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
            cancellationToken.ThrowIfCancellationRequested();
            var data = Get(key);
            // Ownership transfers to the caller, which disposes the returned stream.
            // codeql[cs/local-not-disposed]
            return Task.FromResult<Stream>(new MemoryStream(
                data,
                checked((int)offset),
                Math.Min(length, data.Length - checked((int)offset))));
        }

        public Task<long> GetObjectSizeAsync(
            string bucket,
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult((long)Get(key).Length);
        }

        public void ClearRequests() => _requests.Clear();

        private byte[] Get(string key) =>
            objects.TryGetValue(key, out var data)
                ? data
                : throw new FileNotFoundException(key);
    }
}
