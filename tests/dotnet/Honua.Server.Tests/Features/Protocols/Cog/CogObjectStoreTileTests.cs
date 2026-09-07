// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Cog;

/// <summary>
/// Serving imagery read from a registered cloud-hosted COG is a GA promise for 2026.1, and
/// until now no test crossed the storage-provider boundary: every COG test substituted
/// <see cref="Honua.Core.Features.Infrastructure.Abstractions.ICloudRangeReader"/>, so
/// authentication, range semantics, partial reads and provider failures were all unexercised.
/// <para>
/// These tests put the real COG bytes in a real object store (LocalStack S3 via
/// <see cref="EmulatorFixture"/>), let the server construct its own production
/// <c>AwsS3RangeReader</c> from <c>FileStorage:AwsS3</c> configuration, and route that
/// reader's traffic through a recording reverse proxy so the HTTP conversation itself can be
/// asserted: every object read is a <c>Range</c> request answered <c>206 Partial Content</c>
/// with a <c>Content-Range</c>, and the bytes actually transferred are a small fraction of the
/// object. The served tile's pixels are compared against GDAL's own decode of the same file
/// (<c>deflate_pred1_uint8.bin</c>, the fixture behind
/// <c>TileDecompressorFixtureTests</c>), not against a snapshot of our own output.
/// </para>
/// </summary>
[Collection("Emulators")]
[Protocol(TestProtocols.Cog, TestProtocols.ImageServer)]
public sealed class CogObjectStoreTileTests : IAsyncLifetime
{
    private const string Bucket = "cog-serving-proof";
    private const string ServiceName = "cog-proof-imagery";
    private const int PublicationLayerId = 1;
    private const int TileSize = 128;

    // 1 MiB of trailing filler appended after the COG's last byte. TIFF readers reach their
    // data through absolute IFD offsets, so the padding changes nothing about how the file
    // decodes - it only makes "the reader did not download the whole object" a claim with a
    // margin no rounding can explain away.
    private const int PaddingBytes = 1024 * 1024;

    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "CogFixtures");

    private RecordingRangeProxy _proxy = null!;
    private AmazonS3Client _uploadClient = null!;
    private byte[] _cog = null!;
    private byte[] _expectedTilePixels = null!;
    private string _upstreamServiceUrl = null!;

    public async Task InitializeAsync()
    {
        _cog = await File.ReadAllBytesAsync(Path.Combine(FixtureDirectory, "deflate_pred1_uint8.tif"));
        _expectedTilePixels = await File.ReadAllBytesAsync(Path.Combine(FixtureDirectory, "deflate_pred1_uint8.bin"));

        _upstreamServiceUrl = Environment.GetEnvironmentVariable("HONUA_TEST_S3_SERVICE_URL")
            ?? throw new InvalidOperationException(
                "The Emulators collection fixture must publish HONUA_TEST_S3_SERVICE_URL before these tests run.");

        _uploadClient = CreateS3Client(_upstreamServiceUrl, "test", "test");
        await EnsureBucketAsync();

        await PutObjectAsync("proofs/plain.tif", _cog);
        await PutObjectAsync("proofs/padded.tif", [.. _cog, .. new byte[PaddingBytes]]);
        await PutObjectAsync("proofs/truncated.tif", _cog.AsSpan(0, 2048).ToArray());

        _proxy = RecordingRangeProxy.Start(new Uri(_upstreamServiceUrl));
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        _uploadClient.Dispose();
    }

    [IntegrationTest]
    [Operation(Operations.CogAdmin, Operations.GetTile)]
    [Endpoint("POST /api/v1/admin/cloud-rasters")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task ImageServerTile_FromObjectStoreCog_MatchesGdalPixelsAndReadsOnlyRanges()
    {
        await using var host = await StartHostAsync(_proxy.BaseUrl, "test", "test");
        await RegisterCloudRasterAsync(host.Fixture, "proofs/padded.tif");

        _proxy.Reset();
        using var tile = await host.Fixture.Client.GetAsync(
            $"/rest/services/{ServiceName}/ImageServer/tile/8/0/0?format=png");

        var tileBytes = await tile.Content.ReadAsByteArrayAsync();
        tile.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(tileBytes));
        tile.Content.Headers.ContentType!.MediaType.Should().Be("image/png");

        // Pixel-for-pixel against GDAL's own decode of this exact file.
        using var decoded = SKBitmap.Decode(tileBytes);
        decoded.Should().NotBeNull();
        decoded.Width.Should().Be(TileSize);
        decoded.Height.Should().Be(TileSize);
        for (var row = 0; row < TileSize; row++)
        {
            for (var col = 0; col < TileSize; col++)
            {
                var value = _expectedTilePixels[(row * TileSize) + col];
                decoded.GetPixel(col, row).Should().Be(new SKColor(value, value, value, 255));
            }
        }

        // The object was read over HTTP range requests, not downloaded.
        var reads = _proxy.Exchanges
            .Where(exchange => exchange.Method == "GET" && exchange.Path.Contains("padded.tif", StringComparison.Ordinal))
            .ToArray();
        reads.Should().NotBeEmpty("the tile must have been served from the object store");
        reads.Should().OnlyContain(exchange => exchange.RequestRange != null,
            "every read on this path must be a byte-range request");
        reads.Should().OnlyContain(exchange => exchange.StatusCode == 206,
            "S3 answers a satisfiable Range request with 206 Partial Content");
        reads.Should().OnlyContain(exchange => exchange.ContentRangeLength != null,
            "a 206 must identify the served range");

        var objectSize = _cog.Length + PaddingBytes;
        var transferred = reads.Sum(exchange => exchange.ResponseBytes);
        transferred.Should().BeLessThan(objectSize / 8,
            "a range read must not pull the whole {0}-byte object; it transferred {1} bytes",
            objectSize, transferred);

        // Every Content-Range agrees with the object's real size and with the body length, so
        // "206" is not just a status the emulator stamps on a full-object body.
        foreach (var exchange in reads)
        {
            exchange.ContentRangeLength.Should().Be(objectSize);
            exchange.ResponseBytes.Should().Be(exchange.ContentRangeTo!.Value - exchange.ContentRangeFrom!.Value + 1);
            exchange.ContentRangeTo!.Value.Should().BeLessThan(objectSize);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task ImageServerTile_WhenObjectIsMissing_FailsBoundedWithNoPartialOutput()
    {
        await using var host = await StartHostAsync(_proxy.BaseUrl, "test", "test");
        await RegisterCloudRasterAsync(host.Fixture, "proofs/never-uploaded.tif");

        using var tile = await host.Fixture.Client.GetAsync(
            $"/rest/services/{ServiceName}/ImageServer/tile/8/0/0?format=png");

        await AssertBoundedFailureAsync(tile);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task ImageServerTile_WhenObjectIsTruncated_FailsBoundedWithNoPartialOutput()
    {
        // The first 2 KiB of a valid COG: the header parses, the tile offsets it advertises
        // point past the end of the object. A reader that streamed what it had would emit a
        // partial image here.
        await using var host = await StartHostAsync(_proxy.BaseUrl, "test", "test");
        await RegisterCloudRasterAsync(host.Fixture, "proofs/truncated.tif");

        using var tile = await host.Fixture.Client.GetAsync(
            $"/rest/services/{ServiceName}/ImageServer/tile/8/0/0?format=png");

        await AssertBoundedFailureAsync(tile);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task ImageServerTile_WithWrongCredentials_FailsBoundedWithNoPartialOutput()
    {
        // The object exists and the request is well formed; only the credentials are wrong.
        // LocalStack rejects the read, and the failure must not leak the credential or the
        // provider's own error text into the response.
        await using var host = await StartHostAsync(_proxy.BaseUrl, "wrong-access-key", "wrong-secret-key");
        await RegisterCloudRasterAsync(host.Fixture, "proofs/plain.tif");

        using var tile = await host.Fixture.Client.GetAsync(
            $"/rest/services/{ServiceName}/ImageServer/tile/8/0/0?format=png");

        var body = await AssertBoundedFailureAsync(tile);
        body.Should().NotContain("wrong-secret-key");
        body.Should().NotContain("SignatureDoesNotMatch");
    }

    private static async Task<string> AssertBoundedFailureAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var body = System.Text.Encoding.UTF8.GetString(bytes);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK, body);
        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(400, body);

        // No partial output: not an image, and not a truncated PNG either.
        (response.Content.Headers.ContentType?.MediaType ?? string.Empty)
            .Should().NotStartWith("image/", body);
        bytes.Take(4).Should().NotEqual([(byte)0x89, (byte)0x50, (byte)0x4E, (byte)0x47]);

        // Bounded: an error document, not a stream of provider internals.
        bytes.Length.Should().BeLessThan(8 * 1024, body);
        return body;
    }

    private async Task<HostScope> StartHostAsync(string serviceUrl, string accessKey, string secretKey)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("cog-proof-resource", "cog-proof-imagery", MetadataV2ResourceType.RasterDataset)
            .AddStorageBinding("cog-proof-binding", "cog-proof-resource", "rasters:900", storageLayerId: 900)
            .AddService("cog-proof-service", ServiceName, protocols: [ServiceProtocols.ImageServer])
            .AddPublication("cog-proof-publication", "cog-proof-service", "cog-proof-resource",
                layerIndex: PublicationLayerId, storageBindingId: "cog-proof-binding",
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();

        // The database raster store holds nothing, so a served tile can only have come from
        // the object store.
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.QueryRastersAsync(default, default!, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());

        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:AwsS3:BucketName"] = Bucket,
                    ["FileStorage:AwsS3:Region"] = "us-east-1",
                    ["FileStorage:AwsS3:ServiceUrl"] = serviceUrl,
                    ["FileStorage:AwsS3:ForcePathStyle"] = "true",
                    ["FileStorage:AwsS3:AccessKeyId"] = accessKey,
                    ["FileStorage:AwsS3:SecretAccessKey"] = secretKey,
                })))
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton<IMetadataV2GraphProvider>(graph);
                services.AddSingleton<IMetadataV2GraphStore>(graph);
                services.AddSingleton(rasterStore);
            });

        await fixture.InitializeAsync();
        return new HostScope(fixture);
    }

    private static async Task RegisterCloudRasterAsync(WebAppFixture fixture, string objectKey)
    {
        using var registration = await fixture.Client.PostAsJsonAsync("/api/v1/admin/cloud-rasters", new
        {
            layerId = PublicationLayerId,
            name = "cog-proof",
            provider = "AwsS3",
            bucket = Bucket,
            objectKey,
        });

        registration.StatusCode.Should().Be(
            HttpStatusCode.Created,
            await registration.Content.ReadAsStringAsync());
    }


    private static AmazonS3Client CreateS3Client(string serviceUrl, string accessKey, string secretKey)
        => new(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                ServiceURL = serviceUrl,
                ForcePathStyle = true,
            });

    private async Task EnsureBucketAsync()
    {
        try
        {
            await _uploadClient.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        }
        catch (AmazonS3Exception ex) when (
            ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Another test in the shared Emulators collection created it first.
        }
    }

    private async Task PutObjectAsync(string key, byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        await _uploadClient.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = stream,
            AutoCloseStream = false,
        });
    }

    private sealed class HostScope(WebAppFixture fixture) : IAsyncDisposable
    {
        public WebAppFixture Fixture { get; } = fixture;

        public async ValueTask DisposeAsync() => await Fixture.DisposeAsync();
    }
}
