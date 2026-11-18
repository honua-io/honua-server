// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Exports;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

namespace Honua.Server.Features.Admin.Exports;

// Versions are written once under their own keys; only the pointer changes, and only when it still
// names the version this run started from.
internal sealed class ExportStore
{
    internal const string PointerFileName = "current.json";
    private const string JsonContentType = "application/json";

    private readonly ICloudFileStorage _storage;
    private readonly string _publicationKey;

    private ExportStore(ICloudFileStorage storage, string publicationKey)
    {
        _storage = storage;
        _publicationKey = publicationKey;
    }

    public string PointerKey => $"{_publicationKey}/{PointerFileName}";

    public static ExportStore Create(ICloudFileStorage storage, CloudStorageOptions options, string publicationId)
    {
        var root = PMTilesPublishKeyRoot.Resolve(options);
        var publicationKey = string.IsNullOrEmpty(root) ? $"publications/{publicationId}" : $"{root}/publications/{publicationId}";
        return new ExportStore(storage, publicationKey);
    }

    public string VersionKey(string version, string fileName) => $"{_publicationKey}/{version}/{fileName}";

    // A create-only marker fences overlapping attempts before any artifact upload. It also
    // survives a lost job receipt; retries recover its version instead of recapturing data.
    public async Task<ExportPointer?> BeginOperationAsync(string operationId, ExportVersionRun run, CancellationToken cancellationToken)
    {
        var operationKey = $"{_publicationKey}/operations/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(operationId)))}.json";
        var pointer = new ExportPointer(run.PublicationId, run.Version,
            $"{run.Version}/{ExportCollectionDocument.FileName}", DateTimeOffset.UtcNow);
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(pointer, ExportVersionJsonContext.Default.ExportPointer));
        UploadResult result;
        try
        {
            result = await _storage.UploadIfMatchAsync(new FileUploadRequest
            {
                Content = content,
                FileName = Path.GetFileName(operationKey),
                ContentType = JsonContentType,
                CachePolicy = StoredObjectCachePolicy.NotShared,
                SizeBytes = content.Length,
                TimeToLive = null,
                ObjectKeyOverride = operationKey,
                Metadata = ImmutableDictionary<string, string>.Empty
            }, expectedETag: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw new ExportActivationUnknownException("The publication operation could not be reserved; no artifacts were uploaded.");
        }

        if (result.Success)
        {
            return null;
        }

        return await ReadJsonAsync(operationKey, ExportVersionJsonContext.Default.ExportPointer, cancellationToken).ConfigureAwait(false)
            ?? throw new ExportActivationUnknownException("The publication operation reservation is unknown; no artifacts were uploaded.");
    }

    // Null when no version has been switched in yet.
    public async Task<string?> ReadPointerETagAsync(CancellationToken cancellationToken)
    {
        var pointer = await _storage.GetMetadataAsync(PointerKey, cancellationToken).ConfigureAwait(false);
        if (pointer is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(pointer.ETag)
            ? throw new ExportException("The storage provider reports no version tag for the current publication pointer.")
            : pointer.ETag;
    }

    public async Task<ExportPointer?> ReadPointerAsync(CancellationToken cancellationToken)
        => await ReadJsonAsync(PointerKey, ExportVersionJsonContext.Default.ExportPointer, cancellationToken).ConfigureAwait(false);

    public Task<ExportVersion?> ReadCollectionAsync(ExportPointer pointer, CancellationToken cancellationToken)
        => ExportReader.IsVersion(pointer.Version)
            ? ReadCollectionAtAsync(VersionKey(pointer.Version, ExportCollectionDocument.FileName), cancellationToken)
            : Task.FromResult<ExportVersion?>(null);

    public Task<ExportVersion?> ReadVersionAsync(string version, CancellationToken cancellationToken)
        => ReadCollectionAtAsync(VersionKey(version, ExportCollectionDocument.FileName), cancellationToken);

    private async Task<ExportVersion?> ReadCollectionAtAsync(string key, CancellationToken cancellationToken)
    {
        var collection = await ReadJsonAsync(key, ExportVersionJsonContext.Default.JsonObject, cancellationToken).ConfigureAwait(false);
        return collection is null ? null : ExportCollectionDocument.Read(collection);
    }

    public async Task<PointerState> PointerStateAsync(string version)
    {
        try
        {
            var pointer = await ReadPointerAsync(CancellationToken.None).ConfigureAwait(false);
            return pointer?.Version == version ? PointerState.NamesVersion : PointerState.NamesOther;
        }
        catch (Exception)
        {
            // Any provider failure leaves the pointer unknown; callers must neither claim nor delete the version.
            return PointerState.Unknown;
        }
    }

    private async Task<T?> ReadJsonAsync<T>(string key, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        var stream = await _storage.DownloadAsync(key, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            const int maxJsonBytes = 1024 * 1024;
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bounded.Length + read > maxJsonBytes)
                {
                    throw new JsonException("Publication JSON exceeds its size limit.");
                }
                bounded.Write(buffer, 0, read);
            }
            return JsonSerializer.Deserialize(bounded.GetBuffer().AsSpan(0, checked((int)bounded.Length)), typeInfo);
        }
    }

    // Version keys are unique per run, so a plain (multipart-capable) upload cannot overwrite another version.
    public async Task PutOnceAsync(string key, Stream content, string contentType, ImmutableDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var result = await _storage.UploadAsync(new FileUploadRequest
        {
            Content = content,
            FileName = Path.GetFileName(key),
            ContentType = contentType,
            CachePolicy = StoredObjectCachePolicy.NotShared,
            SizeBytes = content.CanSeek ? content.Length : null,
            TimeToLive = null,
            ObjectKeyOverride = key,
            Metadata = metadata
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new ExportException($"Writing publication object '{Path.GetFileName(key)}' failed.");
        }
    }

    public async Task PutJsonOnceAsync<T>(string key, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
        await PutOnceAsync(key, content, JsonContentType, ImmutableDictionary<string, string>.Empty, cancellationToken).ConfigureAwait(false);
    }

    // Hashes what storage returns, so the descriptor's checksum describes the stored bytes.
    public async Task<(long Bytes, string Sha256)?> ReadBackAsync(string key, CancellationToken cancellationToken)
    {
        var stream = await _storage.DownloadAsync(key, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long bytes = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                bytes += read;
            }

            return (bytes, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
    }

    public async Task<bool> SwitchPointerAsync(ExportPointer pointer, string? expectedETag, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(
            pointer, ExportVersionJsonContext.Default.ExportPointer));
        var result = await _storage.UploadIfMatchAsync(new FileUploadRequest
        {
            Content = content,
            FileName = PointerFileName,
            ContentType = JsonContentType,
            CachePolicy = StoredObjectCachePolicy.NotShared,
            SizeBytes = content.Length,
            TimeToLive = null,
            ObjectKeyOverride = PointerKey,
            Metadata = ImmutableDictionary<string, string>.Empty
        }, expectedETag, cancellationToken).ConfigureAwait(false);
        return result.Success;
    }
}

internal enum PointerState
{
    NamesVersion,
    NamesOther,
    Unknown
}
