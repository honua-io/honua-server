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
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.FileStorage;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// <para><b>Oracle.</b> The fixture is <c>lzw_pred2_uint8_multitile.tif</c> from
/// <c>tests/dotnet/Honua.Core.Tests/Raster/CogParser/Fixtures</c>, produced by GDAL
/// 3.12.1 via <c>scripts/raster/generate-cog-fixtures.py</c>. Its sibling
/// <c>.bin</c> holds GDAL's own decode of that file in TIFF tile order, so the
/// expected pixel values are computed by a second implementation rather than
/// snapshotted from ours.</para>
///
/// <para><b>Tile alignment.</b> The fixtures are georeferenced to EPSG:3857 at the
/// Web Mercator origin with a 1222.992452562495 m pixel. A 128-pixel COG tile
/// therefore spans 128 x 1222.992452562495 = 156543.034 m, which is exactly the
/// width of a slippy tile at zoom 8 (40075016.686 / 2^8). This fixture is 256 x 256
/// pixels in 128-pixel blocks, so slippy tiles (z8, row 0..1, col 0..1) map onto its
/// four COG tiles with no resampling, which is what
/// <c>CogTileResolver.TryResolveAlignedTileIndex</c> requires. The test asks for
/// (row 1, col 1) so that the tile it wants is neither the first tile nor adjacent
/// to the header, and a read of the tile's own byte range is distinguishable from a
/// read of the file's metadata.</para>
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

    // The multi-tile fixture (256x256 image, 128px blocks -> four tiles) is used
    // deliberately. On the single-tile fixture the compressed tile is 17,975 bytes of
    // an 18,362-byte object, so "the tile read is small relative to the object" is not
    // a property that fixture can demonstrate at all. Here one tile is roughly a
    // quarter of the data, so a regression that fetched the whole object to serve one
    // tile really does break the bound asserted below.
    private const string Fixture = "lzw_pred2_uint8_multitile";
    private const int TileSize = 128;
    private const int TileLevel = 8;
    private const int ExpectedTileBytes = TileSize * TileSize; // one uint8 band
    private const int TileCount = 4;

    /// <summary>
    /// The slippy tile requested, and the COG tile it must resolve to. The fixture is
    /// two tiles across, and CogTileResolver indexes tiles row-major, so slippy
    /// (col 1, row 1) is COG tile 3 — the last 16,384 bytes of the reference blob.
    /// </summary>
    private const int RequestedTileCol = 1;
    private const int RequestedTileRow = 1;
    private const int RequestedTileIndex = 3;

    private static readonly string FixtureDirectory = Path.Join(AppContext.BaseDirectory, "CogFixtures");

    /// <summary>
    /// The layer the COG is registered against. It has to satisfy three constraints
    /// at once: positive (the admin endpoint rejects layer 0 with "LayerId must be a
    /// positive integer"), present in <c>honua.layers</c> (the registration row
    /// carries a foreign key, so an invented id fails the insert), and free of
    /// PostGIS raster rows (otherwise <c>ImageServerTileHandler</c> serves from
    /// PostGIS and never reaches the COG fallback). Seeded layer 1 is all three; the
    /// V2 graph below republishes it as an ImageServer raster layer so the tile route
    /// resolves it.
    /// </summary>
    private const int CogLayerId = 1;

    /// <summary>
    /// A second seeded, raster-free layer carrying only the registration whose object
    /// does not exist, so the failure case has no healthy COG to fall back to.
    /// </summary>
    private const int MissingCogLayerId = 2;

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
        // The .bin concatenates every tile in TIFF tile order, so the expected decode of
        // the requested tile is its slice of that blob.
        reference.Length.Should().Be(ExpectedTileBytes * TileCount);
        _gdalDecodedTile = reference
            .Skip(RequestedTileIndex * ExpectedTileBytes)
            .Take(ExpectedTileBytes)
            .ToArray();

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

        var graph = new Honua.TestKit.Infrastructure.TestMetadataV2GraphBuilder()
            .AddResource("cog-http-resource", "cog-http-resource", MetadataV2ResourceType.RasterDataset)
            .AddStorageBinding("cog-http-binding", "cog-http-resource", "rasters", storageLayerId: CogLayerId)
            .AddService("cog-http-service", "cog-http-service", protocols: [ServiceProtocols.ImageServer])
            .AddPublication(
                "cog-http-publication",
                "cog-http-service",
                "cog-http-resource",
                layerIndex: CogLayerId,
                storageBindingId: "cog-http-binding",
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .AddResource("cog-missing-resource", "cog-missing-resource", MetadataV2ResourceType.RasterDataset)
            .AddStorageBinding("cog-missing-binding", "cog-missing-resource", "rasters", storageLayerId: MissingCogLayerId)
            .AddService("cog-missing-service", "cog-missing-service", protocols: [ServiceProtocols.ImageServer])
            .AddPublication(
                "cog-missing-publication",
                "cog-missing-service",
                "cog-missing-resource",
                layerIndex: MissingCogLayerId,
                storageBindingId: "cog-missing-binding",
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();

        _fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton<IMetadataV2GraphProvider>(graph);
                services.AddSingleton<IMetadataV2GraphStore>(graph);

                // The production reader, against the emulator. Honua.Aws grants this
                // assembly internal access, so the type under test is the shipping
                // AwsS3RangeReader rather than a test double: if it stops issuing valid
                // ranged requests or mishandles object metadata, these tests go red.
                // The recorder only observes what it was asked for.
                _recorder = new RecordingRangeReader(new AwsS3RangeReader(CreateClient()));
                services.AddSingleton<ICloudRangeReader>(_recorder);
            });

        await _fixture.InitializeAsync();

        // Register through the admin endpoint rather than ICogStore directly: the
        // fixture routes each request to its own Postgres schema via test schema
        // headers, so a registration written outside a request lands in a schema the
        // tile request never reads.
        await RegisterAsync("GDAL LZW predictor-1 uint8 fixture", _objectKey);
    }

    private async Task<long> RegisterAsync(string name, string objectKey, int layerId = CogLayerId)
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/api/v1/admin/cloud-rasters", new
        {
            layerId,
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

        using var response = await _fixture.Client.GetAsync(TileUrl(TileLevel, RequestedTileRow, RequestedTileCol));
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

        using var response = await _fixture.Client.GetAsync(TileUrl(TileLevel, RequestedTileRow, RequestedTileCol));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _recorder.Reads.Should().NotBeEmpty("serving a tile must read from the object");

        _recorder.Reads.Should().OnlyContain(read => read.Length < _sourceBytes.Length,
            "each read must be a bounded range, not a whole-object GET");
        _recorder.Reads.Should().NotContain(read => read.Offset == 0 && read.Length == _sourceBytes.Length);

        // Name the tile transfer instead of inferring it. Metadata parsing issues its
        // own header and IFD reads, so "the smallest read" or "the largest read" only
        // describes the tile by accident; the tile's offset and compressed length come
        // from the fixture's own TIFF directory (tags 324/325), read here by a local
        // walker rather than by the parser under test.
        var (tileOffset, tileByteCount) = ReadTileExtent(_sourceBytes, RequestedTileIndex);
        var recorded = string.Join(", ", _recorder.Reads.Select(r => $"{r.Offset}+{r.Length}"));
        _recorder.Reads.Should().ContainSingle(
            read => read.Offset == tileOffset && read.Length == tileByteCount,
            "exactly one read must fetch tile {0}'s own byte range {1}+{2}; reads were: {3}",
            RequestedTileIndex, tileOffset, tileByteCount, recorded);

        // The largest single transfer is the compressed tile. Bounding *that* is the
        // property that matters: taking the minimum would instead measure a small
        // header read and would stay green while the tile fetch grew to the whole
        // object.
        var largest = _recorder.Reads.Max(read => read.Length);
        largest.Should().BeLessThan(_sourceBytes.Length / 2,
            "one tile of four must not cost a transfer of half the object");

        // And the whole exchange — metadata plus tile — stays under the object size, so
        // serving a tile is genuinely cheaper than downloading the file.
        _recorder.Reads.Sum(read => (long)read.Length).Should().BeLessThan(_sourceBytes.Length,
            "the total bytes fetched to serve one tile must be less than the object itself");
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
        // The emulator presigns an HTTPS URL backed by a self-signed certificate.
        // Certificate validation is bypassed for this one request because the subject
        // under test is HTTP range semantics, not TLS, and the endpoint is a local
        // container. Nothing in the product is affected.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler);
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
        var registrationId = await RegisterAsync("Absent object", _missingObjectKey, MissingCogLayerId);

        try
        {
            // This layer's only candidate is the registration whose object is absent.
            using var response = await _fixture.Client.GetAsync(
                TileUrl(TileLevel, 0, 0, MissingCogLayerId));
            var body = await response.Content.ReadAsByteArrayAsync();

            // /rest/services/... follows the GeoServices convention of carrying the
            // error in the body rather than the status line, so the bounded error is a
            // 200 envelope whose code is 404 — not a 4xx status. What matters for this
            // criterion is asserted below: it is an error, it is bounded, it carries no
            // imagery, and it does not describe the storage layout.
            var text = System.Text.Encoding.UTF8.GetString(body);
            // The GeoServices contract is specifically that the transport status stays
            // 200 and the error travels in the body. Asserting only the body would stay
            // green if this regressed to a bare HTTP 404 or 500.
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "GeoServices carries the error in the envelope, not the status line");
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json",
                "no partial or placeholder imagery may be emitted for a missing object");

            using var envelope = System.Text.Json.JsonDocument.Parse(text);
            var error = envelope.RootElement.GetProperty("error");
            error.GetProperty("code").GetInt32().Should().Be(404);
            error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();

            body.Take(4).Should().NotEqual([(byte)0x89, (byte)0x50, (byte)0x4E, (byte)0x47],
                "no PNG bytes may be emitted");
            body.Length.Should().BeLessThan(2048, "the error must be bounded, not a partial object dump");
            text.Should().NotContain(_missingObjectKey).And.NotContain(_bucket!,
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

    private static string TileUrl(int level, int row, int col, int layerId = CogLayerId) =>
        FormattableString.Invariant(
            $"/rest/services/{layerId}/ImageServer/tile/{level}/{row}/{col}?format=png");

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

    /// <summary>
    /// Reads one tile's file offset and compressed byte count out of the fixture's own
    /// TIFF directory (tags 324 <c>TileOffsets</c> and 325 <c>TileByteCounts</c> of the
    /// full-resolution IFD), so a recorded range can be identified as the tile transfer.
    /// </summary>
    /// <remarks>
    /// Deliberately a local reader over the GDAL-written bytes: asking the parser under
    /// test where its tile lives, and then asserting it read from there, would be
    /// circular. Only the little-endian, single-IFD shape these fixtures actually have
    /// is handled, and anything else fails loudly rather than guessing.
    /// </remarks>
    private static (long Offset, int ByteCount) ReadTileExtent(byte[] tiff, int tileIndex)
    {
        (tiff[0], tiff[1]).Should().Be(((byte)'I', (byte)'I'), "the fixtures are little-endian TIFFs");
        BitConverter.ToUInt16(tiff, 2).Should().Be(42, "classic TIFF, not BigTIFF");

        var ifd = (int)BitConverter.ToUInt32(tiff, 4);
        var entryCount = BitConverter.ToUInt16(tiff, ifd);

        long? offsets = null;
        long? byteCounts = null;
        for (var i = 0; i < entryCount; i++)
        {
            var entry = ifd + 2 + (i * 12);
            var tag = BitConverter.ToUInt16(tiff, entry);
            if (tag is not (324 or 325))
            {
                continue;
            }

            var type = BitConverter.ToUInt16(tiff, entry + 2);
            type.Should().Be(4, "tile offsets and byte counts are written as LONG in these fixtures");
            var count = (int)BitConverter.ToUInt32(tiff, entry + 4);
            count.Should().BeGreaterThan(tileIndex, "the fixture must contain the requested tile");

            // A LONG array of one value is inline in the entry; anything longer is
            // stored out of line and the entry holds its offset.
            var arrayStart = count == 1 ? entry + 8 : (int)BitConverter.ToUInt32(tiff, entry + 8);
            var value = BitConverter.ToUInt32(tiff, arrayStart + (tileIndex * 4));
            if (tag == 324)
            {
                offsets = value;
            }
            else
            {
                byteCounts = value;
            }
        }

        offsets.Should().NotBeNull("the fixture must carry TileOffsets");
        byteCounts.Should().NotBeNull("the fixture must carry TileByteCounts");
        return (offsets!.Value, (int)byteCounts!.Value);
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

}
