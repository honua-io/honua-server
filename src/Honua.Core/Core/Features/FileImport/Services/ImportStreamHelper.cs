// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Security;
namespace Honua.Core.Features.FileImport.Services;

internal static class ImportStreamHelper
{
    private const int StagingBufferSize = 64 * 1024;

    // Chooses what the background job reads: a staged local file as is, object-store and cloud
    // sources unchanged, or an inline upload spilled to a new owner-only temp file (the returned
    // path, which the job deletes). A failed spill deletes its file before rethrowing.
    internal static async Task<(string? TempFilePath, ImportRequest Request)> PrepareBackgroundSourceAsync(
        ImportRequest request, string jobId, CancellationToken cancellationToken)
    {
        if (request.UsesLocalFile)
        {
            return (request.LocalFilePath, request);
        }

        if (request.UsesObjectStore || request.UsesCloudStorage)
        {
            return (null, request);
        }

        if (request.FileStream!.CanSeek)
        {
            request.FileStream.Position = 0;
        }

        var tempFilePath = PrivateTempFile.NewPath($"honua-import-{jobId}-", ".tmp");
        try
        {
            var backgroundStream = await StageToPrivateFileAsync(request.FileStream, tempFilePath, cancellationToken);
            return (tempFilePath, request with { FileStream = backgroundStream, CloudFileId = null });
        }
        catch
        {
            TryDelete(tempFilePath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: the original failure is rethrown by the caller.
        }
    }

    // Copies an upload to a new owner-only file at path (created exclusively), then reopens it
    // for the background job to read.
    internal static async Task<FileStream> StageToPrivateFileAsync(Stream source, string path, CancellationToken cancellationToken)
    {
        await using (var target = PrivateTempFile.Create(path, FileOptions.Asynchronous | FileOptions.SequentialScan, StagingBufferSize))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = StagingBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
    }

    internal static async Task<FileStream> SpillToSeekableTempAsync(Stream source, CancellationToken cancellationToken)
    {
        // The name is a fixed prefix plus a fresh GUID, created exclusively and owner-only.
        var tempFile = new FileStream(
            PrivateTempFile.NewPath("honua-import-", ".tmp"),
            PrivateTempFile.ApplyPrivateCreateNew(new FileStreamOptions
            {
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
            }));
        try
        {
            await source.CopyToAsync(tempFile, cancellationToken);
        }
        catch
        {
            await tempFile.DisposeAsync();
            throw;
        }

        tempFile.Position = 0;
        return tempFile;
    }
}
