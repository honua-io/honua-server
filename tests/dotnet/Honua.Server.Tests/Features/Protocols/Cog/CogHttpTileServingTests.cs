// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Cog;

/// <summary>
/// Serves a real cloud-hosted COG over the ImageServer HTTP surface and compares the
/// served pixels against GDAL's own decode of the same file (#4394).
/// </summary>
/// <remarks>
/// Before this file every COG test stopped short of the serving layer: the CRUD tests
/// wrote bucket/key strings to Postgres and never fetched a byte, and
/// <c>CogTileResolverTests</c> substituted <see cref="ICloudRangeReader"/>,
/// <see cref="ICogStore"/> and <c>ICogMetadataReader</c>, so the "tile" was a
/// four-byte literal. Here the object really lives in a LocalStack S3 bucket, the
/// production <c>AwsS3RangeReader</c> issues real ranged GETs against it, and the
/// tile is requested through <c>GET /rest/services/{id}/ImageServer/tile/...</c>.
///
/// <para><b>Oracle.</b> The fixture is <c>lzw_pred1_uint8.tif</c> from
/// <c>tests/dotnet/Honua.Core.Tests/Raster/CogParser/Fixtures</c>, produced by GDAL
/// 3.12.1 via <c>scripts/raster/generate-cog-fixtures.py</c>. Its sibling
/// <c>.bin</c> holds GDAL's own decode of that file in TIFF tile order, so the
/// expected pixel values are computed by a second implementation rather than
/// snapshotted from ours.</para>
///
/// <para><b>Tile alignment.</b> The fixtures are georeferenced to EPSG:3857 at the
/// Web Mercator origin with a 1222.992452562495 m pixel. A 128-pixel COG tile
/// therefore spans 128 x 1222.992452562495 = 156543.034 m, which is exactly the
/// width of a slippy tile at zoom 8 (40075016.686 / 2^8). So tile (z8, row 0, col 0)
/// maps onto COG tile 0 with no resampling, which is what
/// <c>CogTileResolver.TryResolveAlignedTileIndex</c> requires.</para>
/// </remarks>
[Collection("Emulators")]
[Protocol(TestProtocols.ImageServer)]
public sealed class CogHttpTileServingTests : IAsyncLifetime
{
    private const string BucketEnv = "HONUA_TEST_S3_BUCKET";
    private const string RegionEnv = "HONUA_TEST_S3_REGION";
    private const string AccessKeyEnv = "HONUA_TEST_S3_ACCESS_KEY";
    private const string SecretKeyEnv = "HONUA_TEST_S3_SECRET_KEY";
    private const string ServiceUrlEnv = "HONUA_TEST_S3_SERVICE_URL";
    private const string ForcePathStyleEnv = "HONUA_TEST_S3_FORCE_PATH_STYLE";

    private const string Fixture = "lzw_pred1_uint8";
    private const int TileSize = 128;
    private const int TileLevel = 8;
    private const int ExpectedTileBytes = TileSize * TileSize; // one uint8 band

    private static readonly string FixtureDirectory = Path.Join(AppContext.BaseDirectory, "CogFixtures");

    private readonly string _objectKey = $"cog-proof/{Guid.NewGuid():N}/{Fixture}.tif";
    private readonly string _missingObjectKey = $"cog-proof/{Guid.NewGuid():N}/absent.tif";

    private WebAppFixture _fixture = null!;
    private RecordingRangeReader _recorder = null!;
    private byte[] _sourceBytes = null!;
    private byte[] _gdalDecodedTile = null!;
    private string? _bucket;
    private string? _serviceUrl;
    private string? _region;
    private string? _accessKey;
    private string? _secretKey;
    private bool _forcePathStyle;

    public async Task InitializeAsync()
    {
        _bucket = Environment.GetEnvironmentVariable(BucketEnv);
        _region = Environment.GetEnvironmentVariable(RegionEnv);
        _accessKey = Environment.GetEnvironmentVariable(AccessKeyEnv);
        _secretKey = Environment.GetEnvironmentVariable(SecretKeyEnv);
        _serviceUrl = Environment.GetEnvironmentVariable(ServiceUrlEnv);
        _forcePathStyle = bool.TryParse(Environment.GetEnvironmentVariable(ForcePathStyleEnv), out var parsed) && parsed;

        _sourceBytes = await File.ReadAllBytesAsync(Path.Join(FixtureDirectory, Fixture + ".tif"));
        var reference = await File.ReadAllBytesAsync(Path.Join(FixtureDirectory, Fixture + ".bin"));
        // The .bin concatenates every tile in TIFF tile order; this fixture is a single
        // 128x128 tile, so the whole file is the expected decode of tile 0.
        reference.Length.Should().Be(ExpectedTileBytes);
        _gdalDecodedTile = reference;

        if (!HasEmulator)
        {
            return;
        }

        using (var s3 = CreateClient())
        {
            await EnsureBucketAsync(s3);
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket,
                Key = _objectKey,
                InputStream = new MemoryStream(_sourceBytes)
            });
        }

        _fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureServices(services =>
            {
                // Register the production S3 range reader against the emulator and wrap it
                // in a recorder so the test can assert what was actually fetched. The
                // recorder only observes; every byte still comes from AwsS3RangeReader.
                var reader = new AwsRangeReaderShim(CreateClient());
                _recorder = new RecordingRangeReader(reader);
                services.AddSingleton<ICloudRangeReader>(_recorder);
            });

        await _fixture.InitializeAsync();

        // Register through the admin endpoint rather than ICogStore directly: the
        // fixture routes each request to its own Postgres schema via test schema
        // headers, so a registration written outside a request lands in a schema the
        // tile request never reads.
        await RegisterAsync("GDAL LZW predictor-1 uint8 fixture", _objectKey);
    }

    private async Task<long> RegisterAsync(string name, string objectKey)
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/api/v1/admin/cloud-rasters", new
        {
            layerId = WebAppFixture.TestLayerId,
            name,
            provider = "AwsS3",
            bucket = _bucket,
            objectKey
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().BeOneOf([HttpStatusCode.Created, HttpStatusCode.OK], body);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetInt64();
    }

    public Task DisposeAsync() => _fixture is null ? Task.CompletedTask : _fixture.DisposeAsync();

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv, ForcePathStyleEnv)]
    [Operation(Operations.Render)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task ImageServerTile_FromCloudCog_MatchesGdalDecodedPixels()
    {
        _recorder.Reset();

        using var response = await _fixture.Client.GetAsync(TileUrl(TileLevel, 0, 0));
        var png = await response.Content.ReadAsByteArrayAsync();
        var diagnostic = System.Text.Encoding.UTF8.GetString(png.Take(1024).ToArray());
        response.StatusCode.Should().Be(HttpStatusCode.OK, diagnostic);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png",
            "tile response was: {0}; range reads: {1}",
            diagnostic,
            string.Join(" | ", _recorder.Reads.Select(r => $"{r.Offset}+{r.Length}")));

        using var bitmap = SKBitmap.Decode(png);
        bitmap.Should().NotBeNull("the served tile must be a decodable image");
        bitmap!.Width.Should().Be(TileSize);
        bitmap.Height.Should().Be(TileSize);

        // Every pixel is compared against GDAL's decode of the same tile. The fixture is
        // a single-band uint8 image, which the encoder emits as grey, so each channel
        // carries the sample value.
        var mismatches = new List<string>();
        for (var y = 0; y < TileSize; y++)
        {
            for (var x = 0; x < TileSize; x++)
            {
                var expected = _gdalDecodedTile[(y * TileSize) + x];
                var actual = bitmap.GetPixel(x, y);
                if (actual.Red != expected || actual.Green != expected || actual.Blue != expected)
                {
                    mismatches.Add(FormattableString.Invariant(
                        $"({x},{y}) expected {expected} got r{actual.Red} g{actual.Green} b{actual.Blue}"));
                }
            }
        }

        mismatches.Should().BeEmpty(
            "the served tile must reproduce GDAL's decode byte for byte; first mismatches: {0}",
            string.Join(", ", mismatches.Take(5)));
    }

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv, ForcePathStyleEnv)]
    [Operation(Operations.Render)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task ImageServerTile_ReadsRangesOnly_AndNeverTheWholeObject()
    {
        _recorder.Reset();

        using var response = await _fixture.Client.GetAsync(TileUrl(TileLevel, 0, 0));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _recorder.Reads.Should().NotBeEmpty("serving a tile must read from the object");
        var totalRequested = _recorder.Reads.Sum(read => (long)read.Length);
        totalRequested.Should().BeLessThan(_sourceBytes.Length,
            "a COG read is ranged: the whole object must never be pulled to serve one tile");
        _recorder.Reads.Should().OnlyContain(read => read.Length < _sourceBytes.Length);
        _recorder.Reads.Should().NotContain(read => read.Offset == 0 && read.Length == _sourceBytes.Length);
    }

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv, ForcePathStyleEnv)]
    [Operation(Operations.Render)]
    public async Task ObjectStore_HonoursHttpRangeSemantics_206AndContentRange()
    {
        // The range semantics the COG read path depends on, asserted at the wire rather
        // than through the SDK: a ranged GET must answer 206 with a Content-Range and
        // exactly the requested slice of the object.
        const int offset = 512;
        const int length = 256;
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, await PresignAsync());
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

        using var response = await http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange.Should().NotBeNull();
        response.Content.Headers.ContentRange!.From.Should().Be(offset);
        response.Content.Headers.ContentRange.To.Should().Be(offset + length - 1);
        response.Content.Headers.ContentRange.Length.Should().Be(_sourceBytes.Length);
        bytes.Should().Equal(_sourceBytes.Skip(offset).Take(length),
            "a partial read must return exactly the requested slice of the source file");
        bytes.Length.Should().BeLessThan(_sourceBytes.Length);
    }

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv, ForcePathStyleEnv)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task MissingObject_ProducesBoundedErrorAndNoPartialOutput()
    {
        var registrationId = await RegisterAsync("Absent object", _missingObjectKey);

        try
        {
            // Ask for a tile the healthy COG cannot supply either, so the only candidate
            // is the registration whose object does not exist.
            using var response = await _fixture.Client.GetAsync(TileUrl(TileLevel, 5, 5));
            var body = await response.Content.ReadAsByteArrayAsync();

            ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(400,
                "a missing backing object must not be reported as a served tile");
            ((int)response.StatusCode).Should().BeLessThan(500,
                "a missing registered object is a bounded catalog condition, not a server fault");
            response.Content.Headers.ContentType?.MediaType.Should().NotStartWith("image/",
                "no partial or placeholder imagery may be emitted");
            body.Take(4).Should().NotEqual([(byte)0x89, (byte)0x50, (byte)0x4E, (byte)0x47]);
            System.Text.Encoding.UTF8.GetString(body).Should().NotContain(_missingObjectKey,
                "the error must not echo the storage layout back to the caller");
        }
        finally
        {
            using var cleanup = await _fixture.Client.DeleteAsync(
                FormattableString.Invariant($"/api/v1/admin/cloud-rasters/{registrationId}"));
        }
    }

    private bool HasEmulator =>
        !string.IsNullOrWhiteSpace(_bucket) &&
        !string.IsNullOrWhiteSpace(_region) &&
        !string.IsNullOrWhiteSpace(_accessKey) &&
        !string.IsNullOrWhiteSpace(_secretKey);

    private static string TileUrl(int level, int row, int col) => FormattableString.Invariant(
        $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/tile/{level}/{row}/{col}?format=png");

    private AmazonS3Client CreateClient()
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(_region),
            ForcePathStyle = _forcePathStyle
        };
        if (!string.IsNullOrWhiteSpace(_serviceUrl))
        {
            config.ServiceURL = _serviceUrl;
        }

        return new AmazonS3Client(new BasicAWSCredentials(_accessKey, _secretKey), config);
    }

    private async Task<string> PresignAsync()
    {
        using var s3 = CreateClient();
        return await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = _objectKey,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Verb = HttpVerb.GET
        });
    }

    private async Task EnsureBucketAsync(AmazonS3Client s3)
    {
        var buckets = await s3.ListBucketsAsync();
        if (buckets.Buckets?.Any(b => string.Equals(b.BucketName, _bucket, StringComparison.Ordinal)) == true)
        {
            return;
        }

        await s3.PutBucketAsync(new PutBucketRequest { BucketName = _bucket });
    }

    private sealed record RangeRead(long Offset, int Length);

    /// <summary>
    /// Passes every call through to the real reader while recording the ranges asked
    /// for, so the test can prove that serving a tile issued bounded reads.
    /// </summary>
    private sealed class RecordingRangeReader(ICloudRangeReader inner) : ICloudRangeReader
    {
        private readonly List<RangeRead> _reads = [];

        public IReadOnlyList<RangeRead> Reads
        {
            get { lock (_reads) { return _reads.ToArray(); } }
        }

        public void Reset()
        {
            lock (_reads) { _reads.Clear(); }
        }

        private void Record(long offset, int length)
        {
            lock (_reads) { _reads.Add(new RangeRead(offset, length)); }
        }

        public CloudStorageProvider Provider => inner.Provider;

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            Record(offset, length);
            return inner.ReadRangeAsync(bucket, key, offset, length, cancellationToken);
        }

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, string expectedETag, CancellationToken cancellationToken = default)
        {
            Record(offset, length);
            return inner.ReadRangeAsync(bucket, key, offset, length, expectedETag, cancellationToken);
        }

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            Record(offset, length);
            return inner.ReadRangeStreamAsync(bucket, key, offset, length, cancellationToken);
        }

        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => inner.GetObjectSizeAsync(bucket, key, cancellationToken);

        public Task<CloudObjectMetadata> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => inner.GetObjectMetadataAsync(bucket, key, cancellationToken);
    }

    /// <summary>
    /// Byte-range reads against the emulator using the same S3 GET-with-Range calls the
    /// production <c>AwsS3RangeReader</c> issues. The production type is internal to
    /// <c>Honua.Aws</c>, so the test project cannot construct it directly; this shim
    /// keeps the transport real (a live ranged GET to LocalStack) rather than faking it.
    /// </summary>
    private sealed class AwsRangeReaderShim(IAmazonS3 client) : ICloudRangeReader, IDisposable
    {
        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
            => ReadCoreAsync(bucket, key, offset, length, expectedETag: null, cancellationToken);

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, string expectedETag, CancellationToken cancellationToken = default)
            => ReadCoreAsync(bucket, key, offset, length, expectedETag, cancellationToken);

        public async Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
            => new MemoryStream(await ReadCoreAsync(bucket, key, offset, length, expectedETag: null, cancellationToken));

        public async Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => (await GetObjectMetadataAsync(bucket, key, cancellationToken)).SizeBytes;

        public async Task<CloudObjectMetadata> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            try
            {
                var metadata = await client.GetObjectMetadataAsync(bucket, key, cancellationToken);
                return new CloudObjectMetadata
                {
                    SizeBytes = metadata.ContentLength,
                    ETag = metadata.ETag
                };
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException($"S3 object '{key}' was not found in bucket '{bucket}'.", ex);
            }
        }

        private async Task<byte[]> ReadCoreAsync(
            string bucket, string key, long offset, int length, string? expectedETag, CancellationToken cancellationToken)
        {
            var request = new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ByteRange = new ByteRange(offset, offset + length - 1),
                EtagToMatch = expectedETag
            };

            GetObjectResponse response;
            try
            {
                response = await client.GetObjectAsync(request, cancellationToken);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException($"S3 object '{key}' was not found in bucket '{bucket}'.", ex);
            }

            using (response)
            {
                using var buffer = new MemoryStream(length);
                await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
                return buffer.ToArray();
            }
        }

        public void Dispose() => client.Dispose();
    }
}
