// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Import.FileImport.Resumable;

/// <summary>
/// Copies verified upload content to a unique shared source that survives the transfer
/// session's admission window. The coordinator must reauthorize and bind exactly one
/// candidate before durable job acceptance. No upload or job capability is advertised here.
/// </summary>
internal sealed class ImportUploadSourceStorage(RedisImportUploadStore store, ICloudFileStorage storage)
{
    // This is bounded source retention, not a promise of indefinite queue/retry life.
    // Source-aware queue deadlines and aggregate reservations remain admission prerequisites.
    internal static readonly TimeSpan SourceRetention = TimeSpan.FromDays(7);

    public async Task<ImportStagedSourceBinding> StageAsync(ImportUploadSession session, string scratchDirectory,
        long maximumScratchBytes, Func<CancellationToken, Task> reauthorize, CancellationToken cancellationToken = default)
    {
        if (session.StagedSource is not null)
        {
            throw new InvalidOperationException("The upload already has a retained source. Read its original receipt.");
        }

        var scratch = new ImportUploadScratch(store, new ImportUploadChunkStorage(storage));
        await using var content = await scratch.OpenAsync(session, scratchDirectory, maximumScratchBytes,
            reauthorize, cancellationToken).ConfigureAwait(false);
        await reauthorize(cancellationToken).ConfigureAwait(false);
        var key = $"imports/resumable/{session.Id}/source-{Guid.NewGuid():N}";
        var minimumRetention = DateTimeOffset.UtcNow + SourceRetention;
        var result = await storage.UploadAsync(new FileUploadRequest
        {
            Content = content,
            FileName = session.FileName,
            ContentType = "application/octet-stream",
            SizeBytes = session.TotalBytes,
            ObjectKeyOverride = key,
            TimeToLive = SourceRetention,
            Metadata = ImmutableDictionary<string, string>.Empty
                .Add("uploadSessionId", session.Id)
                .Add("sha256", session.ContentSha256!)
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.File is not { } file || file.FileId != key || file.SizeBytes != session.TotalBytes ||
            string.IsNullOrWhiteSpace(file.ETag) || file.ExpiresAt is null ||
            file.ExpiresAt < minimumRetention)
        {
            // Unknown or malformed provider success never becomes an accepted source.
            // Unique candidates have bounded retention; don't delete an unproven key.
            throw new IOException("Storage did not confirm the retained source generation and lifetime.");
        }

        await reauthorize(cancellationToken).ConfigureAwait(false);
        if (await store.GetAsync(session.Id, session.Scope, cancellationToken).ConfigureAwait(false) != session ||
            session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The original upload changed before its source could be retained.");
        }

        return new ImportStagedSourceBinding(file.FileId, file.ETag, file.SizeBytes, session.ContentSha256!, file.ExpiresAt);
    }
}
