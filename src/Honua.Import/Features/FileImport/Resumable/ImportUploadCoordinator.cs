// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Import.FileImport.Resumable;

/// <summary>
/// Canonical authenticated transfer operations over an immutable dataset-import task.
/// Neither session creation nor a verified seal accepts an import job. Quota admission,
/// finalization and HTTP/SDK registration are separate prerequisites before advertising it.
/// </summary>
internal sealed class ImportUploadCoordinator(
    ImportUploadTaskAuthority authority, RedisImportUploadStore store, ICloudFileStorage files, IImportStorageBudget budget,
    ImportScratchStorage? scratchStorage = null)
{
    private readonly ImportUploadChunkStorage _storage = new(files, budget);
    private readonly ImportUploadSourceStorage _sources = new(store, files, budget, scratchStorage);

    public async Task<ImportUploadSession?> GetAsync(ClaimsPrincipal principal, ImportUploadTaskReference reference,
        CancellationToken cancellationToken = default)
    {
        var task = await AuthorizeAsync(principal, reference, requireWrite: false, cancellationToken).ConfigureAwait(false);
        var expected = SessionFor(task);
        var current = await store.GetAsync(expected.Id, expected.Scope, cancellationToken).ConfigureAwait(false);
        var latestTask = await AuthorizeAsync(principal, reference, requireWrite: false, cancellationToken).ConfigureAwait(false);
        RequireBinding(expected, SessionFor(latestTask));
        if (current is not null)
        {
            RequireBinding(current, expected);
        }

        return current;
    }

    public async Task<ImportUploadSession> CreateAsync(ClaimsPrincipal principal, ImportUploadTaskReference reference,
        CancellationToken cancellationToken = default)
    {
        // Recover the original receipt before applying new-admission policy. An
        // imported destination may now exist while its receipt remains readable.
        var task = await AuthorizeAsync(principal, reference, requireWrite: false, cancellationToken).ConfigureAwait(false);
        var expected = SessionFor(task);
        var existing = await store.GetAsync(expected.Id, expected.Scope, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            RequireBinding(existing, expected);
            return await ReadCurrentAfterMutationAsync(principal, reference, cancellationToken).ConfigureAwait(false);
        }

        if (expected.ExpiresAt <= DateTimeOffset.UtcNow || expected.ExpiresAt > DateTimeOffset.UtcNow.AddDays(1))
        {
            throw new InvalidOperationException("The original upload admission window is unavailable.");
        }

        var contributor = await ReauthorizeAsync(principal, reference, expected, cancellationToken).ConfigureAwait(false);
        _ = await store.TryCreateAsync(expected, contributor, cancellationToken).ConfigureAwait(false);
        // A racing create returns the same persisted session. A lost commit reply
        // is recovered with GetAsync against the original task, never a new ID.
        var created = await ReadCurrentAfterMutationAsync(principal, reference, cancellationToken).ConfigureAwait(false);
        RequireBinding(created, expected);
        return created;
    }

    public async Task<ImportUploadSession> AppendAsync(ClaimsPrincipal principal, ImportUploadTaskReference reference,
        long expectedVersion, long expectedOffset, Stream content, string sha256,
        CancellationToken cancellationToken = default)
    {
        var task = await AuthorizeAsync(principal, reference, requireWrite: true, cancellationToken).ConfigureAwait(false);
        var current = await RequireSessionAsync(task, cancellationToken).ConfigureAwait(false);
        if (current.Version != expectedVersion || current.AcceptedBytes != expectedOffset || current.State != ImportUploadState.Receiving)
        {
            throw new InvalidOperationException("The upload range or version changed. Read the original session before continuing.");
        }

        var candidate = await _storage.StageAsync(current, content, sha256, cancellationToken).ConfigureAwait(false);
        var contributor = await ReauthorizeAsync(principal, reference, current, cancellationToken).ConfigureAwait(false);
        if (!await store.TryAppendAsync(current, candidate, contributor, cancellationToken).ConfigureAwait(false))
        {
            // A definite false means this private unique candidate was not accepted.
            // On an exception the result is unknown; never delete before recovery.
            _ = await _storage.DeleteUnacceptedAsync(current, candidate, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The upload changed before the chunk could be accepted.");
        }

        return await ReadCurrentAfterMutationAsync(principal, reference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportUploadSession> VerifyAndSealAsync(ClaimsPrincipal principal, ImportUploadTaskReference reference,
        long expectedVersion, CancellationToken cancellationToken = default)
    {
        var task = await AuthorizeAsync(principal, reference, requireWrite: false, cancellationToken).ConfigureAwait(false);
        var current = await RequireSessionAsync(task, cancellationToken).ConfigureAwait(false);
        if (current.State == ImportUploadState.Sealed)
        {
            RequireSealedDigest(current, task);
            return await ReadCurrentAfterMutationAsync(principal, reference, cancellationToken).ConfigureAwait(false);
        }

        if (current.Version != expectedVersion)
        {
            throw new InvalidOperationException("The upload version changed before verification.");
        }

        await ReauthorizeAsync(principal, reference, current, cancellationToken).ConfigureAwait(false);
        var verifier = new ImportUploadContentVerifier(store, _storage);
        var verified = await verifier.VerifyAsync(current, task.Source.ExpectedSha256!,
            token => ReauthorizeAsync(principal, reference, current, token), cancellationToken).ConfigureAwait(false);
        var contributor = await ReauthorizeAsync(principal, reference, verified.Session, cancellationToken).ConfigureAwait(false);
        _ = await store.TrySealAsync(verified.Session, verified.Sha256, contributor, cancellationToken).ConfigureAwait(false);
        var sealedSession = await ReadCurrentAfterMutationAsync(principal, reference, cancellationToken).ConfigureAwait(false);
        RequireSealedDigest(sealedSession, task);
        return sealedSession;
    }

    // This only selects an immutable source. Queue admission and accepting a job
    // remain separate, and require quota-backed storage and source-aware deadlines.
    public async Task<ImportUploadSession> RetainSourceAsync(ClaimsPrincipal principal, ImportUploadTaskReference reference,
        long expectedVersion, string scratchDirectory, long maximumScratchBytes, CancellationToken cancellationToken = default)
    {
        var task = await AuthorizeAsync(principal, reference, requireWrite: false, cancellationToken).ConfigureAwait(false);
        var current = await RequireSessionAsync(task, cancellationToken).ConfigureAwait(false);
        RequireSealedDigest(current, task);
        if (current.StagedSource is not null)
        {
            // A retry observes the original winner even when new-write authority
            // has changed. Observation still requires current task read access.
            return await ReadCurrentAfterMutationAsync(principal, reference, cancellationToken).ConfigureAwait(false);
        }

        if (current.Version != expectedVersion)
        {
            throw new InvalidOperationException("The upload version changed before source retention.");
        }

        await ReauthorizeAsync(principal, reference, current, cancellationToken).ConfigureAwait(false);
        var source = await _sources.StageAsync(current,
            scratchDirectory, maximumScratchBytes,
            token => ReauthorizeAsync(principal, reference, current, token), cancellationToken).ConfigureAwait(false);
        var contributor = await ReauthorizeAsync(principal, reference, current, cancellationToken).ConfigureAwait(false);
        _ = await store.TryBindSourceAsync(current, source, contributor, cancellationToken).ConfigureAwait(false);
        var retained = await ReadCurrentAfterMutationAsync(principal, reference, cancellationToken).ConfigureAwait(false);
        RequireSealedDigest(retained, task);
        if (retained.StagedSource is null)
        {
            throw new IOException("The retained source receipt is unavailable. Its outcome is unknown.");
        }
        return retained;
    }

    public async Task<ImportUploadSession> CancelAsync(ClaimsPrincipal principal, ImportUploadTaskReference reference,
        long expectedVersion, CancellationToken cancellationToken = default)
    {
        var task = await AuthorizeAsync(principal, reference, requireWrite: false, cancellationToken).ConfigureAwait(false);
        var current = await RequireSessionAsync(task, cancellationToken).ConfigureAwait(false);
        if (current.State == ImportUploadState.Cancelled)
        {
            return await ReadCurrentAfterMutationAsync(principal, reference, cancellationToken).ConfigureAwait(false);
        }

        if (current.State != ImportUploadState.Receiving || current.Version != expectedVersion)
        {
            throw new InvalidOperationException("The upload cannot be cancelled at this version.");
        }

        var contributor = await ReauthorizeAsync(principal, reference, current, cancellationToken).ConfigureAwait(false);
        if (!await store.TryCancelAsync(current, contributor, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The upload changed before cancellation.");
        }

        return await ReadCurrentAfterMutationAsync(principal, reference, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DatasetImportTask> AuthorizeAsync(ClaimsPrincipal principal, ImportUploadTaskReference reference,
        bool requireWrite, CancellationToken cancellationToken)
        => (await authority.AuthorizeAsync(principal, reference, requireWrite, cancellationToken).ConfigureAwait(false)).Task;

    private async Task<string> ReauthorizeAsync(ClaimsPrincipal principal, ImportUploadTaskReference reference,
        ImportUploadSession expected, CancellationToken cancellationToken)
    {
        var current = await authority.AuthorizeAsync(principal, reference, requireWrite: true, cancellationToken).ConfigureAwait(false);
        RequireBinding(expected, SessionFor(current.Task));
        return current.ContributorActorId;
    }

    private async Task<ImportUploadSession> RequireSessionAsync(DatasetImportTask task, CancellationToken cancellationToken)
    {
        var expected = SessionFor(task);
        var current = await store.GetAsync(expected.Id, expected.Scope, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The original upload session is unavailable.");
        RequireBinding(current, expected);
        return current;
    }

    private async Task<ImportUploadSession> ReadCurrentAfterMutationAsync(ClaimsPrincipal principal,
        ImportUploadTaskReference reference, CancellationToken cancellationToken)
        => await GetAsync(principal, reference, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The original upload receipt is unavailable. Its outcome is unknown.");

    private static ImportUploadSession SessionFor(DatasetImportTask task)
    {
        RedisImportUploadStore.ValidateDigest(task.Source.ExpectedSha256!);
        var scope = new ImportUploadScope(task.OwnerActorId, task.TenantId, task.TargetId,
            task.WorkspaceId, task.TaskId, task.Revision);
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        identity.AppendData("honua-import-upload/v1\0"u8);
        identity.AppendData(JsonSerializer.SerializeToUtf8Bytes(scope, ImportUploadJsonContext.Default.ImportUploadScope));
        var session = new ImportUploadSession
        {
            Id = Convert.ToHexStringLower(identity.GetHashAndReset())[..32],
            Scope = scope,
            IntentSha256 = task.ContentHash,
            FileName = task.Source.FileName,
            TotalBytes = task.Source.TotalBytes,
            ChunkBytes = RedisImportUploadStore.MaximumChunkBytes,
            ExpiresAt = task.UploadAdmissionDeadline
        };
        RedisImportUploadStore.Validate(session);
        return session;
    }

    private static void RequireBinding(ImportUploadSession actual, ImportUploadSession expected)
    {
        if (actual.Id != expected.Id || actual.Scope != expected.Scope || actual.IntentSha256 != expected.IntentSha256 ||
            actual.FileName != expected.FileName || actual.TotalBytes != expected.TotalBytes ||
            actual.ChunkBytes != expected.ChunkBytes || actual.ExpiresAt != expected.ExpiresAt)
        {
            throw new InvalidOperationException("The upload is bound to a different immutable import intent.");
        }
    }

    private static void RequireSealedDigest(ImportUploadSession session, DatasetImportTask task)
    {
        if (session.State != ImportUploadState.Sealed || session.ContentSha256 != task.Source.ExpectedSha256)
        {
            throw new InvalidOperationException("The original upload has no matching verified seal.");
        }
    }
}

internal sealed record ImportUploadTaskReference(string WorkspaceId, string TaskId, long Revision, string ContentHash);
