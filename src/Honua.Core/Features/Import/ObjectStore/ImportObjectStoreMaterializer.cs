// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Import.ObjectStore;

/// <summary>
/// Opens an object-store import at execution time. Streamable objects stay on the returned
/// stream. Formats that need random access are staged on the worker (shared file storage when
/// it is configured, otherwise a file in the worker temp directory), never on the API host
/// that accepted the job.
/// </summary>
public static class ImportObjectStoreMaterializer
{
    /// <summary>The request produced for the streaming importer and how the caller cleans it up.</summary>
    public sealed record Result
    {
        /// <summary>Request with exactly one of file stream, cloud file, or local path set.</summary>
        public required ImportRequest Request { get; init; }

        /// <summary>The caller disposes <see cref="ImportRequest.FileStream"/> after the import.</summary>
        public bool DisposeStream { get; init; }

        /// <summary>The caller deletes <see cref="ImportRequest.LocalFilePath"/> after the import.</summary>
        public bool DeleteLocalFile { get; init; }
    }

    /// <summary>
    /// Reads <paramref name="request"/> with the pinned ETag and returns a request the
    /// streaming importer can consume.
    /// </summary>
    public static async Task<Result> MaterializeAsync(
        ImportRequest request,
        IImportObjectStoreReader reader,
        ICloudFileStorage? cloudStorage,
        ImportObjectStoreOptions options,
        CancellationToken cancellationToken)
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
                    Folder = "imports",
                }, cancellationToken).ConfigureAwait(false);
                if (!uploaded.Success || string.IsNullOrWhiteSpace(uploaded.File?.FileId))
                {
                    throw new InvalidOperationException("Failed to stage the object-store import on the worker.");
                }

                return new Result
                {
                    Request = ClearObjectStore(request) with { CloudFileId = uploaded.File.FileId },
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
