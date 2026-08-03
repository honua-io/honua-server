// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

/// <summary>S3-backed, streaming raster output staging and same-bucket publication.</summary>
internal sealed class AwsS3RasterOutputObjectStore : IRasterOutputObjectStore, IRasterOutputManifestStore
{
    private const int MaximumManifestBytes = 256 * 1024;
    private const string ChecksumAlgorithmMetadata = "honua-checksum-algorithm";
    private const string ChecksumValueMetadata = "honua-checksum-value";
    private const string OutputStateMetadata = "honua-raster-state";
    private const string LogicalKeyMetadata = "honua-logical-key";
    private const string ExpiresAtMetadata = "honua-expires-at";
    private readonly IAmazonS3 _client;
    private readonly AwsS3Options _options;
    private readonly string _storeReference;

    public AwsS3RasterOutputObjectStore(
        IOptions<CloudStorageOptions> storageOptions,
        IOptions<RasterOutputPublicationOptions> publicationOptions)
        : this(
            CreateClient(storageOptions?.Value?.AwsS3),
            storageOptions?.Value?.AwsS3,
            publicationOptions?.Value?.StoreReference)
    {
    }

    internal AwsS3RasterOutputObjectStore(
        IAmazonS3 client,
        AwsS3Options? options,
        string? storeReference)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new InvalidOperationException("AWS S3 options are not configured.");
        if (string.IsNullOrWhiteSpace(_options.BucketName))
        {
            throw new InvalidOperationException("AWS S3 bucket name is required for raster outputs.");
        }

        _storeReference = RasterOutputWorkerContract.IsLogicalStoreReference(storeReference)
            ? storeReference!
            : throw new InvalidOperationException("Raster output store reference is invalid.");
    }

    public async Task<RasterStoredObject> StageAsync(
        StagedRasterOutputDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(content);
        EnsureStore(descriptor.StoreReference);
        EnsureStage(descriptor);
        var checksum = RequireSha256(descriptor.Content);
        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = PhysicalKey(descriptor.ObjectKey),
            InputStream = content,
            ContentType = descriptor.Content.MediaType,
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(checksum.Value))
        };
        request.Headers.ContentLength = descriptor.Content.SizeBytes;
        request.Metadata[ChecksumAlgorithmMetadata] = checksum.Algorithm;
        request.Metadata[ChecksumValueMetadata] = checksum.Value;
        request.Metadata[OutputStateMetadata] = "staged";
        request.Metadata[LogicalKeyMetadata] = descriptor.ObjectKey;
        request.Metadata[ExpiresAtMetadata] = descriptor.ExpiresAt.ToString(
            "O",
            System.Globalization.CultureInfo.InvariantCulture);
        if (_options.EnableServerSideEncryption)
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }

        await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        var stored = await InspectAsync(descriptor.StoreReference, descriptor.ObjectKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("S3 did not expose the staged raster after upload.");
        EnsureIdentity(descriptor.Content, stored.Content);
        return stored;
    }

    public async Task<RasterStoredObject?> InspectAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        EnsureStore(storeReference);
        EnsureKey(objectKey);
        try
        {
            var response = await _client.GetObjectMetadataAsync(
                _options.BucketName,
                PhysicalKey(objectKey),
                cancellationToken).ConfigureAwait(false);
            var algorithm = Metadata(response.Metadata, ChecksumAlgorithmMetadata);
            var checksum = Metadata(response.Metadata, ChecksumValueMetadata);
            if (string.IsNullOrWhiteSpace(algorithm) || string.IsNullOrWhiteSpace(checksum))
            {
                throw new InvalidDataException("S3 raster object is missing verified checksum metadata.");
            }

            var state = string.Equals(
                Metadata(response.Metadata, OutputStateMetadata),
                "published",
                StringComparison.Ordinal)
                ? RasterStoredObjectState.Published
                : RasterStoredObjectState.Staged;
            var lastModified = response.LastModified?.ToUniversalTime() ?? DateTime.UtcNow;
            return new RasterStoredObject
            {
                StoreReference = _storeReference,
                ObjectKey = objectKey,
                ObjectVersion = !string.IsNullOrWhiteSpace(response.VersionId)
                    ? "version:" + response.VersionId
                    : "etag:" + (response.ETag ?? checksum).Trim('"'),
                Content = new RasterContentIdentity
                {
                    SizeBytes = response.ContentLength,
                    MediaType = response.Headers.ContentType ?? InferMediaType(objectKey),
                    Checksum = new RasterChecksum(algorithm, checksum)
                },
                State = state,
                LastModifiedAt = new DateTimeOffset(lastModified),
                ExpiresAt = ParseExpiry(Metadata(response.Metadata, ExpiresAtMetadata))
            };
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream?> OpenReadAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        EnsureStore(storeReference);
        EnsureKey(objectKey);
        try
        {
            var response = await _client.GetObjectAsync(
                _options.BucketName,
                PhysicalKey(objectKey),
                cancellationToken).ConfigureAwait(false);
            return new ResponseOwnedStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<RasterStoredObject> PublishAsync(
        RasterObjectPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureStore(request.Stage.StoreReference);
        EnsurePublishedKey(request.DestinationObjectKey);
        var existing = await InspectAsync(
            request.Stage.StoreReference,
            request.DestinationObjectKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureIdentity(request.Stage.Content, existing.Content);
            await DeleteAsync(request.Stage.StoreReference, request.Stage.ObjectKey, cancellationToken)
                .ConfigureAwait(false);
            return existing with { State = RasterStoredObjectState.Published };
        }

        var staged = await InspectAsync(
            request.Stage.StoreReference,
            request.Stage.ObjectKey,
            cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Staged S3 raster output was not found.", request.Stage.ObjectKey);
        EnsureIdentity(request.Stage.Content, staged.Content);
        var copy = new CopyObjectRequest
        {
            SourceBucket = _options.BucketName,
            SourceKey = PhysicalKey(request.Stage.ObjectKey),
            DestinationBucket = _options.BucketName,
            DestinationKey = PhysicalKey(request.DestinationObjectKey),
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = request.Stage.Content.MediaType
        };
        copy.Metadata[ChecksumAlgorithmMetadata] = request.Stage.Content.Checksum!.Algorithm;
        copy.Metadata[ChecksumValueMetadata] = request.Stage.Content.Checksum.Value;
        copy.Metadata[OutputStateMetadata] = "published";
        copy.Metadata[LogicalKeyMetadata] = request.DestinationObjectKey;
        if (_options.EnableServerSideEncryption)
        {
            copy.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }

        await _client.CopyObjectAsync(copy, cancellationToken).ConfigureAwait(false);
        var published = await InspectAsync(
            request.Stage.StoreReference,
            request.DestinationObjectKey,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("S3 did not expose the published raster after copy.");
        EnsureIdentity(request.Stage.Content, published.Content);
        await DeleteAsync(request.Stage.StoreReference, request.Stage.ObjectKey, cancellationToken)
            .ConfigureAwait(false);
        return published;
    }

    public async IAsyncEnumerable<RasterStoredObject> ListExpiredAsync(
        DateTimeOffset olderThan,
        int maximumCount,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        string? continuation = null;
        var emitted = 0;
        do
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _options.BucketName,
                Prefix = PhysicalKey("raster/"),
                ContinuationToken = continuation,
                MaxKeys = Math.Min(1000, maximumCount - emitted)
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.LastModified is not { } modified || modified >= olderThan.UtcDateTime)
                {
                    continue;
                }

                var logicalKey = LogicalKey(item.Key);
                var candidate = await InspectAsync(_storeReference, logicalKey, cancellationToken)
                    .ConfigureAwait(false);
                if (candidate is null)
                {
                    continue;
                }

                yield return candidate;
                emitted++;
                if (emitted >= maximumCount)
                {
                    yield break;
                }
            }

            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrWhiteSpace(continuation) && emitted < maximumCount);
    }

    public async Task DeleteAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        EnsureStore(storeReference);
        EnsureKey(objectKey);
        await _client.DeleteObjectAsync(
            _options.BucketName,
            PhysicalKey(objectKey),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteManifestAsync(
        string storeReference,
        string manifestObjectKey,
        RasterOutputPublicationManifest manifest,
        CancellationToken cancellationToken = default)
    {
        EnsureStore(storeReference);
        EnsureManifest(manifestObjectKey, manifest);
        var json = RasterOutputJson.SerializeManifest(manifest);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Raster output publication manifest exceeds 256 KiB.");
        }

        await using var content = new MemoryStream(bytes, writable: false);
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = PhysicalKey(manifestObjectKey),
            InputStream = content,
            ContentType = "application/json",
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(checksum))
        };
        request.Headers.ContentLength = bytes.LongLength;
        request.Metadata[ChecksumAlgorithmMetadata] = "sha256";
        request.Metadata[ChecksumValueMetadata] = checksum;
        request.Metadata[OutputStateMetadata] = "staged";
        request.Metadata[LogicalKeyMetadata] = manifestObjectKey;
        request.Metadata[ExpiresAtMetadata] = manifest.Outputs.Max(output => output.ExpiresAt).ToString(
            "O",
            System.Globalization.CultureInfo.InvariantCulture);
        if (_options.EnableServerSideEncryption)
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }

        await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RasterOutputPublicationManifest?> ReadManifestAsync(
        string storeReference,
        string manifestObjectKey,
        CancellationToken cancellationToken = default)
    {
        await using var content = await OpenReadAsync(storeReference, manifestObjectKey, cancellationToken)
            .ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }

        using var reader = new StreamReader(
            content,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: false);
        var buffer = new char[MaximumManifestBytes + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (count > MaximumManifestBytes)
        {
            throw new InvalidDataException("Raster output publication manifest exceeds 256 KiB.");
        }

        var manifest = RasterOutputJson.DeserializeManifest(new string(buffer, 0, count));
        EnsureManifest(manifestObjectKey, manifest);
        return manifest;
    }

    private static AmazonS3Client CreateClient(AwsS3Options? options)
    {
        if (options is null)
        {
            throw new InvalidOperationException("AWS S3 options are not configured.");
        }

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            RegionEndpoint = string.IsNullOrWhiteSpace(options.Region)
                ? null
                : RegionEndpoint.GetBySystemName(options.Region)
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }

        return !string.IsNullOrWhiteSpace(options.AccessKeyId)
            ? new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
                config)
            : new AmazonS3Client(config);
    }

    private string PhysicalKey(string logicalKey) => string.IsNullOrWhiteSpace(_options.KeyPrefix)
        ? logicalKey
        : _options.KeyPrefix.Trim('/') + "/" + logicalKey;

    private string LogicalKey(string physicalKey)
    {
        if (string.IsNullOrWhiteSpace(_options.KeyPrefix))
        {
            return physicalKey;
        }

        var prefix = _options.KeyPrefix.Trim('/') + "/";
        return physicalKey.StartsWith(prefix, StringComparison.Ordinal)
            ? physicalKey[prefix.Length..]
            : physicalKey;
    }

    private void EnsureStore(string storeReference)
    {
        if (!string.Equals(storeReference, _storeReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Raster output references an unconfigured S3 store.");
        }
    }

    private static void EnsureKey(string key)
    {
        if (!RasterOutputDescriptorValidator.IsSafeObjectKey(key))
        {
            throw new ArgumentException("Raster output object key is unsafe.", nameof(key));
        }
    }

    private static void EnsurePublishedKey(string key)
    {
        EnsureKey(key);
        if (!key.StartsWith("raster/published/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Published raster key is outside the immutable prefix.", nameof(key));
        }
    }

    private static void EnsureStage(StagedRasterOutputDescriptor descriptor)
    {
        var validation = RasterOutputDescriptorValidator.Validate(descriptor);
        if (!validation.IsValid)
        {
            throw new ArgumentException("Staged raster output metadata is invalid.", nameof(descriptor));
        }
    }

    private static RasterChecksum RequireSha256(RasterContentIdentity content)
    {
        if (content.Checksum is not { Algorithm: "sha256" } checksum)
        {
            throw new InvalidDataException("S3 raster output staging requires a sha256 checksum.");
        }

        return checksum;
    }

    private static void EnsureIdentity(RasterContentIdentity expected, RasterContentIdentity actual)
    {
        if (expected != actual)
        {
            throw new InvalidDataException("S3 raster output does not match its declared identity.");
        }
    }

    private static void EnsureManifest(string key, RasterOutputPublicationManifest manifest)
    {
        var expectedKey = RasterOutputWorkerContract.BuildManifestObjectKey(manifest.JobId, manifest.Attempt);
        if (!string.Equals(key, expectedKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("Raster publication manifest key does not match its job attempt.", nameof(key));
        }

        var validation = RasterOutputDescriptorValidator.Validate(manifest);
        if (!validation.IsValid)
        {
            throw new ArgumentException("Raster publication manifest is invalid.", nameof(manifest));
        }
    }

    private static string? Metadata(MetadataCollection metadata, string name)
    {
        foreach (var key in metadata.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("-" + name, StringComparison.OrdinalIgnoreCase))
            {
                return metadata[key];
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseExpiry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var expiresAt))
        {
            throw new InvalidDataException("S3 raster object contains invalid expiry metadata.");
        }

        return expiresAt;
    }

    private static string InferMediaType(string key) => key.EndsWith(".zarr", StringComparison.OrdinalIgnoreCase)
        ? "application/vnd+zarr"
        : "image/tiff";

    private sealed class ResponseOwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => base.DisposeAsync();
    }
}
