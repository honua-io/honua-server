// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Scene.Assets;

internal sealed partial class SceneObjectOriginReader : ISceneObjectOriginReader
{
    private const int FileStreamBufferSize = 81_920;

    private readonly ICloudFileStorage? _storage;
    private readonly IReadOnlyList<ICloudRangeReader> _rangeReaders;
    private readonly CloudStorageOptions? _storageOptions;
    private readonly SceneObjectOriginOptions _options;
    private readonly ILogger<SceneObjectOriginReader> _logger;

    public SceneObjectOriginReader(
        IOptions<SceneObjectOriginOptions> options,
        ILogger<SceneObjectOriginReader> logger,
        ICloudFileStorage? storage = null,
        IEnumerable<ICloudRangeReader>? rangeReaders = null,
        IOptions<CloudStorageOptions>? storageOptions = null)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storage = storage;
        _rangeReaders = rangeReaders?.ToArray() ?? [];
        _storageOptions = storageOptions?.Value;
    }

    public async Task<SceneObjectAssetMetadata?> GetMetadataAsync(
        SceneDataset scene,
        string relativeAssetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (!IsAllowedAssetPath(relativeAssetPath))
        {
            return null;
        }

        if (SceneAssetResolver.TryResolve(scene, relativeAssetPath, out var local, out _))
        {
            return BuildLocalMetadata(relativeAssetPath, local);
        }

        return await TryGetObjectMetadataAsync(scene, relativeAssetPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SceneObjectAssetReadResult> OpenReadAsync(
        SceneDataset scene,
        string relativeAssetPath,
        SceneByteRange? range = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (!IsAllowedAssetPath(relativeAssetPath))
        {
            return SceneObjectAssetReadResult.InvalidPath("Invalid scene asset path.");
        }

        if (SceneAssetResolver.TryResolve(scene, relativeAssetPath, out var local, out var localError))
        {
            return OpenLocalAsset(relativeAssetPath, local, range);
        }

        if (localError is SceneAssetResolutionError.InvalidPath)
        {
            return SceneObjectAssetReadResult.InvalidPath("Invalid scene asset path.");
        }

        if (localError is SceneAssetResolutionError.OutsideRoot)
        {
            return SceneObjectAssetReadResult.OutsideRoot("Scene asset path escapes asset root.");
        }

        if (string.IsNullOrWhiteSpace(scene.AssetStoragePrefix) || _storage is null)
        {
            return SceneObjectAssetReadResult.NotFound();
        }

        return await OpenObjectOriginAssetAsync(scene, relativeAssetPath, range, cancellationToken)
            .ConfigureAwait(false);
    }

    private SceneObjectAssetReadResult OpenLocalAsset(
        string relativeAssetPath,
        ResolvedSceneAsset local,
        SceneByteRange? range)
    {
        var metadata = BuildLocalMetadata(relativeAssetPath, local);
        var budgetFailure = ValidateRangeAndBudget(metadata, range);
        if (budgetFailure is not null)
        {
            return budgetFailure;
        }

        FileStream? fileStream = null;
        try
        {
            fileStream = new FileStream(
                local.File.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileStreamBufferSize,
                FileOptions.Asynchronous | (range.HasValue ? FileOptions.RandomAccess : FileOptions.SequentialScan));

            if (range is { } slice)
            {
                var effectiveLength = Math.Min(slice.Length, metadata.ContentLength - slice.Start);
                fileStream.Seek(slice.Start, SeekOrigin.Begin);
                var owned = fileStream;
                fileStream = null;
                return CreateBoundedResult(metadata, owned, effectiveLength, slice.Start, slice.Start + effectiveLength - 1);
            }

            var fullStream = fileStream;
            fileStream = null;
            return SceneObjectAssetReadResult.Found(metadata, fullStream);
        }
        finally
        {
            fileStream?.Dispose();
        }
    }

    private async Task<SceneObjectAssetReadResult> OpenObjectOriginAssetAsync(
        SceneDataset scene,
        string relativeAssetPath,
        SceneByteRange? range,
        CancellationToken cancellationToken)
    {
        var objectKey = SceneAssetHydration.BuildObjectKey(scene.AssetStoragePrefix!, relativeAssetPath);
        var metadata = await TryGetObjectMetadataAsync(scene, relativeAssetPath, cancellationToken).ConfigureAwait(false);

        if (metadata is not null)
        {
            var budgetFailure = ValidateRangeAndBudget(metadata, range);
            if (budgetFailure is not null)
            {
                return budgetFailure;
            }
        }

        if (range is { } requestedRange)
        {
            return await OpenObjectRangeAsync(scene.Id, relativeAssetPath, objectKey, metadata, requestedRange, cancellationToken)
                .ConfigureAwait(false);
        }

        return await OpenObjectFullAsync(scene.Id, relativeAssetPath, objectKey, metadata, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SceneObjectAssetReadResult> OpenObjectRangeAsync(
        string sceneId,
        string relativeAssetPath,
        string objectKey,
        SceneObjectAssetMetadata? metadata,
        SceneByteRange range,
        CancellationToken cancellationToken)
    {
        var maxRange = Math.Max(1, _options.MaxRangeReadBytes);
        if (range.Length > maxRange)
        {
            return SceneObjectAssetReadResult.ByteBudgetExceeded(
                metadata,
                string.Create(CultureInfo.InvariantCulture,
                    $"Requested byte range ({range.Length} bytes) exceeds the {maxRange}-byte range ceiling."));
        }

        if (metadata is not null && TryResolveRangeReader(out var reader, out var bucket))
        {
            var effectiveLength = (int)Math.Min(range.Length, metadata.ContentLength - range.Start);
            var stream = await reader!
                .ReadRangeStreamAsync(bucket!, objectKey, range.Start, effectiveLength, cancellationToken)
                .ConfigureAwait(false);
            Log.StreamingObjectRange(_logger, sceneId, relativeAssetPath, range.Start, effectiveLength);
            return CreateBoundedResult(metadata, stream, effectiveLength, range.Start, range.Start + effectiveLength - 1);
        }

        var fullStream = await _storage!.DownloadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (fullStream is null)
        {
            return SceneObjectAssetReadResult.NotFound();
        }

        return await SliceDownloadedStreamAsync(sceneId, relativeAssetPath, metadata, fullStream, range, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SceneObjectAssetReadResult> SliceDownloadedStreamAsync(
        string sceneId,
        string relativeAssetPath,
        SceneObjectAssetMetadata? metadata,
        Stream stream,
        SceneByteRange range,
        CancellationToken cancellationToken)
    {
        var resolvedMeta = metadata ?? BuildFallbackStreamMetadata(relativeAssetPath, stream);
        if (resolvedMeta is not null)
        {
            var budgetFailure = ValidateRangeAndBudget(resolvedMeta, range);
            if (budgetFailure is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return budgetFailure;
            }
        }

        if (stream.CanSeek)
        {
            stream.Seek(range.Start, SeekOrigin.Begin);
        }
        else if (!await TrySkipBoundedAsync(stream, range.Start, cancellationToken).ConfigureAwait(false))
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return SceneObjectAssetReadResult.ByteBudgetExceeded(
                resolvedMeta,
                "Non-seekable object-origin stream offset exceeds the bounded skip budget.");
        }

        var totalLength = resolvedMeta?.ContentLength ?? (range.Start + range.Length);
        var effectiveLength = Math.Min(range.Length, totalLength - range.Start);
        var finalMeta = resolvedMeta ?? new SceneObjectAssetMetadata
        {
            RelativePath = relativeAssetPath,
            ContentType = SceneContentTypes.Resolve(relativeAssetPath),
            ContentLength = totalLength,
            ETag = $"\"{totalLength:X16}\"",
            LastModifiedUtc = DateTimeOffset.UtcNow,
            FromObjectStorage = true
        };

        Log.StreamingObjectRange(_logger, sceneId, relativeAssetPath, range.Start, effectiveLength);
        return CreateBoundedResult(finalMeta, stream, effectiveLength, range.Start, range.Start + effectiveLength - 1);
    }

    private async Task<SceneObjectAssetReadResult> OpenObjectFullAsync(
        string sceneId,
        string relativeAssetPath,
        string objectKey,
        SceneObjectAssetMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var stream = await _storage!.DownloadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return SceneObjectAssetReadResult.NotFound();
        }

        var resolvedMeta = metadata ?? BuildFallbackStreamMetadata(relativeAssetPath, stream);
        var maxAsset = Math.Max(1, _options.MaxSingleAssetBytes);
        if (resolvedMeta is not null && resolvedMeta.ContentLength > maxAsset)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return SceneObjectAssetReadResult.ByteBudgetExceeded(
                resolvedMeta,
                string.Create(CultureInfo.InvariantCulture,
                    $"Scene asset size ({resolvedMeta.ContentLength} bytes) exceeds the {maxAsset}-byte direct-stream budget."));
        }

        var finalMeta = resolvedMeta ?? new SceneObjectAssetMetadata
        {
            RelativePath = relativeAssetPath,
            ContentType = SceneContentTypes.Resolve(relativeAssetPath),
            ContentLength = maxAsset,
            ETag = $"\"obj-{relativeAssetPath.GetHashCode(StringComparison.Ordinal):X8}\"",
            LastModifiedUtc = DateTimeOffset.UtcNow,
            FromObjectStorage = true
        };

        Log.StreamingObjectAsset(_logger, sceneId, relativeAssetPath, finalMeta.ContentLength);
        return CreateBoundedResult(finalMeta, stream, finalMeta.ContentLength, rangeStart: null, rangeEnd: null);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Stream ownership transfers to the returned SceneObjectAssetReadResult, which disposes it.")]
    private static SceneObjectAssetReadResult CreateBoundedResult(
        SceneObjectAssetMetadata metadata,
        Stream inner,
        long length,
        long? rangeStart,
        long? rangeEnd)
        => SceneObjectAssetReadResult.Found(metadata, new BoundedSliceStream(inner, length), rangeStart, rangeEnd);

    private async Task<SceneObjectAssetMetadata?> TryGetObjectMetadataAsync(
        SceneDataset scene,
        string relativeAssetPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scene.AssetStoragePrefix) || _storage is null)
        {
            return null;
        }

        var objectKey = SceneAssetHydration.BuildObjectKey(scene.AssetStoragePrefix, relativeAssetPath);
        try
        {
            var cloudFile = await _storage.GetMetadataAsync(objectKey, cancellationToken).ConfigureAwait(false);
            return cloudFile is null ? null : BuildCloudMetadata(relativeAssetPath, cloudFile);
        }
        catch (NotSupportedException)
        {
            // Minimal storage implementations may omit GetMetadataAsync; fall back to range/stream metadata.
        }

        if (TryResolveRangeReader(out var reader, out var bucket))
        {
            try
            {
                var objectMeta = await reader!.GetObjectMetadataAsync(bucket!, objectKey, cancellationToken)
                    .ConfigureAwait(false);
                var etag = NormalizeETag(objectMeta.ETag, objectMeta.SizeBytes, DateTimeOffset.UtcNow);
                return new SceneObjectAssetMetadata
                {
                    RelativePath = relativeAssetPath,
                    ContentType = SceneContentTypes.Resolve(relativeAssetPath),
                    ContentLength = objectMeta.SizeBytes,
                    ETag = etag,
                    LastModifiedUtc = DateTimeOffset.UtcNow,
                    FromObjectStorage = true
                };
            }
            catch (NotSupportedException)
            {
                // Provider range reader does not expose object metadata; fall back to stream-derived length.
            }
        }

        return null;
    }

    private SceneObjectAssetReadResult? ValidateRangeAndBudget(
        SceneObjectAssetMetadata metadata,
        SceneByteRange? range)
    {
        if (range is { } slice)
        {
            if (metadata.ContentLength <= 0 || slice.Start >= metadata.ContentLength)
            {
                return SceneObjectAssetReadResult.RangeUnsatisfiable(metadata, "Requested byte range is outside asset bounds.");
            }

            var effectiveLength = Math.Min(slice.Length, metadata.ContentLength - slice.Start);
            var maxRange = Math.Max(1, _options.MaxRangeReadBytes);
            if (effectiveLength > maxRange)
            {
                return SceneObjectAssetReadResult.ByteBudgetExceeded(
                    metadata,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Requested byte range ({effectiveLength} bytes) exceeds the {maxRange}-byte range ceiling."));
            }

            return null;
        }

        var maxAsset = Math.Max(1, _options.MaxSingleAssetBytes);
        if (metadata.ContentLength > maxAsset)
        {
            return SceneObjectAssetReadResult.ByteBudgetExceeded(
                metadata,
                string.Create(CultureInfo.InvariantCulture,
                    $"Scene asset size ({metadata.ContentLength} bytes) exceeds the {maxAsset}-byte direct-stream budget."));
        }

        return null;
    }

    private async Task<bool> TrySkipBoundedAsync(Stream stream, long bytesToSkip, CancellationToken cancellationToken)
    {
        var maxSkip = Math.Max(1, _options.MaxRangeReadBytes);
        if (bytesToSkip > maxSkip)
        {
            return false;
        }

        var buffer = new byte[(int)Math.Min(bytesToSkip, FileStreamBufferSize)];
        var remaining = bytesToSkip;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            remaining -= read;
        }

        return true;
    }

    private bool TryResolveRangeReader(out ICloudRangeReader? reader, out string? bucket)
    {
        reader = null;
        bucket = null;
        if (_storage is null)
        {
            return false;
        }

        reader = _rangeReaders.FirstOrDefault(r => r.Provider == _storage.Provider);
        if (reader is null)
        {
            return false;
        }

        bucket = _storage.Provider switch
        {
            CloudStorageProvider.AwsS3 => _storageOptions?.AwsS3?.BucketName,
            CloudStorageProvider.AzureBlob => _storageOptions?.AzureBlob?.ContainerName,
            CloudStorageProvider.Local => "local",
            _ => null
        };
        return !string.IsNullOrWhiteSpace(bucket);
    }

    private static bool IsAllowedAssetPath(string relativeAssetPath)
    {
        if (!SceneAssetResolver.IsSafeRelativePath(relativeAssetPath))
        {
            return false;
        }

        return !string.Equals(relativeAssetPath, SceneAssetHydration.ManifestObjectName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(relativeAssetPath, SceneAssetHydration.MarkerFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static SceneObjectAssetMetadata BuildLocalMetadata(string relativeAssetPath, ResolvedSceneAsset local)
        => new()
        {
            RelativePath = relativeAssetPath,
            ContentType = local.ContentType,
            ContentLength = local.File.Length,
            ETag = $"\"{local.File.LastWriteTimeUtc.Ticks:X16}-{local.File.Length:X16}\"",
            LastModifiedUtc = local.File.LastWriteTimeUtc,
            FromObjectStorage = false
        };

    private static SceneObjectAssetMetadata BuildCloudMetadata(string relativeAssetPath, CloudFile cloudFile)
    {
        var lastModified = cloudFile.UploadedAt == default ? DateTimeOffset.UtcNow : cloudFile.UploadedAt;
        return new SceneObjectAssetMetadata
        {
            RelativePath = relativeAssetPath,
            ContentType = SceneContentTypes.Resolve(relativeAssetPath),
            ContentLength = cloudFile.SizeBytes,
            ETag = NormalizeETag(cloudFile.ETag, cloudFile.SizeBytes, lastModified),
            LastModifiedUtc = lastModified,
            FromObjectStorage = true
        };
    }

    private static SceneObjectAssetMetadata? BuildFallbackStreamMetadata(string relativeAssetPath, Stream stream)
    {
        if (!stream.CanSeek)
        {
            return null;
        }

        var length = stream.Length;
        var now = DateTimeOffset.UtcNow;
        return new SceneObjectAssetMetadata
        {
            RelativePath = relativeAssetPath,
            ContentType = SceneContentTypes.Resolve(relativeAssetPath),
            ContentLength = length,
            ETag = $"\"{length:X16}\"",
            LastModifiedUtc = now,
            FromObjectStorage = true
        };
    }

    private static string NormalizeETag(string? rawETag, long length, DateTimeOffset lastModified)
    {
        if (string.IsNullOrWhiteSpace(rawETag))
        {
            return $"\"{lastModified.Ticks:X16}-{length:X16}\"";
        }

        var trimmed = rawETag.Trim();
        return trimmed.StartsWith('"') && trimmed.EndsWith('"') ? trimmed : $"\"{trimmed}\"";
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 8464, Level = LogLevel.Debug,
            Message = "Streaming scene asset {SceneId}/{AssetPath} directly from object origin ({Bytes} bytes).")]
        public static partial void StreamingObjectAsset(ILogger logger, string sceneId, string assetPath, long bytes);

        [LoggerMessage(EventId = 8465, Level = LogLevel.Debug,
            Message = "Streaming scene asset range {SceneId}/{AssetPath} directly from object origin (offset={Offset}, length={Length}).")]
        public static partial void StreamingObjectRange(ILogger logger, string sceneId, string assetPath, long offset, long length);
    }
}
