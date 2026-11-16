// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Collaboration.FeatureLocks;

internal sealed class PostgresFeatureLockService : IFeatureLockService
{
    private const long FeatureLockMutexNamespace = 0x484F4E55;
    private const string DeniedReason = "Feature lock mutation requires authorized write access.";
    private const string Columns = "lock_id, holder_id, display_name, session_id, tenant_id, principal_name, acquired_at, expires_at, renewed_at";
    private const string FeaturePredicate = "service_name = @service AND layer_id = @layer AND feature_id = @feature";
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _configuredSchema;
    private readonly ISchemaContext? _schemaContext;
    private string _table => SchemaSearchPath.QualifyTable("feature_edit_locks", _schemaContext?.CurrentSchema ?? _configuredSchema);

    public PostgresFeatureLockService(NpgsqlDataSource dataSource, string? schemaName = null, ISchemaContext? schemaContext = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _configuredSchema = string.IsNullOrWhiteSpace(schemaName) ? "honua" : schemaName;
        _schemaContext = schemaContext;
    }

    public ValueTask<FeatureLockClaimResponse> ClaimAsync(
        FeatureRef feature, LockHolder holder, TimeSpan leaseDuration,
        FeatureLockAccessContext access, CancellationToken cancellationToken = default)
    {
        ValidateMutation(feature, holder, leaseDuration);
        if (!access.IsAuthorized || !access.CanWrite)
        {
            return ValueTask.FromResult(new FeatureLockClaimResponse(FeatureLockClaimStatus.Denied, DenialReason: DeniedReason));
        }

        return MutateAsync(feature, (existing, now) =>
        {
            if (existing is not null && !IsSameHolder(existing.Holder, holder))
            {
                return new Mutation<FeatureLockClaimResponse>(existing,
                    new(FeatureLockClaimStatus.HeldByOther, Error: FeatureLockHeldError.FromLease(existing)));
            }

            var lease = existing is null
                ? new FeatureLockLease(Guid.NewGuid().ToString("N"), feature, holder, now, now.Add(leaseDuration))
                : existing with { Holder = holder, RenewedAt = now, ExpiresAt = now.Add(leaseDuration) };
            return new Mutation<FeatureLockClaimResponse>(lease,
                new(existing is null ? FeatureLockClaimStatus.Claimed : FeatureLockClaimStatus.Renewed, Lease: lease));
        }, cancellationToken);
    }

    public ValueTask<FeatureLockRenewResponse> RenewAsync(
        FeatureRef feature, LockHolder holder, TimeSpan leaseDuration,
        FeatureLockAccessContext access, CancellationToken cancellationToken = default)
    {
        ValidateMutation(feature, holder, leaseDuration);
        if (!access.IsAuthorized || !access.CanWrite)
        {
            return ValueTask.FromResult(new FeatureLockRenewResponse(FeatureLockRenewStatus.Denied, DenialReason: DeniedReason));
        }

        return MutateAsync(feature, (existing, now) =>
        {
            if (existing is null)
            {
                return new Mutation<FeatureLockRenewResponse>(null, new(FeatureLockRenewStatus.NotFound));
            }
            if (!IsSameHolder(existing.Holder, holder))
            {
                return new Mutation<FeatureLockRenewResponse>(existing,
                    new(FeatureLockRenewStatus.HeldByOther, Error: FeatureLockHeldError.FromLease(existing)));
            }
            var lease = existing with { Holder = holder, RenewedAt = now, ExpiresAt = now.Add(leaseDuration) };
            return new Mutation<FeatureLockRenewResponse>(lease, new(FeatureLockRenewStatus.Renewed, Lease: lease));
        }, cancellationToken);
    }

    public ValueTask<FeatureLockReleaseResponse> ReleaseAsync(
        FeatureRef feature, LockHolder holder, FeatureLockAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateFeature(feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder.HolderId);
        if (!access.IsAuthorized || !access.CanWrite)
        {
            return ValueTask.FromResult(new FeatureLockReleaseResponse(FeatureLockReleaseStatus.Denied, feature, DenialReason: DeniedReason));
        }
        return MutateAsync(feature, (existing, _) =>
        {
            if (existing is null)
            {
                return new Mutation<FeatureLockReleaseResponse>(null, new(FeatureLockReleaseStatus.NotFound, feature));
            }
            return IsSameHolder(existing.Holder, holder)
                ? new Mutation<FeatureLockReleaseResponse>(null, new(FeatureLockReleaseStatus.Released, feature, ReleasedLease: existing))
                : new Mutation<FeatureLockReleaseResponse>(existing,
                    new(FeatureLockReleaseStatus.HeldByOther, feature, Error: FeatureLockHeldError.FromLease(existing)));
        }, cancellationToken);
    }

    public async ValueTask<FeatureLockLease?> GetActiveLeaseAsync(FeatureRef feature, CancellationToken cancellationToken = default)
    {
        ValidateFeature(feature);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLeaseAsync(connection, feature, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> HasAnyActiveLeasesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {_table} WHERE expires_at > clock_timestamp())", connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async ValueTask<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"DELETE FROM {_table} WHERE expires_at <= clock_timestamp()", connection);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> MutateAsync<T>(FeatureRef feature,
        Func<FeatureLockLease?, DateTimeOffset, Mutation<T>> mutate, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireFeatureMutexAsync(connection, feature, cancellationToken).ConfigureAwait(false);
        var existing = await ReadLeaseAsync(connection, feature, cancellationToken).ConfigureAwait(false);
        await using var clock = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        var now = new DateTimeOffset((DateTime)(await clock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
        var mutation = mutate(existing, now);
        if (!ReferenceEquals(existing, mutation.Lease))
        {
            await WriteLeaseAsync(connection, new LeaseWrite(feature, mutation.Lease), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return mutation.Response;
    }

    private async Task AcquireFeatureMutexAsync(NpgsqlConnection connection, FeatureRef feature, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, @namespace))", connection);
        command.Parameters.AddWithValue("namespace", FeatureLockMutexNamespace);
        command.Parameters.AddWithValue("key", string.Create(CultureInfo.InvariantCulture,
            $"{_table}:{feature.ServiceName.Length}:{feature.ServiceName}:{feature.LayerId}:{feature.FeatureId}"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<FeatureLockLease?> ReadLeaseAsync(NpgsqlConnection connection, FeatureRef feature, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_table} WHERE {FeaturePredicate} AND expires_at > clock_timestamp()", connection);
        AddFeatureParameters(command, feature);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new FeatureLockLease(reader.GetString(0), feature,
            new LockHolder(reader.GetString(1), ReadNullableText(reader, 2), ReadNullableText(reader, 3),
                ReadNullableText(reader, 4), ReadNullableText(reader, 5)),
            reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
    }

    private async Task WriteLeaseAsync(NpgsqlConnection connection, LeaseWrite write, CancellationToken cancellationToken)
    {
        var (feature, lease) = write;
        var sql = lease is null ? $"DELETE FROM {_table} WHERE {FeaturePredicate}" : $"""
            INSERT INTO {_table} (service_name, layer_id, feature_id, {Columns})
            VALUES (@service, @layer, @feature, @lock, @holder, @display, @session, @tenant, @principal, @acquired, @expires, @renewed)
            ON CONFLICT (service_name, layer_id, feature_id) DO UPDATE SET
                lock_id = EXCLUDED.lock_id, holder_id = EXCLUDED.holder_id, display_name = EXCLUDED.display_name,
                session_id = EXCLUDED.session_id, tenant_id = EXCLUDED.tenant_id, principal_name = EXCLUDED.principal_name,
                acquired_at = EXCLUDED.acquired_at, expires_at = EXCLUDED.expires_at, renewed_at = EXCLUDED.renewed_at
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        AddFeatureParameters(command, feature);
        if (lease is not null)
        {
            AddLeaseParameters(command, lease);
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddFeatureParameters(NpgsqlCommand command, FeatureRef feature)
    {
        command.Parameters.AddWithValue("service", feature.ServiceName);
        command.Parameters.AddWithValue("layer", feature.LayerId);
        command.Parameters.AddWithValue("feature", feature.FeatureId);
    }

    private static void AddLeaseParameters(NpgsqlCommand command, FeatureLockLease lease)
    {
        command.Parameters.AddWithValue("lock", lease.LockId);
        command.Parameters.AddWithValue("holder", lease.Holder.HolderId);
        command.Parameters.AddWithValue("display", NpgsqlDbType.Text, (object?)lease.Holder.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Text, (object?)lease.Holder.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, (object?)lease.Holder.TenantId ?? DBNull.Value);
        command.Parameters.AddWithValue("principal", NpgsqlDbType.Text, (object?)lease.Holder.PrincipalName ?? DBNull.Value);
        command.Parameters.AddWithValue("acquired", lease.AcquiredAt);
        command.Parameters.AddWithValue("expires", lease.ExpiresAt);
        command.Parameters.AddWithValue("renewed", NpgsqlDbType.TimestampTz, (object?)lease.RenewedAt ?? DBNull.Value);
    }

    private static string? ReadNullableText(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static bool IsSameHolder(LockHolder existing, LockHolder candidate)
        => (string.IsNullOrWhiteSpace(existing.PrincipalName)
            || string.Equals(existing.PrincipalName, candidate.PrincipalName, StringComparison.Ordinal))
           && string.Equals(existing.HolderId, candidate.HolderId, StringComparison.Ordinal)
           && ((!string.IsNullOrWhiteSpace(existing.SessionId) || !string.IsNullOrWhiteSpace(candidate.SessionId))
               ? string.Equals(existing.SessionId, candidate.SessionId, StringComparison.Ordinal)
               : (string.IsNullOrWhiteSpace(existing.TenantId) && string.IsNullOrWhiteSpace(candidate.TenantId))
                 || string.Equals(existing.TenantId, candidate.TenantId, StringComparison.Ordinal));

    private static void ValidateFeature(FeatureRef feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.FeatureId);
    }

    private static void ValidateMutation(FeatureRef feature, LockHolder holder, TimeSpan leaseDuration)
    {
        ValidateFeature(feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder.HolderId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
    }

    private sealed record LeaseWrite(FeatureRef Feature, FeatureLockLease? Lease);

    private sealed record Mutation<T>(FeatureLockLease? Lease, T Response);
}
