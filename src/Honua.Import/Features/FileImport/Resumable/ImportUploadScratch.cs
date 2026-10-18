// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Import.FileImport.Resumable;

/// <summary>
/// Materializes a sealed transfer into private, seekable worker scratch. The caller
/// owns the returned stream and must dispose it after import. This is not durable
/// job admission: shared source retention and aggregate tenant quotas are separate.
/// </summary>
internal sealed class ImportUploadScratch(RedisImportUploadStore store, ImportUploadChunkStorage storage)
{
    public async Task<FileStream> OpenAsync(ImportUploadSession session, string scratchDirectory,
        long maximumScratchBytes, Func<CancellationToken, Task> reauthorize,
        CancellationToken cancellationToken = default)
    {
        RedisImportUploadStore.Validate(session);
        ArgumentNullException.ThrowIfNull(reauthorize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumScratchBytes, 1);
        if (session.State != ImportUploadState.Sealed || session.AcceptedBytes != session.TotalBytes ||
            session.TotalBytes > maximumScratchBytes)
        {
            throw new InvalidOperationException("The sealed source does not fit the worker scratch allowance.");
        }

        RedisImportUploadStore.ValidateDigest(session.ContentSha256!);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        if (!Path.IsPathFullyQualified(scratchDirectory))
        {
            throw new ArgumentException("Worker scratch requires an absolute configured directory.", nameof(scratchDirectory));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await reauthorize(cancellationToken).ConfigureAwait(false);
        if (await store.GetAsync(session.Id, session.Scope, cancellationToken).ConfigureAwait(false) != session ||
            session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The sealed upload source is unavailable.");
        }

        Directory.CreateDirectory(scratchDirectory);
        // One open handle survives verification through import; no path reopen can
        // substitute a different source. The OS preallocation is requested before
        // reading chunks, but is not a portable aggregate quota guarantee.
        var file = new FileStream(Path.Join(scratchDirectory, $"honua-upload-{Guid.NewGuid():N}.tmp"), new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            PreallocationSize = session.TotalBytes
        });
        try
        {
            var verifier = new ImportUploadContentVerifier(store, storage);
            _ = await verifier.CopySealedAsync(session, file, reauthorize, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            await reauthorize(cancellationToken).ConfigureAwait(false);
            if (await store.GetAsync(session.Id, session.Scope, cancellationToken).ConfigureAwait(false) != session ||
                session.ExpiresAt <= DateTimeOffset.UtcNow || file.Length != session.TotalBytes)
            {
                throw new InvalidDataException("The sealed source changed before scratch became available.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
