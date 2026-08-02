// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Worker-only local object-store adapter used for local execution and integration tests. Raster
/// bytes are streamed through a bounded pooled buffer and atomically renamed on one filesystem;
/// this type is deliberately absent from the serving/AOT project graph.
/// </summary>
internal sealed class LocalRasterOutputObjectStore : IRasterOutputObjectStore
{
    private const int BufferSize = 64 * 1024;
    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly string _storeReference;

    public LocalRasterOutputObjectStore(string root, string storeReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeReference);
        if (storeReference.Length > 128 || storeReference.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("Store reference must be a bounded logical identifier.", nameof(storeReference));
        }

        _root = Path.GetFullPath(root);
        _rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        _storeReference = storeReference;
        Directory.CreateDirectory(_root);
    }

    public async Task<RasterStoredObject> StageAsync(
        StagedRasterOutputDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("Raster staging content must be readable.", nameof(content));
        }

        EnsureStore(descriptor.StoreReference);
        var validation = RasterOutputDescriptorValidator.Validate(descriptor);
        if (!validation.IsValid)
        {
            throw new ArgumentException("Staged raster output metadata is invalid.", nameof(descriptor));
        }

        var expectedKey = RasterOutputWorkerContract.BuildStagingObjectKey(
            descriptor.JobId,
            descriptor.Attempt,
            descriptor.OutputName);
        if (!string.Equals(descriptor.ObjectKey, expectedKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("Staging key does not match the owning job, attempt, and output.", nameof(descriptor));
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
                var identity = await CopyAndHashAsync(
                    content,
                    output,
                    descriptor.Content.Checksum!.Algorithm,
                    descriptor.Content.SizeBytes,
                    cancellationToken).ConfigureAwait(false);
                EnsureContentIdentity(descriptor.Content, identity);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
            {
                await VerifyFileAsync(destination, descriptor.Content, cancellationToken).ConfigureAwait(false);
                File.Delete(temporary);
            }
            else
            {
                try
                {
                    File.Move(temporary, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    await VerifyFileAsync(destination, descriptor.Content, cancellationToken).ConfigureAwait(false);
                    File.Delete(temporary);
                }
            }

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

        var identity = await HashFileAsync(path, "sha256", cancellationToken).ConfigureAwait(false);
        return new RasterStoredObject
        {
            StoreReference = _storeReference,
            ObjectKey = objectKey,
            ObjectVersion = "sha256:" + identity.Checksum,
            Content = new RasterContentIdentity
            {
                SizeBytes = identity.SizeBytes,
                MediaType = InferMediaType(objectKey),
                Checksum = new RasterChecksum("sha256", identity.Checksum)
            },
            State = StateFor(objectKey),
            LastModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
        };
    }

    public async Task<RasterStoredObject> PublishAsync(
        RasterObjectPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureStore(request.Stage.StoreReference);
        if (!RasterOutputDescriptorValidator.IsSafeObjectKey(request.DestinationObjectKey)
            || !request.DestinationObjectKey.StartsWith("raster/published/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Published raster key is not a safe stable key.", nameof(request));
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

            return Stored(
                request.Stage,
                request.DestinationObjectKey,
                RasterStoredObjectState.Published,
                request.PublishedAt);
        }

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Staged raster output was not found.", request.Stage.ObjectKey);
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
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        return Task.CompletedTask;
    }

    private static RasterStoredObject Stored(
        StagedRasterOutputDescriptor stage,
        string key,
        RasterStoredObjectState state,
        DateTimeOffset lastModifiedAt) => new()
        {
            StoreReference = stage.StoreReference,
            ObjectKey = key,
            ObjectVersion = stage.Content.Checksum!.Algorithm + ":" + stage.Content.Checksum.Value.ToLowerInvariant(),
            Content = stage.Content,
            State = state,
            LastModifiedAt = lastModifiedAt
        };

    private static async Task<FileIdentity> CopyAndHashAsync(
        Stream source,
        Stream destination,
        string algorithm,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(ToHashAlgorithm(algorithm));
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long size = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, read);
                size = checked(size + read);
                if (size > maximumBytes)
                {
                    throw new InvalidDataException(
                        "Raster output bytes exceed their declared content size.");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        return new FileIdentity(size, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<FileIdentity> HashFileAsync(
        string path,
        string algorithm,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await CopyAndHashAsync(
            source,
            Stream.Null,
            algorithm,
            long.MaxValue,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyFileAsync(
        string path,
        RasterContentIdentity expected,
        CancellationToken cancellationToken)
    {
        var actual = await HashFileAsync(path, expected.Checksum!.Algorithm, cancellationToken).ConfigureAwait(false);
        EnsureContentIdentity(expected, actual);
    }

    private static void EnsureContentIdentity(RasterContentIdentity expected, FileIdentity actual)
    {
        var checksum = expected.Checksum!;
        if (expected.SizeBytes != actual.SizeBytes
            || !string.Equals(checksum.Value, actual.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Raster output bytes do not match declared size/checksum metadata.");
        }
    }

    private static HashAlgorithmName ToHashAlgorithm(string algorithm) => algorithm switch
    {
        "sha256" => HashAlgorithmName.SHA256,
        "sha512" => HashAlgorithmName.SHA512,
        _ => throw new InvalidDataException("Raster output checksum algorithm is unsupported.")
    };

    private static string InferMediaType(string objectKey) => Path.GetExtension(objectKey).ToLowerInvariant() switch
    {
        ".tif" or ".tiff" => "image/tiff",
        ".zarr" => "application/vnd+zarr",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };

    private static RasterStoredObjectState StateFor(string objectKey) =>
        objectKey.StartsWith("raster/published/", StringComparison.Ordinal)
            ? RasterStoredObjectState.Published
            : RasterStoredObjectState.Staged;

    private void EnsureStore(string storeReference)
    {
        if (!string.Equals(storeReference, _storeReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Raster output references an unconfigured object store.");
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
            throw new ArgumentException("Raster output object key escapes the configured root.", nameof(objectKey));
        }

        return path;
    }

    private sealed record FileIdentity(long SizeBytes, string Checksum);
}
