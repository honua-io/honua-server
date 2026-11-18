// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.ObjectStorage;
using Honua.Core.Features.Security;

namespace Honua.Core.Features.Import.ObjectStore;

// Formats that need random access are staged on the worker, never on the API host that accepted the job.
public static class ImportObjectStoreMaterializer
{
    public sealed record Result
    {
        public required ImportRequest Request { get; init; }

        public bool DisposeStream { get; init; }

        public bool DeleteLocalFile { get; init; }

        public bool DeleteCloudFile { get; init; }
    }

    public sealed record Source
    {
        public required ImportRequest Request { get; init; }

        public required IImportObjectStoreReader Reader { get; init; }

        public required IObjectStorageConnectionResolver Connections { get; init; }

        public ICloudFileStorage? CloudStorage { get; init; }

        public long? ContentLength { get; init; }
    }

    public static readonly TimeSpan StagedCopyLifetime = TimeSpan.FromDays(1);

    public static async Task<Result> MaterializeAsync(Source source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var request = source.Request;
        if (!request.UsesObjectStore)
        {
            return new Result { Request = request };
        }

        if (string.IsNullOrWhiteSpace(request.ObjectStoreETag))
        {
            throw ImportObjectStoreException.Changed("The object-store import has no pinned ETag.");
        }

        var location = await ReresolveAsync(request, source.Connections, cancellationToken).ConfigureAwait(false);
        var stream = await source.Reader.OpenReadAsync(location, request.ObjectStoreETag, cancellationToken)
            .ConfigureAwait(false);
        if (!ImportObjectStoreFormats.NeedsRandomAccess(request.FileName))
        {
            return new Result
            {
                Request = ClearObjectStore(request) with { FileStream = stream },
                DisposeStream = true,
            };
        }

        return await StageAsync(source, stream, cancellationToken).ConfigureAwait(false);
    }

    // The job re-reads the connection and refuses a deleted connection or a key outside its current prefix.
    public static async Task<ImportObjectLocation> ReresolveAsync(
        ImportRequest request,
        IObjectStorageConnectionResolver connections,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(connections);
        if (!Guid.TryParse(request.ObjectStoreConnectionId, out var connectionId)
            || SplitSourceUri(request.ObjectStoreUri) is not { } source)
        {
            throw ImportObjectStoreException.Forbidden("The object-store import does not name a connection.");
        }

        var resolution = await connections.ResolveObjectAsync(connectionId, source.Key, cancellationToken).ConfigureAwait(false);
        if (resolution.Failure == ObjectStorageResolveFailure.ConnectionNotFound)
        {
            throw ImportObjectStoreException.Forbidden("The connection for this import was deleted.");
        }

        if (!resolution.Succeeded || !string.Equals(resolution.Connection!.Bucket, source.Bucket, StringComparison.Ordinal))
        {
            throw ImportObjectStoreException.Forbidden("The object is no longer under the connection prefix.");
        }

        return ImportObjectLocation.From(resolution);
    }

    private static (string Bucket, string Key)? SplitSourceUri(string? sourceUri)
    {
        if (sourceUri is null || !sourceUri.StartsWith("s3://", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = sourceUri["s3://".Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 || slash == rest.Length - 1 ? null : (rest[..slash], rest[(slash + 1)..]);
    }

    private static async Task<Result> StageAsync(Source source, Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return source.CloudStorage is null
                ? await StageLocallyAsync(source.Request, stream, cancellationToken).ConfigureAwait(false)
                : await StageInCloudAsync(source, stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Result> StageInCloudAsync(Source source, Stream stream, CancellationToken cancellationToken)
    {
        var request = source.Request;
        var uploaded = await source.CloudStorage!.UploadAsync(new FileUploadRequest
        {
            Content = stream,
            FileName = request.FileName,
            ContentType = string.IsNullOrWhiteSpace(request.ObjectStoreContentType)
                ? "application/octet-stream"
                : request.ObjectStoreContentType,
            SizeBytes = source.ContentLength ?? (stream.CanSeek ? stream.Length : null),
            TimeToLive = StagedCopyLifetime,
            Folder = "imports",
        }, cancellationToken).ConfigureAwait(false);
        if (!uploaded.Success || string.IsNullOrWhiteSpace(uploaded.File?.FileId))
        {
            throw new InvalidOperationException("Failed to stage the object-store import on the worker.");
        }

        return new Result
        {
            Request = ClearObjectStore(request) with { CloudFileId = uploaded.File.FileId },
            DeleteCloudFile = true,
        };
    }

    private static async Task<Result> StageLocallyAsync(ImportRequest request, Stream stream, CancellationToken cancellationToken)
    {
        var tempPath = PrivateTempFile.NewPath("honua-import-object-", Path.GetExtension(request.FileName));
        try
        {
            await using var file = PrivateTempFile.Create(tempPath, FileOptions.Asynchronous, bufferSize: 4096);
            await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return new Result
        {
            Request = ClearObjectStore(request) with { LocalFilePath = tempPath },
            DeleteLocalFile = true,
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
        {
            // The caller still observes the original failure. A leftover temp file is scratch.
        }
    }

    private static ImportRequest ClearObjectStore(ImportRequest request)
        => request with
        {
            ObjectStoreUri = null,
            ObjectStoreETag = null,
            ObjectStoreConnectionId = null,
            ObjectStoreContentType = null,
        };
}
