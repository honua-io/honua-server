// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

/// <summary>Shared-filesystem raster output store for local/on-prem worker deployments.</summary>
internal sealed class LocalRasterOutputObjectStore : IRasterOutputObjectStore, IRasterOutputManifestStore
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumManifestBytes = 256 * 1024;
    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly string _storeReference;

    public LocalRasterOutputObjectStore(
        IOptions<CloudStorageOptions> storageOptions,
        IOptions<RasterOutputPublicationOptions> publicationOptions)
    {
        var local = storageOptions?.Value?.LocalStorage
            ?? throw new InvalidOperationException("Local file storage is not configured for raster outputs.");
        _root = Path.GetFullPath(local.BasePath);
        _rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        var storeReference = publicationOptions?.Value?.StoreReference;
        if (!RasterOutputWorkerContract.IsLogicalStoreReference(storeReference))
        {
            throw new InvalidOperationException("Raster output store reference is invalid.");
        }

        _storeReference = storeReference!;

        if (local.CreateDirectoryIfNotExists)
        {
            Directory.CreateDirectory(_root);
        }
    }

    public async Task<RasterStoredObject> StageAsync(
        StagedRasterOutputDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(content);
        EnsureStore(descriptor.StoreReference);
        var validation = RasterOutputDescriptorValidator.Validate(descriptor);
        if (!validation.IsValid)
        {
            throw new ArgumentException("Staged raster output metadata is invalid.", nameof(descriptor));
        }

        var destination = ResolvePath(descriptor.ObjectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".upload-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyAndVerifyAsync(content, output, descriptor.Content, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(destination))
            {
                await VerifyFileAsync(destination, descriptor.Content, cancellationToken).ConfigureAwait(false);
                File.Delete(temporary);
            }
            else
            {
                File.Move(temporary, destination);
            }

            await WriteExpiryMetadataAsync(destination, descriptor.ExpiresAt, cancellationToken)
                .ConfigureAwait(false);

            return Stored(descriptor, descriptor.ObjectKey, RasterStoredObjectState.Staged, descriptor.CreatedAt);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<RasterStoredObject?> InspectAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        EnsureStore(storeReference);
        var path = ResolvePath(objectKey);
        if (!File.Exists(path))
        {
            return null;
        }

        var identity = await HashAsync(path, cancellationToken).ConfigureAwait(false);
        var state = objectKey.StartsWith("raster/published/", StringComparison.Ordinal)
            ? RasterStoredObjectState.Published
            : RasterStoredObjectState.Staged;
        return new RasterStoredObject
        {
            StoreReference = _storeReference,
            ObjectKey = objectKey,
            ObjectVersion = "sha256:" + identity.Checksum,
            Content = new RasterContentIdentity
            {
                SizeBytes = identity.Size,
                MediaType = InferMediaType(objectKey),
                Checksum = new RasterChecksum("sha256", identity.Checksum)
            },
            State = state,
            LastModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
            ExpiresAt = state == RasterStoredObjectState.Staged
                ? await TryReadStageExpiryAsync(objectKey, path, cancellationToken).ConfigureAwait(false)
                : null
        };
    }

    public Task<Stream?> OpenReadAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStore(storeReference);
        var path = ResolvePath(objectKey);
        Stream? stream = File.Exists(path)
            ? new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
        return Task.FromResult(stream);
    }

    public async Task<RasterStoredObject> PublishAsync(
        RasterObjectPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureStore(request.Stage.StoreReference);
        if (!request.DestinationObjectKey.StartsWith("raster/published/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Published raster key is outside the immutable prefix.", nameof(request));
        }

        var source = ResolvePath(request.Stage.ObjectKey);
        var destination = ResolvePath(request.DestinationObjectKey);
        if (File.Exists(destination))
        {
            await VerifyFileAsync(destination, request.Stage.Content, cancellationToken).ConfigureAwait(false);
            if (File.Exists(source))
            {
                File.Delete(source);
            }
        }
        else
        {
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("Staged local raster output was not found.", request.Stage.ObjectKey);
            }

            await VerifyFileAsync(source, request.Stage.Content, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Move(source, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                await VerifyFileAsync(destination, request.Stage.Content, cancellationToken).ConfigureAwait(false);
                if (File.Exists(source))
                {
                    File.Delete(source);
                }
            }
        }

        DeleteExpiryMetadata(source);
        File.SetLastWriteTimeUtc(destination, request.PublishedAt.UtcDateTime);
        return Stored(
            request.Stage,
            request.DestinationObjectKey,
            RasterStoredObjectState.Published,
            request.PublishedAt);
    }

    public async IAsyncEnumerable<RasterStoredObject> ListExpiredAsync(
        DateTimeOffset olderThan,
        int maximumCount,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        var rasterRoot = Path.Combine(_root, "raster");
        if (!Directory.Exists(rasterRoot))
        {
            yield break;
        }

        var count = 0;
        foreach (var path in Directory.EnumerateFiles(rasterRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.EndsWith(RasterOutputWorkerContract.LocalExpiryMetadataSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            if (File.GetLastWriteTimeUtc(path) >= olderThan.UtcDateTime)
            {
                continue;
            }

            var key = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
            var candidate = await InspectAsync(_storeReference, key, cancellationToken).ConfigureAwait(false);
            if (candidate is not null)
            {
                yield return candidate;
                count++;
                if (count >= maximumCount)
                {
                    yield break;
                }
            }
        }
    }

    public Task DeleteAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStore(storeReference);
        var path = ResolvePath(objectKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        DeleteExpiryMetadata(path);

        return Task.CompletedTask;
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

        var destination = ResolvePath(manifestObjectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".upload-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<RasterOutputPublicationManifest?> ReadManifestAsync(
        string storeReference,
        string manifestObjectKey,
        CancellationToken cancellationToken = default)
    {
        EnsureStore(storeReference);
        var path = ResolvePath(manifestObjectKey);
        if (!File.Exists(path))
        {
            return null;
        }

        if (new FileInfo(path).Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Raster output publication manifest exceeds 256 KiB.");
        }

        var manifest = RasterOutputJson.DeserializeManifest(
            await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        EnsureManifest(manifestObjectKey, manifest);
        return manifest;
    }

    private static async Task CopyAndVerifyAsync(
        Stream source,
        Stream destination,
        RasterContentIdentity expected,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long size = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            size = checked(size + read);
            if (size > expected.SizeBytes)
            {
                throw new InvalidDataException("Raster output exceeds its declared size.");
            }
        }

        var checksum = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (size != expected.SizeBytes
            || !string.Equals(checksum, expected.Checksum!.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Raster output bytes do not match their declared identity.");
        }
    }

    private static async Task VerifyFileAsync(
        string path,
        RasterContentIdentity expected,
        CancellationToken cancellationToken)
    {
        var actual = await HashAsync(path, cancellationToken).ConfigureAwait(false);
        if (actual.Size != expected.SizeBytes
            || !string.Equals(actual.Checksum, expected.Checksum!.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Raster output bytes do not match their declared identity.");
        }
    }

    private static async Task<(long Size, string Checksum)> HashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long size = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            size = checked(size + read);
        }

        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private async Task<DateTimeOffset?> TryReadStageExpiryAsync(
        string objectKey,
        string objectPath,
        CancellationToken cancellationToken)
    {
        var metadataPath = ExpiryMetadataPath(objectPath);
        if (File.Exists(metadataPath))
        {
            if (new FileInfo(metadataPath).Length > RasterOutputWorkerContract.MaximumLocalExpiryMetadataBytes)
            {
                throw new InvalidDataException("Local raster expiry metadata exceeds its bounded size.");
            }

            return ParseExpiry(
                await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false));
        }

        var segments = objectKey.Split('/');
        if (segments.Length < 5
            || !string.Equals(segments[0], "raster", StringComparison.Ordinal)
            || !string.Equals(segments[1], "staging", StringComparison.Ordinal)
            || !segments[3].StartsWith("attempt-", StringComparison.Ordinal)
            || !int.TryParse(
                segments[3].AsSpan("attempt-".Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var attempt)
            || attempt < 0
            || !RasterOutputWorkerContract.IsLogicalStoreReference(segments[2]))
        {
            return null;
        }

        var manifestKey = RasterOutputWorkerContract.BuildManifestObjectKey(segments[2], attempt);
        var manifestPath = ResolvePath(manifestKey);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        if (new FileInfo(manifestPath).Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Raster output publication manifest exceeds 256 KiB.");
        }

        var manifest = RasterOutputJson.DeserializeManifest(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        EnsureManifest(manifestKey, manifest);
        if (string.Equals(objectKey, manifestKey, StringComparison.Ordinal))
        {
            return manifest.Outputs.Max(output => output.ExpiresAt);
        }

        return manifest.Outputs.FirstOrDefault(output =>
            string.Equals(output.ObjectKey, objectKey, StringComparison.Ordinal))?.ExpiresAt;
    }

    private static async Task WriteExpiryMetadataAsync(
        string objectPath,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var metadataPath = ExpiryMetadataPath(objectPath);
        var temporary = metadataPath + ".upload-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                expiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, metadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static DateTimeOffset ParseExpiry(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var expiresAt))
        {
            throw new InvalidDataException("Local raster object contains invalid expiry metadata.");
        }

        return expiresAt;
    }

    private static void DeleteExpiryMetadata(string objectPath)
    {
        var metadataPath = ExpiryMetadataPath(objectPath);
        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
        }
    }

    private static string ExpiryMetadataPath(string objectPath) =>
        objectPath + RasterOutputWorkerContract.LocalExpiryMetadataSuffix;

    private static RasterStoredObject Stored(
        StagedRasterOutputDescriptor stage,
        string key,
        RasterStoredObjectState state,
        DateTimeOffset lastModifiedAt) => new()
        {
            StoreReference = stage.StoreReference,
            ObjectKey = key,
            ObjectVersion = "sha256:" + stage.Content.Checksum!.Value.ToLowerInvariant(),
            Content = stage.Content,
            State = state,
            LastModifiedAt = lastModifiedAt,
            ExpiresAt = state == RasterStoredObjectState.Staged ? stage.ExpiresAt : null
        };

    private void EnsureStore(string storeReference)
    {
        if (!string.Equals(storeReference, _storeReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Raster output references an unconfigured local store.");
        }
    }

    private string ResolvePath(string objectKey)
    {
        if (!RasterOutputDescriptorValidator.IsSafeObjectKey(objectKey))
        {
            throw new ArgumentException("Raster output object key is unsafe.", nameof(objectKey));
        }

        var path = Path.GetFullPath(Path.Combine(
            _root,
            objectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Raster output object key escapes its configured root.", nameof(objectKey));
        }

        return path;
    }

    private static void EnsureManifest(string key, RasterOutputPublicationManifest manifest)
    {
        var expected = RasterOutputWorkerContract.BuildManifestObjectKey(manifest.JobId, manifest.Attempt);
        if (!string.Equals(key, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException("Raster publication manifest key does not match its job attempt.", nameof(key));
        }

        var validation = RasterOutputDescriptorValidator.Validate(manifest);
        if (!validation.IsValid)
        {
            throw new ArgumentException("Raster publication manifest is invalid.", nameof(manifest));
        }
    }

    private static string InferMediaType(string objectKey) => Path.GetExtension(objectKey).ToLowerInvariant() switch
    {
        ".tif" or ".tiff" => "image/tiff",
        ".zarr" => "application/vnd+zarr",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
