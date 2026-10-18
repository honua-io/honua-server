// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Import.FileImport.Resumable;

/// <summary>
/// Stores and verifies bounded chunks through the shared local/S3/Azure storage contract.
/// This is format I/O, not an authorization or import-acceptance boundary: the coordinator
/// must revalidate current authority before staging and before appending a durable receipt.
/// </summary>
internal sealed class ImportUploadChunkStorage(ICloudFileStorage storage, IImportStorageBudget? budget = null)
{
    public async Task<ImportUploadChunk> StageAsync(ImportUploadSession session, Stream source,
        string sha256, CancellationToken cancellationToken = default)
    {
        if (budget is null) throw new InvalidOperationException("Import storage admission is required before staging bytes.");
        RedisImportUploadStore.Validate(session);
        if (session.State != ImportUploadState.Receiving || session.ExpiresAt <= DateTimeOffset.UtcNow ||
            session.AcceptedBytes == session.TotalBytes)
        {
            throw new InvalidOperationException("Upload session is not accepting content.");
        }

        var bytes = (int)Math.Min(session.ChunkBytes, session.TotalBytes - session.AcceptedBytes);
        await using var content = await ReadVerifiedAsync(source, bytes, sha256, cancellationToken).ConfigureAwait(false);
        // Only server-generated keys are used. Concurrent writers stage independent
        // objects; the receipt store chooses one without overwriting the other's bytes.
        var objectKey = $"imports/resumable/{session.Id}/{Guid.NewGuid():N}.part";
        var reservation = RedisImportStorageBudget.ForObject(session, objectKey, bytes);
        if (!await budget.TryReserveAsync(reservation, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The import storage budget is exhausted.");
        // From this point, failed/unknown provider writes remain charged. A client
        // retry gets a different object identity and cannot hide the earlier usage.
        cancellationToken.ThrowIfCancellationRequested();
        var result = await storage.UploadAsync(new FileUploadRequest
        {
            Content = content,
            FileName = "chunk.part",
            ContentType = "application/octet-stream",
            SizeBytes = bytes,
            ObjectKeyOverride = objectKey,
            // Chunks outlive the transfer receipt so an in-flight verifier does not
            // lose its bytes to ordinary expiry. They are not permanent job outputs.
            TimeToLive = session.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromDays(1),
            Metadata = ImmutableDictionary<string, string>.Empty
                .Add("uploadSessionId", session.Id)
                .Add("chunkIndex", session.ChunkCount.ToString(CultureInfo.InvariantCulture))
                .Add("sha256", sha256)
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Success || result.File is not { } file || file.FileId != objectKey ||
            file.SizeBytes != bytes || string.IsNullOrWhiteSpace(file.ETag))
        {
            // No receipt is returned on an uncertain or incomplete provider write.
            // Unique candidate objects expire; do not delete an unrelated key from
            // a malformed provider response or guess that a failed write committed.
            throw new IOException("Storage did not confirm the complete chunk generation.");
        }

        return new ImportUploadChunk(session.ChunkCount, session.AcceptedBytes, bytes, sha256, file.FileId, file.ETag);
    }

    /// <summary>
    /// Returns at most one chunk in memory, verified against its retained receipt.
    /// The caller streams/disposes chunks sequentially and rechecks authority between them.
    /// A complete-file verifier must compute its own SHA across their actual bytes.
    /// </summary>
    public async Task<Stream> OpenVerifiedAsync(ImportUploadChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ValidateChunkSize(chunk.Bytes);
        RedisImportUploadStore.ValidateDigest(chunk.Sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk.FileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk.ETag);
        var before = await storage.GetMetadataAsync(chunk.FileId, cancellationToken).ConfigureAwait(false);
        RequireGeneration(before, chunk);
        await using var source = await storage.DownloadAsync(chunk.FileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("An accepted upload chunk is unavailable.");
        var content = await ReadVerifiedAsync(source, chunk.Bytes, chunk.Sha256, cancellationToken).ConfigureAwait(false);
        try
        {
            // The current interface has no conditional-read primitive. Hash the
            // actual bounded bytes and check generation again before exposing them.
            var after = await storage.GetMetadataAsync(chunk.FileId, cancellationToken).ConfigureAwait(false);
            RequireGeneration(after, chunk);
            cancellationToken.ThrowIfCancellationRequested();
            return content;
        }
        catch
        {
            await content.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Removes only this candidate generation after the coordinator has proved it
    /// was not accepted. Never call after an ambiguous receipt commit without recovery.
    /// </summary>
    public async Task<bool> DeleteUnacceptedAsync(ImportUploadSession session, ImportUploadChunk candidate,
        CancellationToken cancellationToken = default)
    {
        if (budget is null) throw new InvalidOperationException("Import storage accounting is required for cleanup.");
        var reservation = RedisImportStorageBudget.ForObject(session, candidate.FileId, candidate.Bytes);
        if (!await storage.DeleteIfMatchAsync(candidate.FileId, candidate.ETag, cancellationToken).ConfigureAwait(false))
            return false;
        // A lost deletion response or accounting reply keeps a conservative charge
        // until durable cleanup reconciliation proves what happened.
        _ = await budget.ReleaseDeletedAsync(reservation, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void RequireGeneration(CloudFile? file, ImportUploadChunk chunk)
    {
        if (file is null || file.FileId != chunk.FileId || file.SizeBytes != chunk.Bytes ||
            !string.Equals(file.ETag, chunk.ETag, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The accepted upload chunk generation is unavailable.");
        }
    }

    private static async Task<MemoryStream> ReadVerifiedAsync(Stream source, int bytes, string sha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateChunkSize(bytes);
        RedisImportUploadStore.ValidateDigest(sha256);
        cancellationToken.ThrowIfCancellationRequested();
        var content = new MemoryStream(bytes);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var total = 0;
            while (true)
            {
                // Probe one byte beyond the declared range without consuming an
                // arbitrarily large body or trusting Content-Length/Stream.Length.
                var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, bytes - total + 1)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (read > bytes - total)
                {
                    throw new InvalidDataException("Upload chunk exceeds its declared range.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                content.Write(buffer, 0, read);
                total += read;
            }

            if (total != bytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Upload chunk length or SHA-256 does not match its receipt.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var verified = new MemoryStream(content.GetBuffer(), 0, total, writable: false, publiclyVisible: false);
            await content.DisposeAsync().ConfigureAwait(false);
            return verified;
        }
        catch
        {
            await content.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ValidateChunkSize(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, RedisImportUploadStore.MaximumChunkBytes);
    }
}
