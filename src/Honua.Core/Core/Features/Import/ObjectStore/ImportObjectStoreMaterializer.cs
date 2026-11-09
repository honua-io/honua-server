// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

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

    public static readonly TimeSpan StagedCopyLifetime = TimeSpan.FromDays(1);

    public static async Task<Result> MaterializeAsync(
        ImportRequest request,
        IImportObjectStoreReader reader,
        ICloudFileStorage? cloudStorage,
        ImportObjectStoreOptions options,
        CancellationToken cancellationToken,
        long? contentLength = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        if (!request.UsesObjectStore)
        {
            return new Result { Request = request };
        }

        if (string.IsNullOrWhiteSpace(request.ObjectStoreETag))
        {
            throw ImportObjectStoreException.Changed("The object-store import has no pinned ETag.");
        }

        if (!ImportObjectStorePolicy.TryAdmit(
                request.ObjectStoreUri,
                request.ObjectStoreRoleArn,
                options,
                out var location,
                out var problem))
        {
            throw ImportObjectStoreException.Forbidden(problem?.Detail ?? "The object-store source is not allow-listed.");
        }

        var stream = await reader.OpenReadAsync(location!, request.ObjectStoreETag, cancellationToken)
            .ConfigureAwait(false);
        if (!ImportObjectStoreFormats.NeedsRandomAccess(request.FileName))
        {
            return new Result
            {
                Request = ClearObjectStore(request) with { FileStream = stream },
                DisposeStream = true,
            };
        }

        string? tempPath = null;
        try
        {
            if (cloudStorage is not null)
            {
                var uploaded = await cloudStorage.UploadAsync(new FileUploadRequest
                {
                    Content = stream,
                    FileName = request.FileName,
                    ContentType = string.IsNullOrWhiteSpace(request.ObjectStoreContentType)
                        ? "application/octet-stream"
                        : request.ObjectStoreContentType,
                    SizeBytes = contentLength ?? (stream.CanSeek ? stream.Length : null),
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

            var extension = Path.GetExtension(request.FileName);
            tempPath = Path.Join(Path.GetTempPath(), $"honua-import-object-{Guid.NewGuid():N}{extension}");
            await using (var file = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            }))
            {
                await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var stagedPath = tempPath;
            tempPath = null;
            return new Result
            {
                Request = ClearObjectStore(request) with { LocalFilePath = stagedPath },
                DeleteLocalFile = true,
            };
        }
        catch
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                {
                    // The caller still observes the original failure. A leftover temp file is scratch.
                }
            }

            throw;
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ImportRequest ClearObjectStore(ImportRequest request)
        => request with
        {
            ObjectStoreUri = null,
            ObjectStoreETag = null,
            ObjectStoreRoleArn = null,
            ObjectStoreContentType = null,
        };
}
