// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace Honua.Import.FileImport.Resumable;

/// <summary>
/// Atomic storage admission accounting, separate from registered artifact quotas.
/// Reservations have no automatic expiry: an expired transfer does not prove its
/// objects were deleted. This ledger requires durable Redis and must be reconciled
/// with storage before admitting writes after loss of accounting state.
/// </summary>
internal sealed partial class RedisImportStorageBudget : IImportStorageBudget
{
    // Lua numbers exactly represent every integer within this admitted range.
    internal const long MaximumBudgetBytes = 1L << 50;
    private readonly IDatabase _database;
    private readonly ImportStorageBudgetLimits _limits;
    internal RedisKey Key { get; }
    internal string Epoch { get; }
    private string Policy => FormattableString.Invariant($"{_limits.GlobalBytes}:{_limits.TenantBytes}:{_limits.OwnerBytes}:{_limits.MaximumObjects}");

    private const string ReserveScript = """
        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind ~= 'hash' then return -2 end
        if redis.call('PTTL', KEYS[1]) >= 0 then return -2 end
        if redis.call('HGET', KEYS[1], 'epoch') ~= ARGV[11] then return -4 end
        local existing = redis.call('HGET', KEYS[1], ARGV[1])
        if existing and existing ~= ARGV[2] then return -1 end
        local policy = redis.call('HGET', KEYS[1], 'limits')
        if kind == 'hash' and not policy then return -2 end
        if policy and policy ~= ARGV[10] and not existing then return -3 end
        local fields = {'bytes', ARGV[3], ARGV[4], 'count'}
        local amounts = {tonumber(ARGV[5]), tonumber(ARGV[5]), tonumber(ARGV[5]), 1}
        local limits = {tonumber(ARGV[6]), tonumber(ARGV[7]), tonumber(ARGV[8]), tonumber(ARGV[9])}
        local values = {}
        local exceeds = false
        for i = 1, 4 do
          local raw = redis.call('HGET', KEYS[1], fields[i])
          if not raw and (existing or kind == 'hash' and (i == 1 or i == 4)) then return -2 end
          local value = 0
          if raw then value = tonumber(raw) end
          if not value or value < 0 or value ~= math.floor(value) or value > 1125899906842624 then return -2 end
          if existing and value < amounts[i] then return -2 end
          if value > limits[i] - amounts[i] then exceeds = true end
          values[i] = value + amounts[i]
        end
        if values[1] < values[2] or values[2] < values[3] then return -2 end
        if existing then return 1 end
        if exceeds then return 0 end
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2], 'limits', ARGV[10], fields[1], string.format('%.0f', values[1]),
          fields[2], string.format('%.0f', values[2]), fields[3], string.format('%.0f', values[3]),
          fields[4], string.format('%.0f', values[4]))
        return 1
        """;

    private const string ReleaseScript = """
        if redis.call('TYPE', KEYS[1]).ok ~= 'hash' or redis.call('PTTL', KEYS[1]) >= 0 then return -2 end
        if redis.call('HGET', KEYS[1], 'epoch') ~= ARGV[6] then return -4 end
        local existing = redis.call('HGET', KEYS[1], ARGV[1])
        if not existing then return 0 end
        if existing ~= ARGV[2] then return -1 end
        local fields = {'bytes', ARGV[3], ARGV[4], 'count'}
        local amounts = {tonumber(ARGV[5]), tonumber(ARGV[5]), tonumber(ARGV[5]), 1}
        local values = {}
        for i = 1, 4 do
          local value = tonumber(redis.call('HGET', KEYS[1], fields[i]))
          if not value or value < amounts[i] or value ~= math.floor(value) or value > 1125899906842624 then return -2 end
          values[i] = value - amounts[i]
        end
        redis.call('HDEL', KEYS[1], ARGV[1])
        redis.call('HSET', KEYS[1], fields[1], string.format('%.0f', values[1]),
          fields[2], string.format('%.0f', values[2]), fields[3], string.format('%.0f', values[3]),
          fields[4], string.format('%.0f', values[4]))
        if values[2] == 0 then redis.call('HDEL', KEYS[1], fields[2]) end
        if values[3] == 0 then redis.call('HDEL', KEYS[1], fields[3]) end
        return 1
        """;

    // storageBudgetId is host configuration identifying the actual shared storage
    // domain, not a user/task-selected ID. Every replica must use the same domain.
    internal RedisImportStorageBudget(IConnectionMultiplexer redis, string storageBudgetId, ImportStorageBudgetLimits limits, string epoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageBudgetId);
        if (storageBudgetId.Length > 256 || storageBudgetId.Any(char.IsControl)) throw new ArgumentException("Invalid storage budget identity.");
        ArgumentNullException.ThrowIfNull(limits);
        ValidateEpoch(epoch);
        if (limits.GlobalBytes is < 1 or > MaximumBudgetBytes || limits.TenantBytes < 1 || limits.TenantBytes > limits.GlobalBytes ||
            limits.OwnerBytes < 1 || limits.OwnerBytes > limits.TenantBytes || limits.MaximumObjects is < 1 or > 1000000)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _database = redis.GetDatabase();
        _limits = limits;
        Epoch = epoch;
        Key = $"honua:import-storage:{{{Hash(storageBudgetId)}}}:reservations";
    }

    public async Task<bool> TryReserveAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
    {
        Validate(reservation);
        cancellationToken.ThrowIfCancellationRequested();
        var result = (long)await _database.ScriptEvaluateAsync(ReserveScript, [Key],
            [Field(reservation), Serialize(reservation), TenantField(reservation), OwnerField(reservation), reservation.Bytes,
                _limits.GlobalBytes, _limits.TenantBytes, _limits.OwnerBytes, _limits.MaximumObjects,
                Policy, Epoch]).ConfigureAwait(false);
        return Interpret(result);
    }

    // Only the storage cleanup coordinator may call this after proving that its
    // writer finished and the exact unique candidate was removed. A TTL, missing
    // upload receipt, timeout, or uncertain provider response is not that proof.
    public async Task<bool> ReleaseDeletedAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
    {
        Validate(reservation);
        cancellationToken.ThrowIfCancellationRequested();
        var result = (long)await _database.ScriptEvaluateAsync(ReleaseScript, [Key],
            [Field(reservation), Serialize(reservation), TenantField(reservation), OwnerField(reservation), reservation.Bytes, Epoch]).ConfigureAwait(false);
        return Interpret(result);
    }

    internal static ImportStorageReservation ForObject(ImportUploadSession session, string fileId, long bytes)
    {
        RedisImportUploadStore.Validate(session);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, session.TotalBytes);
        var reservation = new ImportStorageReservation(session.Id, session.Scope, fileId, bytes);
        Validate(reservation);
        return reservation;
    }

    internal static void Validate(ImportStorageReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        _ = RedisImportUploadStore.Key(reservation.UploadId);
        RedisImportUploadStore.ValidateScope(reservation.Scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(reservation.Bytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reservation.Bytes, RedisImportUploadStore.MaximumFileBytes);
        if (string.IsNullOrWhiteSpace(reservation.FileId) || reservation.FileId.Length > 1024 || reservation.FileId.Any(char.IsControl) ||
            !reservation.FileId.StartsWith($"imports/resumable/{reservation.UploadId}/", StringComparison.Ordinal))
            throw new ArgumentException("The reservation must identify its upload's server-owned object.", nameof(reservation));
        var leaf = reservation.FileId[("imports/resumable/".Length + reservation.UploadId.Length + 1)..];
        var identity = leaf.EndsWith(".part", StringComparison.Ordinal) ? leaf[..^5]
            : leaf.StartsWith("source-", StringComparison.Ordinal) ? leaf[7..]
            : leaf.StartsWith("scratch-", StringComparison.Ordinal) ? leaf[8..] : string.Empty;
        _ = RedisImportUploadStore.Key(identity);
    }

    private static bool Interpret(long result) => result switch
    {
        1 => true,
        0 => false,
        -1 => throw new InvalidOperationException("The storage reservation belongs to a different immutable request."),
        -3 => throw new InvalidOperationException("Storage budget configuration differs from the retained admission policy."),
        -4 => throw new InvalidOperationException("Storage accounting belongs to another recovery epoch."),
        _ => throw new InvalidDataException("Import storage accounting is unavailable or invalid.")
    };

    private static string Field(ImportStorageReservation reservation) => "object:" + Hash(reservation.FileId);
    private static string TenantField(ImportStorageReservation reservation) => "tenant:" + AccountHash(reservation, includeActor: false);
    private static string OwnerField(ImportStorageReservation reservation) => "owner:" + AccountHash(reservation, includeActor: true);
    private static string AccountHash(ImportStorageReservation reservation, bool includeActor)
        => Hash(JsonSerializer.Serialize(new ImportStorageAccount(reservation.Scope.TargetId, reservation.Scope.TenantId,
            includeActor ? reservation.Scope.ActorId : null), ImportUploadJsonContext.Default.ImportStorageAccount));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Serialize(ImportStorageReservation reservation)
        => JsonSerializer.Serialize(reservation, ImportUploadJsonContext.Default.ImportStorageReservation);

    private static void ValidateEpoch(string epoch)
    {
        if (epoch is not { Length: 32 } || !Guid.TryParseExact(epoch, "N", out var parsed) || parsed.ToString("N") != epoch)
            throw new ArgumentException("Storage accounting requires its durable canonical recovery epoch.", nameof(epoch));
    }
}

internal sealed record ImportStorageBudgetLimits(long GlobalBytes, long TenantBytes, long OwnerBytes, int MaximumObjects);
internal sealed record ImportStorageReservation(string UploadId, ImportUploadScope Scope, string FileId, long Bytes);
internal sealed record ImportStorageAccount(string TargetId, string? TenantId, string? ActorId);

// Admission precedes provider writes. Release requires a completed writer and a
// positively confirmed deletion of its exact generation, never a timeout or TTL.
internal interface IImportStorageBudget
{
    Task<bool> TryReserveAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default);
    Task<bool> ReleaseDeletedAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default);
}
