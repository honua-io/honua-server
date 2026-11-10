// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

// Staged geoprocessing outputs in the host's shared file storage (S3 or Azure Blob). The object key
// on the descriptor is the key in the bucket. A read lease and a retention hold are small sibling
// objects, as they are sibling files in the local store.
internal sealed class ObjectStorageGeoprocessingOutputObjectStore : IGeoprocessingOutputObjectStore
{
    internal const string ReadLeaseSuffix = ".readlease";
    internal const string RetentionHoldSuffix = ".hold";
    private const string SidecarMediaType = "text/plain";

    private readonly ICloudFileStorage _storage;
    private readonly string _storageKeyPrefix;
    private readonly TimeProvider _timeProvider;

    public ObjectStorageGeoprocessingOutputObjectStore(
        ICloudFileStorage storage,
        IOptions<GeoprocessingOutputStagingOptions> options,
        GeoprocessingOutputStorageKeyPrefix storageKeyPrefix,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storageKeyPrefix);

        _storage = storage;
        _storageKeyPrefix = storageKeyPrefix.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
        StoreReference = options.Value.StoreReference;
    }

    public CloudStorageProvider Provider => _storage.Provider;

    public string StoreReference { get; }

    public async Task<RasterContentIdentity> WriteAsync(
        string objectKey,
        Stream content,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var measured = new HashingReadStream(content, hash);
        var upload = await _storage.UploadIfMatchAsync(
            new FileUploadRequest
            {
                Content = measured,
                FileName = Path.GetFileName(objectKey),
                ContentType = mediaType,
                ObjectKeyOverride = objectKey
            },
            expectedETag: null,
            cancellationToken).ConfigureAwait(false);
        if (!upload.Success)
        {
            throw await _storage.ExistsAsync(objectKey, cancellationToken).ConfigureAwait(false)
                ? new InvalidOperationException(
                    $"Staged output object '{objectKey}' already exists; attempt-scoped keys are written at most once.")
                : new IOException($"Staged output object '{objectKey}' could not be written to object storage.");
        }

        return new RasterContentIdentity
        {
            SizeBytes = measured.BytesRead,
            MediaType = mediaType,
            Checksum = new RasterChecksum("sha256", Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()),
        };
    }

    public async Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var metadata = await _storage.GetMetadataAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        var download = await _storage.DownloadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        return download is null ? null : new ForwardSeekReadStream(download, metadata.SizeBytes);
    }

    public async Task<GeoprocessingStagedObjectInfo?> GetInfoAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var metadata = await _storage.GetMetadataAsync(objectKey, cancellationToken).ConfigureAwait(false);
        return metadata is null ? null : new GeoprocessingStagedObjectInfo(objectKey, metadata.SizeBytes, metadata.UploadedAt);
    }

    public async IAsyncEnumerable<GeoprocessingStagedObjectInfo> ListAsync(
        string keyPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

        var files = await _storage.ListFilesAsync(
            FolderWithinStorage(keyPrefix),
            maxResults: int.MaxValue,
            includeMetadata: false,
            cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            if (!IsSidecar(file.FileId))
            {
                yield return new GeoprocessingStagedObjectInfo(file.FileId, file.SizeBytes, file.UploadedAt);
            }
        }
    }

    public async Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var deleted = await _storage.DeleteAsync(objectKey, cancellationToken).ConfigureAwait(false);
        await _storage.DeleteAsync(objectKey + ReadLeaseSuffix, cancellationToken).ConfigureAwait(false);
        await _storage.DeleteAsync(objectKey + RetentionHoldSuffix, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public async Task<bool> TryAcquireReadLeaseAsync(
        string objectKey,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (!await _storage.ExistsAsync(objectKey, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var expiresAt = _timeProvider.GetUtcNow().Add(duration).UtcTicks.ToString(CultureInfo.InvariantCulture);
        var written = await WriteSidecarAsync(objectKey + ReadLeaseSuffix, expiresAt, replaceExisting: true, cancellationToken)
            .ConfigureAwait(false);
        return written && await _storage.ExistsAsync(objectKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasActiveReadLeaseAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var lease = await _storage.DownloadBytesAsync(objectKey + ReadLeaseSuffix, cancellationToken).ConfigureAwait(false);
        return lease is not null
            && long.TryParse(Encoding.ASCII.GetString(lease), NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAtTicks)
            && expiresAtTicks > _timeProvider.GetUtcNow().UtcTicks;
    }

    // The hold is written before the object is checked, so a sweep that deletes the object first
    // leaves this call reporting ObjectMissing, and a sweep that runs afterwards sees the hold.
    public async Task<GeoprocessingRetentionHoldResult> SetRetentionHoldAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        var holdKey = objectKey + RetentionHoldSuffix;
        var added = await WriteSidecarAsync(holdKey, "held", replaceExisting: false, cancellationToken).ConfigureAwait(false);
        if (await _storage.ExistsAsync(objectKey, cancellationToken).ConfigureAwait(false))
        {
            return added ? GeoprocessingRetentionHoldResult.Added : GeoprocessingRetentionHoldResult.AlreadyHeld;
        }

        await _storage.DeleteAsync(holdKey, cancellationToken).ConfigureAwait(false);
        return GeoprocessingRetentionHoldResult.ObjectMissing;
    }

    public Task ReleaseRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default)
        => _storage.DeleteAsync(objectKey + RetentionHoldSuffix, cancellationToken);

    public Task<bool> HasRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default)
        => _storage.ExistsAsync(objectKey + RetentionHoldSuffix, cancellationToken);

    private async Task<bool> WriteSidecarAsync(string key, string text, bool replaceExisting, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(Encoding.ASCII.GetBytes(text), writable: false);
        var request = new FileUploadRequest
        {
            Content = content,
            FileName = Path.GetFileName(key),
            ContentType = SidecarMediaType,
            SizeBytes = content.Length,
            ObjectKeyOverride = key,
            EnableChunkedUpload = false
        };
        var upload = replaceExisting
            ? await _storage.UploadAsync(request, cancellationToken).ConfigureAwait(false)
            : await _storage.UploadIfMatchAsync(request, expectedETag: null, cancellationToken).ConfigureAwait(false);
        return upload.Success;
    }

    // The storage lists by a folder under its own key prefix, while staged keys are whole bucket keys.
    private string FolderWithinStorage(string keyPrefix)
    {
        var normalized = keyPrefix.Trim('/');
        if (_storageKeyPrefix.Length == 0)
        {
            return normalized;
        }

        return normalized.StartsWith(_storageKeyPrefix + "/", StringComparison.Ordinal)
            ? normalized[(_storageKeyPrefix.Length + 1)..]
            : throw new InvalidOperationException(
                "Staged output keys are outside the file storage key prefix, so they cannot be listed.");
    }

    private static bool IsSidecar(string key)
        => key.EndsWith(ReadLeaseSuffix, StringComparison.Ordinal) || key.EndsWith(RetentionHoldSuffix, StringComparison.Ordinal);

    private sealed class HashingReadStream(Stream source, IncrementalHash hash) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            Record(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Record(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Record(ReadOnlySpan<byte> bytes)
        {
            hash.AppendData(bytes);
            BytesRead += bytes.Length;
        }
    }
}

// The key prefix the configured file storage puts in front of a listed folder (empty when none).
internal sealed record GeoprocessingOutputStorageKeyPrefix(string Value)
{
    public static GeoprocessingOutputStorageKeyPrefix FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var storage = configuration.GetSection("FileStorage");
        var configured = FileStorageProvider(configuration) switch
        {
            CloudStorageProvider.AwsS3 => storage["AwsS3:KeyPrefix"],
            CloudStorageProvider.AzureBlob => storage["AzureBlob:BlobPrefix"],
            _ => null
        };
        return new GeoprocessingOutputStorageKeyPrefix((configured ?? string.Empty).Trim('/'));
    }

    // Resolved as the file storage registration resolves it, including its environment fallback.
    public static CloudStorageProvider FileStorageProvider(Microsoft.Extensions.Configuration.IConfiguration configuration)
        => Enum.TryParse<CloudStorageProvider>(
            configuration["FileStorage:Provider"] ?? Environment.GetEnvironmentVariable("HONUA_STORAGE_PROVIDER"),
            ignoreCase: true,
            out var provider)
            ? provider
            : CloudStorageProvider.Local;
}
