// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

/// <summary>Azure Blob-backed, streaming raster output staging and server-side publication.</summary>
internal sealed class AzureBlobRasterOutputObjectStore : IRasterOutputObjectStore, IRasterOutputManifestStore
{
    private const int MaximumManifestBytes = 256 * 1024;
    private const string ChecksumAlgorithmMetadata = "honua_checksum_algorithm";
    private const string ChecksumValueMetadata = "honua_checksum_value";
    private const string OutputStateMetadata = "honua_raster_state";
    private const string LogicalKeyMetadata = "honua_logical_key";
    private readonly BlobContainerClient _container;
    private readonly AzureBlobOptions _options;
    private readonly string _storeReference;

    public AzureBlobRasterOutputObjectStore(
        IOptions<CloudStorageOptions> storageOptions,
        IOptions<RasterOutputPublicationOptions> publicationOptions)
        : this(
            CreateContainer(storageOptions?.Value?.AzureBlob),
            storageOptions?.Value?.AzureBlob,
            publicationOptions?.Value?.StoreReference)
    {
    }

    internal AzureBlobRasterOutputObjectStore(
        BlobContainerClient container,
        AzureBlobOptions? options,
        string? storeReference)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _options = options ?? throw new InvalidOperationException("Azure Blob options are not configured.");
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
        var existing = await InspectAsync(
            descriptor.StoreReference,
            descriptor.ObjectKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureIdentity(descriptor.Content, existing.Content);
            return existing;
        }

        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var blob = _container.GetBlobClient(PhysicalKey(descriptor.ObjectKey));
        try
        {
            await using var verifying = new Sha256VerifyingReadStream(content, descriptor.Content);
            await blob.UploadAsync(verifying, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = descriptor.Content.MediaType },
                Metadata = Metadata(descriptor.Content, "staged", descriptor.ObjectKey)
            }, cancellationToken).ConfigureAwait(false);
            verifying.EnsureComplete();
        }
        catch
        {
            await blob.DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var stored = await InspectAsync(descriptor.StoreReference, descriptor.ObjectKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Azure Blob did not expose the staged raster after upload.");
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
            var properties = (await _container.GetBlobClient(PhysicalKey(objectKey))
                .GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
            if (!properties.Metadata.TryGetValue(ChecksumAlgorithmMetadata, out var algorithm)
                || !properties.Metadata.TryGetValue(ChecksumValueMetadata, out var checksum))
            {
                throw new InvalidDataException("Azure raster blob is missing verified checksum metadata.");
            }

            return new RasterStoredObject
            {
                StoreReference = _storeReference,
                ObjectKey = objectKey,
                ObjectVersion = !string.IsNullOrWhiteSpace(properties.VersionId)
                    ? "version:" + properties.VersionId
                    : "etag:" + properties.ETag.ToString().Trim('"'),
                Content = new RasterContentIdentity
                {
                    SizeBytes = properties.ContentLength,
                    MediaType = properties.ContentType ?? InferMediaType(objectKey),
                    Checksum = new RasterChecksum(algorithm, checksum)
                },
                State = properties.Metadata.TryGetValue(OutputStateMetadata, out var state)
                    && string.Equals(state, "published", StringComparison.Ordinal)
                        ? RasterStoredObjectState.Published
                        : RasterStoredObjectState.Staged,
                LastModifiedAt = properties.LastModified
            };
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
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
            var response = await _container.GetBlobClient(PhysicalKey(objectKey))
                .DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Value.Content;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
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

        var source = _container.GetBlobClient(PhysicalKey(request.Stage.ObjectKey));
        var staged = await InspectAsync(
            request.Stage.StoreReference,
            request.Stage.ObjectKey,
            cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Staged Azure raster output was not found.", request.Stage.ObjectKey);
        EnsureIdentity(request.Stage.Content, staged.Content);
        if (!source.CanGenerateSasUri)
        {
            throw new InvalidOperationException(
                "Azure raster publication requires a shared-key client capable of a short-lived read SAS.");
        }

        var sourceUri = source.GenerateSasUri(
            BlobSasPermissions.Read,
            DateTimeOffset.UtcNow.AddMinutes(15));
        var destination = _container.GetBlobClient(PhysicalKey(request.DestinationObjectKey));
        var operation = await destination.StartCopyFromUriAsync(sourceUri, new BlobCopyFromUriOptions
        {
            Metadata = Metadata(request.Stage.Content, "published", request.DestinationObjectKey)
        }, cancellationToken).ConfigureAwait(false);
        await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);

        var published = await InspectAsync(
            request.Stage.StoreReference,
            request.DestinationObjectKey,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Azure Blob did not expose the published raster after copy.");
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
        var emitted = 0;
        await foreach (var item in _container.GetBlobsAsync(
            BlobTraits.None,
            BlobStates.None,
            prefix: PhysicalKey("raster/"),
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (item.Properties.LastModified is not { } modified || modified >= olderThan)
            {
                continue;
            }

            var candidate = await InspectAsync(
                _storeReference,
                LogicalKey(item.Name),
                cancellationToken).ConfigureAwait(false);
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
    }

    public async Task DeleteAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        EnsureStore(storeReference);
        EnsureKey(objectKey);
        await _container.GetBlobClient(PhysicalKey(objectKey))
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WriteManifestAsync(
        string storeReference,
        string manifestObjectKey,
        RasterOutputPublicationManifest manifest,
        CancellationToken cancellationToken = default)
    {
        EnsureStore(storeReference);
        EnsureManifest(manifestObjectKey, manifest);
        var bytes = Encoding.UTF8.GetBytes(RasterOutputJson.SerializeManifest(manifest));
        if (bytes.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Raster output publication manifest exceeds 256 KiB.");
        }

        await using var content = new MemoryStream(bytes, writable: false);
        var identity = new RasterContentIdentity
        {
            SizeBytes = bytes.LongLength,
            MediaType = "application/json",
            Checksum = new RasterChecksum(
                "sha256",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
        };
        await _container.GetBlobClient(PhysicalKey(manifestObjectKey)).UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Metadata = Metadata(identity, "staged", manifestObjectKey)
            },
            cancellationToken).ConfigureAwait(false);
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

    private static BlobContainerClient CreateContainer(AzureBlobOptions? options)
    {
        if (options is null || string.IsNullOrWhiteSpace(options.ConnectionString)
            || string.IsNullOrWhiteSpace(options.ContainerName))
        {
            throw new InvalidOperationException("Azure Blob options are not configured for raster outputs.");
        }

        return new BlobContainerClient(options.ConnectionString, options.ContainerName);
    }

    private static Dictionary<string, string> Metadata(
        RasterContentIdentity content,
        string state,
        string logicalKey) => new(StringComparer.Ordinal)
        {
            [ChecksumAlgorithmMetadata] = content.Checksum!.Algorithm,
            [ChecksumValueMetadata] = content.Checksum.Value,
            [OutputStateMetadata] = state,
            [LogicalKeyMetadata] = logicalKey
        };

    private string PhysicalKey(string logicalKey) => string.IsNullOrWhiteSpace(_options.BlobPrefix)
        ? logicalKey
        : _options.BlobPrefix.Trim('/') + "/" + logicalKey;

    private string LogicalKey(string physicalKey)
    {
        if (string.IsNullOrWhiteSpace(_options.BlobPrefix))
        {
            return physicalKey;
        }

        var prefix = _options.BlobPrefix.Trim('/') + "/";
        return physicalKey.StartsWith(prefix, StringComparison.Ordinal)
            ? physicalKey[prefix.Length..]
            : physicalKey;
    }

    private void EnsureStore(string storeReference)
    {
        if (!string.Equals(storeReference, _storeReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Raster output references an unconfigured Azure Blob store.");
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

        if (descriptor.Content.Checksum is not { Algorithm: "sha256" })
        {
            throw new InvalidDataException("Azure raster output staging requires a sha256 checksum.");
        }
    }

    private static void EnsureIdentity(RasterContentIdentity expected, RasterContentIdentity actual)
    {
        if (expected != actual)
        {
            throw new InvalidDataException("Azure raster output does not match its declared identity.");
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

    private static string InferMediaType(string key) => key.EndsWith(".zarr", StringComparison.OrdinalIgnoreCase)
        ? "application/vnd+zarr"
        : "image/tiff";

    private sealed class Sha256VerifyingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly RasterContentIdentity _expected;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        private bool _completed;

        public Sha256VerifyingReadStream(Stream inner, RasterContentIdentity expected)
        {
            _inner = inner;
            _expected = expected;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Append(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Append(buffer.Span[..read]);
            return read;
        }

        public void EnsureComplete()
        {
            if (!_completed)
            {
                throw new InvalidDataException("Azure Blob upload did not consume the complete raster stream.");
            }

            var checksum = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
            if (_length != _expected.SizeBytes
                || !string.Equals(checksum, _expected.Checksum!.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Azure raster output bytes do not match their declared identity.");
            }
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                _completed = true;
                return;
            }

            _hash.AppendData(bytes);
            _length = checked(_length + bytes.Length);
            if (_length > _expected.SizeBytes)
            {
                throw new InvalidDataException("Azure raster output exceeds its declared size.");
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => base.DisposeAsync();
    }
}
