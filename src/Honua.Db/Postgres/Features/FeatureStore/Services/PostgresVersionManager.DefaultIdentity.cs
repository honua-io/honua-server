// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresVersionManager
{
    /// <inheritdoc />
    public async Task<DefaultVersionIdentity?> GetDefaultVersionIdentityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT version_id, created_at
            FROM honua.gdb_version_store_identity
            WHERE singleton
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The managed version store's durable DEFAULT identity is missing. Apply the required database migration.");
        }

        var identity = new DefaultVersionIdentity
        {
            VersionId = reader.GetGuid(0),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(1)
        };
        if (identity.VersionId == Guid.Empty || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The managed version store's durable DEFAULT identity is invalid.");
        }

        return identity;
    }

    private async Task<bool> HasDefaultNameCollisionAsync(string requestedName, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM honua.gdb_versions
                WHERE state <> 3 AND LOWER(CASE WHEN @qualified THEN owner || '.' || version_name ELSE version_name END) = LOWER(@name))
            """, connection);
        command.Parameters.AddWithValue("qualified", requestedName.Contains('.'));
        command.Parameters.AddWithValue("name", requestedName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    private async Task EnsureBranchTargetAsync(Guid versionId, CancellationToken cancellationToken)
    {
        if (versionId == Guid.Empty)
        {
            throw new ArgumentException("A non-nil branch version identity is required.", nameof(versionId));
        }

        var identity = await GetDefaultVersionIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identity?.VersionId == versionId)
        {
            throw new ArgumentException("DEFAULT is a system-managed base-store identity and is not a mutable branch target.", nameof(versionId));
        }
    }

    private static void RejectReservedVersionName(string name)
    {
        if (DefaultVersionIdentity.IsDefaultName(name))
        {
            throw new ArgumentException("The canonical DEFAULT version name is reserved for the base store.", nameof(name));
        }
    }
}
