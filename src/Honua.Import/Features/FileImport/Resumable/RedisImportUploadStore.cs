// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using StackExchange.Redis;

namespace Honua.Import.FileImport.Resumable;

/// <summary>
/// Durable receipts for sequential resumable transfers, independent of the import runner.
/// No in-memory fallback: an uncertain write is reconciled by reading this same session.
/// Authorization and streaming verification precede these internal storage operations.
/// </summary>
internal sealed class RedisImportUploadStore(IConnectionMultiplexer redis)
{
    internal const int MaximumChunkBytes = 4 * 1024 * 1024;
    internal const long MaximumFileBytes = 1024L * 1024 * 1024 * 1024;
    internal const int MaximumChunks = 65536;
    private const int MaximumPageSize = 128;
    private const string StateField = "state";

    // State and accepted chunk share ONE hash and ONE HSET. They cannot diverge
    // because a second write fails, and cancellation/expiry cannot leave a chunk
    // accepted without its byte offset. All keys naturally share a Redis hash slot.
    private const string CreateScript = """
        if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
        local now = redis.call('TIME')
        local milliseconds = tonumber(now[1]) * 1000 + math.floor(tonumber(now[2]) / 1000)
        if tonumber(ARGV[2]) <= milliseconds then return 0 end
        redis.call('HSET', KEYS[1], 'state', ARGV[1], ARGV[3], ARGV[4])
        redis.call('PEXPIREAT', KEYS[1], ARGV[2])
        return 1
        """;

    private const string AppendScript = """
        if redis.call('HGET', KEYS[1], 'state') ~= ARGV[1] then return 0 end
        if redis.call('PTTL', KEYS[1]) <= 0 then return 0 end
        if redis.call('HEXISTS', KEYS[1], ARGV[3]) == 1 then return 0 end
        redis.call('HSET', KEYS[1], 'state', ARGV[2], ARGV[3], ARGV[4], ARGV[5], ARGV[6])
        return 1
        """;

    private const string TransitionScript = """
        if redis.call('HGET', KEYS[1], 'state') ~= ARGV[1] then return 0 end
        if redis.call('PTTL', KEYS[1]) <= 0 then return 0 end
        redis.call('HSET', KEYS[1], 'state', ARGV[2], ARGV[3], ARGV[4])
        return 1
        """;

    private readonly IDatabase _database = redis.GetDatabase();

    public Task<bool> TryCreateAsync(ImportUploadSession session, CancellationToken cancellationToken = default)
        => TryCreateAsync(session, session.Scope.ActorId, cancellationToken);

    public async Task<bool> TryCreateAsync(ImportUploadSession session, string contributorActorId,
        CancellationToken cancellationToken = default)
    {
        Validate(session);
        if (session.Version != 1 || session.AcceptedBytes != 0 || session.ChunkCount != 0 ||
            session.State != ImportUploadState.Receiving || session.ContentSha256 is not null || session.StagedSource is not null ||
            session.ExpiresAt > DateTimeOffset.UtcNow.AddDays(1))
        {
            throw new ArgumentException("An upload must start with an empty receiving session.", nameof(session));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (long)await _database.ScriptEvaluateAsync(CreateScript, [Key(session.Id)],
            [Serialize(session), session.ExpiresAt.ToUnixTimeMilliseconds(), MutationField(session.Version),
                Mutation(session.Version, ImportUploadMutationKind.Create, contributorActorId)]).ConfigureAwait(false) == 1;
    }

    public async Task<ImportUploadSession?> GetAsync(string id, ImportUploadScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await _database.HashGetAsync(Key(id), StateField).ConfigureAwait(false);
        if (!payload.HasValue)
        {
            return null;
        }

        var session = JsonSerializer.Deserialize(payload.ToString(), ImportUploadJsonContext.Default.ImportUploadSession)
            ?? throw new InvalidDataException("Upload session is invalid.");
        Validate(session);
        return session.Id == id && session.Scope == scope ? session : null;
    }

    public Task<bool> TryAppendAsync(ImportUploadSession expected, ImportUploadChunk chunk,
        CancellationToken cancellationToken = default) => TryAppendAsync(expected, chunk, expected.Scope.ActorId, cancellationToken);

    public async Task<bool> TryAppendAsync(ImportUploadSession expected, ImportUploadChunk chunk,
        string contributorActorId, CancellationToken cancellationToken = default)
    {
        Validate(expected);
        ArgumentNullException.ThrowIfNull(chunk);
        if (expected.State != ImportUploadState.Receiving)
        {
            return false;
        }

        var remaining = expected.TotalBytes - expected.AcceptedBytes;
        if (chunk.Index != expected.ChunkCount || chunk.Offset != expected.AcceptedBytes ||
            chunk.Bytes <= 0 || chunk.Bytes != Math.Min(expected.ChunkBytes, remaining))
        {
            throw new ArgumentException("Chunk must exactly fill the next declared range.", nameof(chunk));
        }

        ValidateDigest(chunk.Sha256);
        ValidateText(chunk.FileId, 1024);
        ValidateText(chunk.ETag, 1024);
        var updated = expected with
        {
            Version = checked(expected.Version + 1),
            AcceptedBytes = checked(expected.AcceptedBytes + chunk.Bytes),
            ChunkCount = checked(expected.ChunkCount + 1)
        };
        cancellationToken.ThrowIfCancellationRequested();
        return (long)await _database.ScriptEvaluateAsync(AppendScript, [Key(expected.Id)],
            [Serialize(expected), Serialize(updated), ChunkField(chunk.Index),
                JsonSerializer.Serialize(chunk, ImportUploadJsonContext.Default.ImportUploadChunk), MutationField(updated.Version),
                Mutation(updated.Version, ImportUploadMutationKind.Append, contributorActorId)]).ConfigureAwait(false) == 1;
    }

    public Task<bool> TryCancelAsync(ImportUploadSession expected, CancellationToken cancellationToken = default)
        => TryCancelAsync(expected, expected.Scope.ActorId, cancellationToken);

    public Task<bool> TryCancelAsync(ImportUploadSession expected, string contributorActorId, CancellationToken cancellationToken = default)
    {
        Validate(expected);
        return expected.State != ImportUploadState.Receiving
            ? Task.FromResult(false)
            : TransitionAsync(expected, expected with { State = ImportUploadState.Cancelled, Version = checked(expected.Version + 1) },
                ImportUploadMutationKind.Cancel, contributorActorId, cancellationToken);
    }

    // The caller supplies the digest computed while reading ALL verified chunk
    // bytes. A seal is not an import/job acceptance receipt and must never be
    // presented to a client as proof that a data mutation was submitted.
    public Task<bool> TrySealAsync(ImportUploadSession expected, string contentSha256,
        CancellationToken cancellationToken = default) => TrySealAsync(expected, contentSha256, expected.Scope.ActorId, cancellationToken);

    public Task<bool> TrySealAsync(ImportUploadSession expected, string contentSha256,
        string contributorActorId, CancellationToken cancellationToken = default)
    {
        Validate(expected);
        ValidateDigest(contentSha256);
        return expected.State != ImportUploadState.Receiving || expected.AcceptedBytes != expected.TotalBytes
            ? Task.FromResult(false)
            : TransitionAsync(expected, expected with
            {
                State = ImportUploadState.Sealed,
                ContentSha256 = contentSha256,
                Version = checked(expected.Version + 1)
            }, ImportUploadMutationKind.Seal, contributorActorId, cancellationToken);
    }

    // Select exactly one durable source before job acceptance. A lost CAS reply is
    // recovered from this receipt; another candidate cannot overwrite the winner.
    public Task<bool> TryBindSourceAsync(ImportUploadSession expected, ImportStagedSourceBinding source,
        CancellationToken cancellationToken = default) => TryBindSourceAsync(expected, source, expected.Scope.ActorId, cancellationToken);

    public Task<bool> TryBindSourceAsync(ImportUploadSession expected, ImportStagedSourceBinding source,
        string contributorActorId, CancellationToken cancellationToken = default)
    {
        Validate(expected);
        ArgumentNullException.ThrowIfNull(source);
        if (expected.State != ImportUploadState.Sealed || expected.StagedSource is not null)
        {
            return Task.FromResult(false);
        }

        var updated = expected with { StagedSource = source, Version = checked(expected.Version + 1) };
        Validate(updated);
        return TransitionAsync(expected, updated, ImportUploadMutationKind.RetainSource, contributorActorId, cancellationToken);
    }

    public async Task<IReadOnlyList<ImportUploadChunk>> ReadChunksAsync(string id, ImportUploadScope scope,
        int first, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumPageSize);
        var session = await GetAsync(id, scope, cancellationToken).ConfigureAwait(false);
        if (session is null || session.State == ImportUploadState.Cancelled || first >= session.ChunkCount)
        {
            return [];
        }

        var length = Math.Min(count, session.ChunkCount - first);
        RedisValue[] fields = Enumerable.Range(first, length).Select(index => (RedisValue)ChunkField(index)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var payloads = await _database.HashGetAsync(Key(id), fields).ConfigureAwait(false);
        var chunks = new List<ImportUploadChunk>(length);
        for (var i = 0; i < payloads.Length; i++)
        {
            if (!payloads[i].HasValue)
            {
                throw new InvalidDataException("An accepted upload chunk is unavailable.");
            }

            var chunk = JsonSerializer.Deserialize(payloads[i].ToString(), ImportUploadJsonContext.Default.ImportUploadChunk);
            var index = first + i;
            var offset = (long)index * session.ChunkBytes;
            if (chunk is null || chunk.Index != index || chunk.Offset != offset ||
                chunk.Bytes != Math.Min(session.ChunkBytes, session.TotalBytes - offset))
            {
                throw new InvalidDataException("An accepted upload chunk has an invalid range.");
            }

            ValidateDigest(chunk.Sha256);
            ValidateText(chunk.FileId, 1024);
            ValidateText(chunk.ETag, 1024);
            chunks.Add(chunk);
        }

        // A concurrent cancel or expiry must not turn observation into permission
        // to open the returned storage objects. The reader rechecks between chunks.
        var current = await GetAsync(id, scope, cancellationToken).ConfigureAwait(false);
        return current is null || current.State == ImportUploadState.Cancelled ? [] : chunks;
    }

    private async Task<bool> TransitionAsync(ImportUploadSession expected, ImportUploadSession updated,
        ImportUploadMutationKind kind, string contributorActorId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (long)await _database.ScriptEvaluateAsync(TransitionScript, [Key(expected.Id)],
            [Serialize(expected), Serialize(updated), MutationField(updated.Version),
                Mutation(updated.Version, kind, contributorActorId)]).ConfigureAwait(false) == 1;
    }

    internal static string Key(string id)
    {
        if (id is null || id.Length != 32 || id.Any(static value => !char.IsAsciiHexDigitLower(value)))
        {
            throw new ArgumentException("Upload identity must be a canonical random identifier.", nameof(id));
        }

        return $"honua:import-upload:{{{id}}}:receipt";
    }

    private static string MutationField(long version) => "mutation:" + version.ToString(CultureInfo.InvariantCulture);

    private static string Mutation(long version, ImportUploadMutationKind kind, string actor)
    {
        ValidateText(actor, 512);
        return JsonSerializer.Serialize(new ImportUploadMutation(version, kind, actor, DateTimeOffset.UtcNow),
            ImportUploadJsonContext.Default.ImportUploadMutation);
    }

    internal async Task<IReadOnlyList<ImportUploadMutation>> ReadMutationsAsync(string id, ImportUploadScope scope,
        long firstVersion, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstVersion, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumPageSize);
        var session = await GetAsync(id, scope, cancellationToken).ConfigureAwait(false);
        if (session is null || firstVersion > session.Version) return [];
        var length = (int)Math.Min(count, session.Version - firstVersion + 1);
        RedisValue[] fields = Enumerable.Range(0, length).Select(index => (RedisValue)MutationField(firstVersion + index)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var payloads = await _database.HashGetAsync(Key(id), fields).ConfigureAwait(false);
        var result = new List<ImportUploadMutation>(length);
        for (var index = 0; index < payloads.Length; index++)
        {
            if (!payloads[index].HasValue) throw new InvalidDataException("An accepted upload mutation receipt is unavailable.");
            var mutation = JsonSerializer.Deserialize(payloads[index].ToString(), ImportUploadJsonContext.Default.ImportUploadMutation);
            if (mutation is null || mutation.Version != firstVersion + index || !Enum.IsDefined(mutation.Kind) || mutation.RecordedAt == default)
                throw new InvalidDataException("An accepted upload mutation receipt is invalid.");
            ValidateText(mutation.ContributorActorId, 512);
            result.Add(mutation);
        }
        // Expiry during lookup cannot expose detached receipts after their scoped session is gone.
        return await GetAsync(id, scope, cancellationToken).ConfigureAwait(false) is null ? [] : result;
    }

    private static string ChunkField(int index) => "chunk:" + index.ToString(CultureInfo.InvariantCulture);
    private static string Serialize(ImportUploadSession value) =>
        JsonSerializer.Serialize(value, ImportUploadJsonContext.Default.ImportUploadSession);

    internal static void Validate(ImportUploadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _ = Key(session.Id);
        ValidateScope(session.Scope);
        ValidateDigest(session.IntentSha256);
        ValidateText(session.FileName, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(session.TotalBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(session.TotalBytes, MaximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(session.ChunkBytes, 64 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(session.ChunkBytes, MaximumChunkBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            (session.TotalBytes + session.ChunkBytes - 1) / session.ChunkBytes, MaximumChunks);
        if (session.Version < 1 || session.Version == long.MaxValue || session.AcceptedBytes < 0 ||
            session.AcceptedBytes > session.TotalBytes || session.ChunkCount < 0 ||
            session.ChunkCount != (session.AcceptedBytes + session.ChunkBytes - 1) / session.ChunkBytes ||
            session.AcceptedBytes != Math.Min((long)session.ChunkCount * session.ChunkBytes, session.TotalBytes) ||
            !Enum.IsDefined(session.State) ||
            session.State == ImportUploadState.Sealed && (session.AcceptedBytes != session.TotalBytes || session.ContentSha256 is null) ||
            session.State != ImportUploadState.Sealed && session.ContentSha256 is not null)
        {
            throw new ArgumentException("Upload session has an invalid state.", nameof(session));
        }

        if (session.ContentSha256 is not null)
        {
            ValidateDigest(session.ContentSha256);
        }

        if (session.StagedSource is { } source)
        {
            source.Validate();
            if (source.Transfer is { } transfer && (transfer.UploadId != session.Id || transfer.OwnerActorId != session.Scope.ActorId ||
                transfer.TenantId != session.Scope.TenantId || transfer.TargetId != session.Scope.TargetId ||
                transfer.WorkspaceId != session.Scope.WorkspaceId || transfer.TaskId != session.Scope.TaskId ||
                transfer.TaskRevision != session.Scope.TaskRevision || transfer.TaskContentHash != session.IntentSha256))
                throw new ArgumentException("The staged source provenance must match its original upload.", nameof(session));
            if (session.State != ImportUploadState.Sealed || source.Bytes != session.TotalBytes ||
                source.Sha256 != session.ContentSha256 ||
                source.ExpiresAt is null || source.ExpiresAt <= session.ExpiresAt ||
                !source.FileId.StartsWith($"imports/resumable/{session.Id}/source-", StringComparison.Ordinal))
            {
                throw new ArgumentException("The staged source must match this sealed upload.", nameof(session));
            }
        }
    }

    internal static void ValidateScope(ImportUploadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateText(scope.ActorId, 512);
        if (scope.TenantId is not null)
        {
            ValidateText(scope.TenantId, 256);
        }

        ValidateText(scope.TargetId, 256);
        ValidateText(scope.WorkspaceId, 256);
        ValidateText(scope.TaskId, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(scope.TaskRevision, 1);
    }

    internal static void ValidateDigest(string value)
    {
        if (value is null || value.Length != 64 || value.Any(static character => !char.IsAsciiHexDigitLower(character)))
        {
            throw new ArgumentException("A lowercase SHA-256 digest is required.", nameof(value));
        }
    }

    private static void ValidateText(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() || value.Any(char.IsControl))
        {
            throw new ArgumentException("Upload binding contains an invalid value.", nameof(value));
        }
    }
}
