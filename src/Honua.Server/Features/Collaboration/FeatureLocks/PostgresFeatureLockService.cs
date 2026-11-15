// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.ControlPlane;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Collaboration.FeatureLocks;

internal sealed class PostgresFeatureLockService(JobStoreDatabase database) : IFeatureLockService, IExpiredRowSweep
{
    private const string DeniedReason = "Feature lock mutation requires authorized write access.";
    private readonly string _table = database.Table("feature_locks");

    public ValueTask<FeatureLockClaimResponse> ClaimAsync(FeatureRef feature, LockHolder holder,
        TimeSpan leaseDuration, FeatureLockAccessContext access, CancellationToken cancellationToken = default)
    {
        Validate(feature, holder, leaseDuration);
        if (!access.IsAuthorized || !access.CanWrite)
        {
            return ValueTask.FromResult(new FeatureLockClaimResponse(FeatureLockClaimStatus.Denied, DenialReason: DeniedReason));
        }
        return MutateAsync<FeatureLockClaimResponse>(feature, (existing, now) =>
        {
            if (existing is not null && !existing.Holder.Admits(holder))
            {
                return (new(FeatureLockClaimStatus.HeldByOther, Error: FeatureLockHeldError.FromLease(existing)), existing);
            }
            var lease = existing is null
                ? new FeatureLockLease(Guid.NewGuid().ToString("N"), feature, holder, now, now.Add(leaseDuration))
                : Renew(existing with { Holder = existing.Holder.RenewedBy(holder) }, leaseDuration, now);
            return (new(existing is null ? FeatureLockClaimStatus.Claimed : FeatureLockClaimStatus.Renewed, Lease: lease), lease);
        }, cancellationToken);
    }

    public ValueTask<FeatureLockRenewResponse> RenewAsync(FeatureRef feature, LockHolder holder,
        TimeSpan leaseDuration, FeatureLockAccessContext access, CancellationToken cancellationToken = default)
    {
        Validate(feature, holder, leaseDuration);
        if (!access.IsAuthorized || !access.CanWrite)
        {
            return ValueTask.FromResult(new FeatureLockRenewResponse(FeatureLockRenewStatus.Denied, DenialReason: DeniedReason));
        }
        return MutateAsync<FeatureLockRenewResponse>(feature, (existing, now) =>
        {
            if (existing is null)
            {
                return (new(FeatureLockRenewStatus.NotFound), null);
            }
            if (!existing.Holder.Admits(holder))
            {
                return (new(FeatureLockRenewStatus.HeldByOther, Error: FeatureLockHeldError.FromLease(existing)), existing);
            }
            var lease = Renew(existing with { Holder = existing.Holder.RenewedBy(holder) }, leaseDuration, now);
            return (new(FeatureLockRenewStatus.Renewed, Lease: lease), lease);
        }, cancellationToken);
    }

    public ValueTask<FeatureLockReleaseResponse> ReleaseAsync(FeatureRef feature, LockHolder holder,
        FeatureLockAccessContext access, CancellationToken cancellationToken = default)
    {
        Validate(feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder.HolderId);
        if (!access.IsAuthorized || !access.CanWrite)
        {
            return ValueTask.FromResult(new FeatureLockReleaseResponse(FeatureLockReleaseStatus.Denied, feature, DenialReason: DeniedReason));
        }
        return MutateAsync<FeatureLockReleaseResponse>(feature, (existing, _) =>
        {
            if (existing is null)
            {
                return (new(FeatureLockReleaseStatus.NotFound, feature), null);
            }
            return !existing.Holder.Admits(holder)
                ? (new(FeatureLockReleaseStatus.HeldByOther, feature, Error: FeatureLockHeldError.FromLease(existing)), existing)
                : (new(FeatureLockReleaseStatus.Released, feature, ReleasedLease: existing), null);
        }, cancellationToken);
    }

    public async ValueTask<FeatureLockLease?> GetActiveLeaseAsync(FeatureRef feature, CancellationToken cancellationToken = default)
    {
        Validate(feature);
        return await database.ExecuteAsync<FeatureLockLease?>(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                SELECT body FROM {_table}
                WHERE service_name = @service AND layer_id = @layer AND feature_id = @feature
                    AND expires_at > statement_timestamp()
                """, connection);
            AddKey(command, feature);
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string json ? Deserialize(json) : null;
        }, StatementRepeat.SafeToRepeat, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> HasAnyActiveLeasesAsync(CancellationToken cancellationToken = default)
        => await database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand(
                $"SELECT EXISTS (SELECT 1 FROM {_table} WHERE expires_at > statement_timestamp())", connection);
            return (bool)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        }, StatementRepeat.SafeToRepeat, cancellationToken).ConfigureAwait(false);

    public async ValueTask<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
        => await DeleteExpiredAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                WITH expired AS (
                    SELECT service_name, layer_id, feature_id FROM {_table}
                    WHERE expires_at <= statement_timestamp()
                    ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                DELETE FROM {_table} AS stored USING expired
                WHERE stored.service_name = expired.service_name AND stored.layer_id = expired.layer_id
                    AND stored.feature_id = expired.feature_id
                """, connection);
            command.Parameters.AddWithValue("batch_size", batchSize);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.NotAfterSend, cancellationToken);
    }

    private async ValueTask<TResult> MutateAsync<TResult>(FeatureRef feature,
        Func<FeatureLockLease?, DateTimeOffset, (TResult Result, FeatureLockLease? Lease)> transition,
        CancellationToken cancellationToken)
        => await database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            // This also serializes claims for a feature that has no row yet, so it is what prevents a double grant.
            // The key is built from the table (schema included, so separate catalogues do not block each other) and
            // the key columns in a fixed format, so tasks of different versions take the same lock during a rollout.
            await using (var gate = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(concat_ws('|', @table, @service, @layer::text, @feature), 0))",
                connection, transaction))
            {
                gate.Parameters.AddWithValue("table", _table);
                AddKey(gate, feature);
                await gate.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            DateTimeOffset now;
            await using (var clock = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction))
            {
                now = new DateTimeOffset((DateTime)(await clock.ExecuteScalarAsync(token).ConfigureAwait(false))!);
            }
            var mutation = new LeaseMutation(transaction, feature, _table);
            var existing = await mutation.ReadAsync(now, token).ConfigureAwait(false);
            var (result, lease) = transition(existing, now);
            // A refusal returns the live lease unchanged, and rewriting it would only add a row version. A null result
            // (released, or no live lease) always deletes, which also clears an expired row left for the sweep.
            if (lease is null || !ReferenceEquals(lease, existing))
            {
                await mutation.WriteAsync(lease, token).ConfigureAwait(false);
            }
            await transaction.CommitSafelyAsync(token).ConfigureAwait(false);
            return result;
        }, StatementRepeat.NotAfterSend, cancellationToken).ConfigureAwait(false);

    private sealed class LeaseMutation(NpgsqlTransaction transaction, FeatureRef feature, string table)
    {
        public async Task<FeatureLockLease?> ReadAsync(DateTimeOffset now, CancellationToken token)
        {
            await using var read = new NpgsqlCommand($"""
                SELECT body FROM {table}
                WHERE service_name = @service AND layer_id = @layer AND feature_id = @feature AND expires_at > @now
                FOR UPDATE
                """, transaction.Connection, transaction);
            AddKey(read, feature);
            read.Parameters.AddWithValue("now", now);
            return await read.ExecuteScalarAsync(token).ConfigureAwait(false) is string json ? Deserialize(json) : null;
        }

        public async Task WriteAsync(FeatureLockLease? lease, CancellationToken token)
        {
            await using var write = new NpgsqlCommand(lease is null ? $"""
                DELETE FROM {table} WHERE service_name = @service AND layer_id = @layer AND feature_id = @feature
                """ : $"""
                INSERT INTO {table} (service_name, layer_id, feature_id, body, expires_at)
                VALUES (@service, @layer, @feature, @body, @expiry)
                ON CONFLICT (service_name, layer_id, feature_id) DO UPDATE SET body = EXCLUDED.body, expires_at = EXCLUDED.expires_at
                """, transaction.Connection, transaction);
            AddKey(write, feature);
            if (lease is not null)
            {
                write.Parameters.AddWithValue("body", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(lease, FeatureLockStoreJsonContext.Default.FeatureLockLease));
                write.Parameters.AddWithValue("expiry", lease.ExpiresAt);
            }
            await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static FeatureLockLease Deserialize(string json)
        => JsonSerializer.Deserialize(json, FeatureLockStoreJsonContext.Default.FeatureLockLease)
            ?? throw new JsonException("Feature lease is missing.");

    private static void AddKey(NpgsqlCommand command, FeatureRef feature)
    {
        command.Parameters.AddWithValue("service", feature.ServiceName);
        command.Parameters.AddWithValue("layer", feature.LayerId);
        command.Parameters.AddWithValue("feature", feature.FeatureId);
    }

    private static FeatureLockLease Renew(FeatureLockLease lease, TimeSpan duration, DateTimeOffset now)
        => lease with { RenewedAt = now, ExpiresAt = now.Add(duration) };

    private static void Validate(FeatureRef feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.FeatureId);
    }

    private static void Validate(FeatureRef feature, LockHolder holder, TimeSpan duration)
    {
        Validate(feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder.HolderId);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Lease duration must be greater than zero.");
        }
    }
}

[JsonSerializable(typeof(FeatureLockLease))]
internal sealed partial class FeatureLockStoreJsonContext : JsonSerializerContext;
