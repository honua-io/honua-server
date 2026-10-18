// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Import.FileImport.Durable;

/// <summary>
/// Verifies a durable source into a single private seekable handle before the import
/// pipeline can observe bytes. This format operation does not authorize the job:
/// the worker must check current execution authority before and after materialization.
/// </summary>
internal static class StagedImportSourceMaterializer
{
    internal static async Task<FileStream> OpenAsync(ImportStagedSourceBinding source, ICloudFileStorage storage,
        long maximumScratchBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(storage);
        source.Validate();
        if (source.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The accepted staged source retention window expired.");
        }
        if (maximumScratchBytes <= 0 || source.Bytes > maximumScratchBytes)
        {
            throw new InvalidOperationException("The staged source exceeds the configured worker scratch allowance.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        RequireGeneration(await storage.GetMetadataAsync(source.FileId, cancellationToken).ConfigureAwait(false), source);
        var file = new FileStream(Path.Join(Path.GetTempPath(), $"honua-staged-import-{Guid.NewGuid():N}.tmp"), new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            PreallocationSize = source.Bytes
        });
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            await using var input = await storage.DownloadAsync(source.FileId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The accepted staged source is unavailable.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            while (true)
            {
                // Consume at most the declared source plus one probe byte, even if
                // a provider returns an endless stream or inaccurate metadata.
                var remaining = source.Bytes - total;
                var requested = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
                var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (read > remaining) throw new InvalidDataException("The accepted staged source length changed.");
                hash.AppendData(buffer.AsSpan(0, read));
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }

            if (total != source.Bytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != source.Sha256)
            {
                throw new InvalidDataException("The accepted staged source bytes do not match their receipt.");
            }

            RequireGeneration(await storage.GetMetadataAsync(source.FileId, cancellationToken).ConfigureAwait(false), source);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void RequireGeneration(CloudFile? file, ImportStagedSourceBinding source)
    {
        if (file is null || file.FileId != source.FileId || file.ETag != source.ETag || file.SizeBytes != source.Bytes ||
            file.ExpiresAt != source.ExpiresAt || source.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The accepted staged source generation is unavailable.");
        }
    }
}
