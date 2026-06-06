// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Postgres-backed branch-version registry that maps a service's DEFAULT storage layer id
/// to per-version synthetic branch storage layer ids for gdbVersion routing.
/// </summary>
internal sealed class PostgresBranchVersionStore : IBranchVersionStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresBranchVersionStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<BranchVersion> CreateVersionAsync(
        string serviceId,
        string versionName,
        int baseLayerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);
        if (IBranchVersionStore.IsDefaultVersion(versionName))
        {
            throw new ArgumentException("DEFAULT is a reserved branch version name.", nameof(versionName));
        }

        // Idempotent create: ON CONFLICT DO NOTHING preserves the existing branch_layer_id,
        // then a follow-up SELECT returns the persisted row whether it was just inserted or
        // already present. The branch_layer_id column defaults from the dedicated sequence.
        const string insertSql = """
            INSERT INTO honua.gdb_versions (service_id, version_name, version_name_lower, base_layer_id)
            VALUES ($1, $2, LOWER($2), $3)
            ON CONFLICT (service_id, version_name_lower, base_layer_id) DO NOTHING
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand(insertSql, connection))
        {
            insert.Parameters.AddWithValue(NpgsqlDbType.Text, serviceId);
            insert.Parameters.AddWithValue(NpgsqlDbType.Text, versionName);
            insert.Parameters.AddWithValue(NpgsqlDbType.Integer, baseLayerId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var version = await ReadVersionAsync(connection, serviceId, versionName, baseLayerId, cancellationToken).ConfigureAwait(false);
        return version ?? throw new InvalidOperationException(
            $"Branch version '{versionName}' for service '{serviceId}' could not be persisted.");
    }

    public async Task<BranchVersion?> GetVersionAsync(
        string serviceId,
        string versionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadVersionAsync(connection, serviceId, versionName, baseLayerId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BranchVersion>> ListVersionsAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        const string sql = """
            SELECT service_id, version_name, base_layer_id, branch_layer_id, created_at
            FROM honua.gdb_versions
            WHERE service_id = $1
            ORDER BY created_at ASC, version_name_lower ASC
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, serviceId);

        var results = new List<BranchVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapVersion(reader));
        }

        return results;
    }

    public async Task<int?> ResolveBranchLayerIdAsync(
        string serviceId,
        string? versionName,
        int baseLayerId,
        CancellationToken cancellationToken = default)
    {
        if (IBranchVersionStore.IsDefaultVersion(versionName))
        {
            return baseLayerId;
        }

        const string sql = """
            SELECT branch_layer_id
            FROM honua.gdb_versions
            WHERE service_id = $1 AND version_name_lower = LOWER($2) AND base_layer_id = $3
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, serviceId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, versionName!);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, baseLayerId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is int branchLayerId ? branchLayerId : null;
    }

    private static async Task<BranchVersion?> ReadVersionAsync(
        NpgsqlConnection connection,
        string serviceId,
        string versionName,
        int? baseLayerId,
        CancellationToken cancellationToken)
    {
        var sql = baseLayerId.HasValue
            ? """
              SELECT service_id, version_name, base_layer_id, branch_layer_id, created_at
              FROM honua.gdb_versions
              WHERE service_id = $1 AND version_name_lower = LOWER($2) AND base_layer_id = $3
              """
            : """
              SELECT service_id, version_name, base_layer_id, branch_layer_id, created_at
              FROM honua.gdb_versions
              WHERE service_id = $1 AND version_name_lower = LOWER($2)
              ORDER BY created_at ASC
              LIMIT 1
              """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, serviceId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, versionName);
        if (baseLayerId.HasValue)
        {
            command.Parameters.AddWithValue(NpgsqlDbType.Integer, baseLayerId.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapVersion(reader);
    }

    private static BranchVersion MapVersion(NpgsqlDataReader reader) => new()
    {
        ServiceId = reader.GetString(0),
        VersionName = reader.GetString(1),
        BaseLayerId = reader.GetInt32(2),
        BranchLayerId = reader.GetInt32(3),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(4)
    };
}
