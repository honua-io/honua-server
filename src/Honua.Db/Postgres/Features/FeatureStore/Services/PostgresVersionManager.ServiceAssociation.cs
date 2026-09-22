// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresVersionManager
{
    /// <inheritdoc />
    public async Task<VersionServiceAssociationResult> AssociateLegacyVersionAsync(
        Guid versionId, string serviceId, string expectedOwner, IReadOnlyList<int> permittedStorageLayers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwner);
        ArgumentNullException.ThrowIfNull(permittedStorageLayers);
        // Adoption and queued maintenance use the same lock. A job that starts afterward must
        // recheck the persisted association under this lock before changing branch or DEFAULT data.
        await using var lockHandle = await AcquireVersionLockAsync(versionId, cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // FOR UPDATE also excludes concurrent new FK references from version_edits during validation.
        // The normal overlay mutation only updates values of existing keys; it never moves their layer.
        await using var select = new NpgsqlCommand("""
            SELECT owner, state, service_id FROM honua.gdb_versions WHERE version_id = @id FOR UPDATE
            """, connection, transaction);
        select.Parameters.AddWithValue("id", versionId);
        string? existingService;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return VersionServiceAssociationResult.Missing;
            }
            if (!string.Equals(reader.GetString(0), expectedOwner, StringComparison.Ordinal)
                || reader.GetInt16(1) != (short)VersionState.Active)
            {
                return VersionServiceAssociationResult.Conflict;
            }
            existingService = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        if (existingService is not null && !string.Equals(existingService, serviceId, StringComparison.Ordinal))
        {
            return VersionServiceAssociationResult.Conflict;
        }
        await using var parent = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM honua.gdb_versions child
                JOIN honua.gdb_versions ancestor ON ancestor.version_id = child.parent_version
                WHERE child.version_id = @id AND ancestor.service_id IS DISTINCT FROM @service)
            """, connection, transaction);
        parent.Parameters.AddWithValue("id", versionId);
        parent.Parameters.AddWithValue("service", serviceId);
        if ((bool)(await parent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            return VersionServiceAssociationResult.Conflict;
        }
        await using var affected = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM honua.version_edits
                WHERE version_id = @id AND NOT (layer_id = ANY(@layers)))
            """, connection, transaction);
        affected.Parameters.AddWithValue("id", versionId);
        affected.Parameters.AddWithValue("layers", permittedStorageLayers.ToArray());
        if ((bool)(await affected.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            return VersionServiceAssociationResult.Conflict;
        }
        await using var update = new NpgsqlCommand("""
            UPDATE honua.gdb_versions SET service_id = @service WHERE version_id = @id
            """, connection, transaction);
        update.Parameters.AddWithValue("id", versionId);
        update.Parameters.AddWithValue("service", serviceId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return VersionServiceAssociationResult.Associated;
    }
}
