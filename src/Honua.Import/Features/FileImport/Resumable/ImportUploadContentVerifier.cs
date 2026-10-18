// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;

namespace Honua.Import.FileImport.Resumable;

/// <summary>
/// Computes the complete-file digest from the persisted, ordered chunk generations.
/// This read-only format operation neither seals the session nor accepts an import.
/// The coordinator must check current authority before verification and again before
/// committing the seal against the returned unchanged session version.
/// </summary>
internal sealed class ImportUploadContentVerifier(RedisImportUploadStore store, ImportUploadChunkStorage storage)
{
    private const int ManifestPageSize = 128;

    public Task<ImportUploadVerifiedContent> VerifyAsync(ImportUploadSession session,
        string expectedSha256, CancellationToken cancellationToken = default)
        => VerifyAsync(session, expectedSha256, static _ => Task.CompletedTask, cancellationToken);

    public async Task<ImportUploadVerifiedContent> VerifyAsync(ImportUploadSession session,
        string expectedSha256, Func<CancellationToken, Task> reauthorize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reauthorize);
        RedisImportUploadStore.Validate(session);
        RedisImportUploadStore.ValidateDigest(expectedSha256);
        if (session.State != ImportUploadState.Receiving || session.AcceptedBytes != session.TotalBytes)
        {
            throw new InvalidOperationException("Upload content is not complete and awaiting verification.");
        }

        await RequireCurrentAsync(session, reauthorize, cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            for (var first = 0; first < session.ChunkCount; first += ManifestPageSize)
            {
                var count = Math.Min(ManifestPageSize, session.ChunkCount - first);
                var chunks = await store.ReadChunksAsync(session.Id, session.Scope, first, count, cancellationToken)
                    .ConfigureAwait(false);
                if (chunks.Count != count)
                {
                    throw new InvalidDataException("The complete upload manifest is unavailable.");
                }

                foreach (var chunk in chunks)
                {
                    await RequireCurrentAsync(session, reauthorize, cancellationToken).ConfigureAwait(false);
                    if (chunk.Offset != total)
                    {
                        throw new InvalidDataException("Upload chunk order does not match the complete file.");
                    }

                    await using var content = await storage.OpenVerifiedAsync(chunk, cancellationToken).ConfigureAwait(false);
                    int read;
                    while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        hash.AppendData(buffer.AsSpan(0, read));
                        total = checked(total + read);
                    }

                    // Cancellation/expiry during a provider read must not produce a
                    // verification receipt, including when this was the final chunk.
                    await RequireCurrentAsync(session, reauthorize, cancellationToken).ConfigureAwait(false);
                }
            }

            var actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (total != session.TotalBytes || !string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The complete upload length or SHA-256 does not match its intent.");
            }

            await RequireCurrentAsync(session, reauthorize, cancellationToken).ConfigureAwait(false);
            return new ImportUploadVerifiedContent(session, actualSha256);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async Task RequireCurrentAsync(ImportUploadSession session, Func<CancellationToken, Task> reauthorize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await reauthorize(cancellationToken).ConfigureAwait(false);
        var current = await store.GetAsync(session.Id, session.Scope, cancellationToken).ConfigureAwait(false);
        if (current is null || current != session || current.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The upload session changed or expired during content verification.");
        }
    }
}

// A successful read of every accepted byte, not an authorization or job receipt.
internal sealed record ImportUploadVerifiedContent(ImportUploadSession Session, string Sha256);
