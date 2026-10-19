// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using StackExchange.Redis;

namespace Honua.Import.FileImport.Resumable;

internal sealed partial class RedisImportStorageBudget
{
    private const int RecoveryBatchSize = 64;
    private const string ReadEpochScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return '' end
        if redis.call('TYPE', KEYS[1]).ok ~= 'hash' or redis.call('PTTL', KEYS[1]) >= 0 then return 'invalid' end
        return redis.call('HGET', KEYS[1], 'epoch') or 'invalid'
        """;

    private const string BeginRecoveryScript = """
        if redis.call('EXISTS', KEYS[1]) ~= 0 then return -2 end
        redis.call('HSET', KEYS[1], 'epoch', ARGV[1], 'limits', ARGV[2], 'bytes', '0', 'count', '0', 'building', '1')
        redis.call('PEXPIRE', KEYS[1], 3600000)
        return 1
        """;

    // Only the private staging key changes while a streamed census is consumed.
    // A partial/error result is never published as the active accounting ledger.
    private const string AppendRecoveryScript = """
        if redis.call('TYPE', KEYS[1]).ok ~= 'hash' or redis.call('HGET', KEYS[1], 'epoch') ~= ARGV[1]
           or redis.call('HGET', KEYS[1], 'building') ~= '1' or (#ARGV - 1) % 5 ~= 0 then return -2 end
        for i = 2, #ARGV, 5 do
          if redis.call('HEXISTS', KEYS[1], ARGV[i]) ~= 0 then return -1 end
          local bytes = tonumber(ARGV[i+4])
          local total = tonumber(redis.call('HGET', KEYS[1], 'bytes'))
          local count = tonumber(redis.call('HGET', KEYS[1], 'count'))
          if not bytes or bytes < 1 or bytes ~= math.floor(bytes) or not total or total < 0
             or total > 1125899906842624 - bytes or not count or count < 0 or count >= 1000000 then return -2 end
          redis.call('HSET', KEYS[1], ARGV[i], ARGV[i+1])
          redis.call('HINCRBY', KEYS[1], 'bytes', ARGV[i+4])
          redis.call('HINCRBY', KEYS[1], ARGV[i+2], ARGV[i+4])
          redis.call('HINCRBY', KEYS[1], ARGV[i+3], ARGV[i+4])
          redis.call('HINCRBY', KEYS[1], 'count', 1)
        end
        redis.call('PEXPIRE', KEYS[1], 3600000)
        return 1
        """;

    private const string PublishRecoveryScript = """
        if redis.call('TYPE', KEYS[2]).ok ~= 'hash'
           or redis.call('HGET', KEYS[2], 'epoch') ~= ARGV[2]
           or redis.call('HGET', KEYS[2], 'limits') ~= ARGV[3]
           or redis.call('HGET', KEYS[2], 'bytes') ~= ARGV[4]
           or redis.call('HGET', KEYS[2], 'count') ~= ARGV[5]
           or redis.call('HGET', KEYS[2], 'building') ~= '1' then return -2 end
        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind == 'none' then
          if ARGV[1] ~= '' then return -4 end
        else
          if kind ~= 'hash' or redis.call('PTTL', KEYS[1]) >= 0 then return -2 end
          if ARGV[1] == '' or redis.call('HGET', KEYS[1], 'epoch') ~= ARGV[1] then return -4 end
        end
        redis.call('HDEL', KEYS[2], 'building')
        redis.call('PERSIST', KEYS[2])
        local renamed = redis.pcall('RENAME', KEYS[2], KEYS[1])
        if type(renamed) == 'table' and renamed.err then
          redis.call('PEXPIRE', KEYS[2], 3600000)
          return -2
        end
        return 1
        """;

    internal async Task<string?> ReadStoredEpochAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var epoch = (string?)await _database.ScriptEvaluateAsync(ReadEpochScript, [Key]).ConfigureAwait(false);
        if (epoch == string.Empty) return null;
        if (epoch is not { Length: 32 } || !Guid.TryParseExact(epoch, "N", out var parsed) || parsed.ToString("N") != epoch)
            throw new InvalidDataException("The storage ledger has no valid durable recovery epoch.");
        return epoch;
    }

    // Caller must hold the exclusive physical domain fence, census every writer's
    // roots, and durably advance the local journal to Epoch before invoking this.
    // In particular, loss of Redis is NOT proof that a domain contains no files.
    // Late old-epoch reserve/release commands cannot affect the published ledger.
    internal async Task RebuildFromCensusAsync(string? expectedPreviousEpoch,
        IAsyncEnumerable<ImportStorageReservation> retained, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retained);
        if (expectedPreviousEpoch is not null)
        {
            ValidateEpoch(expectedPreviousEpoch);
            if (expectedPreviousEpoch == Epoch)
                throw new ArgumentException("A storage rebuild must use a fresh durable epoch.", nameof(expectedPreviousEpoch));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Same hash tag keeps staging and active keys in one Redis Cluster slot.
        RedisKey staging = $"{Key}:recovery:{Guid.NewGuid():N}";
        try
        {
            _ = Interpret((long)await _database.ScriptEvaluateAsync(BeginRecoveryScript, [staging], [Epoch, Policy]).ConfigureAwait(false));
            var batch = new List<RedisValue>(1 + (5 * RecoveryBatchSize)) { Epoch };
            long bytes = 0;
            var count = 0;
            await foreach (var reservation in retained.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Validate(reservation);
                bytes = checked(bytes + reservation.Bytes);
                if (bytes > MaximumBudgetBytes || ++count > 1000000)
                    throw new InvalidDataException("The physical census exceeds the supported accounting range.");
                batch.Add(Field(reservation));
                batch.Add(Serialize(reservation));
                batch.Add(TenantField(reservation));
                batch.Add(OwnerField(reservation));
                batch.Add(reservation.Bytes);
                if (batch.Count != 1 + (5 * RecoveryBatchSize)) continue;
                _ = Interpret((long)await _database.ScriptEvaluateAsync(AppendRecoveryScript, [staging], batch.ToArray()).ConfigureAwait(false));
                batch.Clear();
                batch.Add(Epoch);
            }
            if (batch.Count > 1)
                _ = Interpret((long)await _database.ScriptEvaluateAsync(AppendRecoveryScript, [staging], batch.ToArray()).ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interpret((long)await _database.ScriptEvaluateAsync(PublishRecoveryScript, [Key, staging],
                [expectedPreviousEpoch ?? string.Empty, Epoch, Policy, bytes, count]).ConfigureAwait(false));
        }
        finally
        {
            // If publication's response was lost, the candidate has already been
            // renamed; deleting this unique staging key cannot delete the ledger.
            try { _ = await _database.KeyDeleteAsync(staging).ConfigureAwait(false); }
            catch (Exception exception) when (exception is RedisException or ObjectDisposedException)
            {
                // Inactive candidates expire; the active ledger never does.
            }
        }
    }
}
