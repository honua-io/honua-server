// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Postgres-backed editing store for the <c>honua.network_datasets</c> registry
/// (#1882 editing surface). Implements list / get / register / update / delete over the
/// registry table provisioned by migration 062 and extended by migration 066. All
/// inputs are validated by <see cref="NetworkDatasetValidation"/> before they reach the
/// database; values are bound as parameters (never interpolated), and the edge/vertex
/// table identifiers are additionally constrained to safe schema-qualified identifiers
/// because the read path interpolates them into the pgRouting edges-SQL string.
/// </summary>
internal sealed class PostgresNetworkDatasetStore : INetworkDatasetStore
{
    private const string SelectColumns =
        "id, name, edge_table, vertex_table, srid, status, topology_version, needs_rebuild, " +
        "description, created_at, updated_at, created_by, updated_by";

    private readonly IDatabaseSessionFactory _sessionFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresNetworkDatasetStore"/> class.
    /// </summary>
    public PostgresNetworkDatasetStore(IDatabaseSessionFactory sessionFactory)
        => _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));

    /// <inheritdoc />
    public async Task<IReadOnlyList<NetworkDatasetRecord>> ListAsync(
        bool includeDisabled = false,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT {SelectColumns}
            FROM honua.network_datasets
            {(includeDisabled ? string.Empty : "WHERE status <> 'disabled'")}
            ORDER BY id;
            """;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<NetworkDatasetRecord>();
        await foreach (var record in session.QueryAsync(sql, MapRecord, parameters: null, cancellationToken)
            .ConfigureAwait(false))
        {
            results.Add(record);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<NetworkDatasetRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var sql = $"""
            SELECT {SelectColumns}
            FROM honua.network_datasets
            WHERE id = @id
            LIMIT 1;
            """;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await session.QuerySingleOrDefaultAsync(
                sql,
                MapRecord,
                new Dictionary<string, object?> { ["id"] = id },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NetworkDatasetRecord> RegisterAsync(
        NetworkDatasetRecord dataset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        const string sql = """
            INSERT INTO honua.network_datasets
                (id, name, edge_table, vertex_table, srid, status, description, created_by, updated_by)
            VALUES
                (@id, @name, @edge_table, @vertex_table, @srid, @status, @description, @created_by, @updated_by)
            ON CONFLICT (id) DO NOTHING;
            """;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await session.ExecuteAsync(
                sql,
                new Dictionary<string, object?>
                {
                    ["id"] = dataset.Id,
                    ["name"] = dataset.Name,
                    ["edge_table"] = dataset.EdgeTable,
                    ["vertex_table"] = dataset.VertexTable,
                    ["srid"] = dataset.Srid,
                    ["status"] = dataset.Status,
                    ["description"] = dataset.Description,
                    ["created_by"] = dataset.CreatedBy,
                    ["updated_by"] = dataset.CreatedBy,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            throw new NetworkDatasetAlreadyExistsException(dataset.Id);
        }

        var saved = await GetAsync(dataset.Id, cancellationToken).ConfigureAwait(false);
        return saved ?? throw new InvalidOperationException(
            $"Network dataset '{dataset.Id}' was registered but could not be read back.");
    }

    /// <inheritdoc />
    public async Task<NetworkDatasetRecord?> UpdateAsync(
        NetworkDatasetRecord dataset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        const string sql = """
            UPDATE honua.network_datasets
            SET name = @name,
                edge_table = @edge_table,
                vertex_table = @vertex_table,
                srid = @srid,
                status = @status,
                description = @description,
                updated_by = @updated_by,
                updated_at = now()
            WHERE id = @id;
            """;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await session.ExecuteAsync(
                sql,
                new Dictionary<string, object?>
                {
                    ["id"] = dataset.Id,
                    ["name"] = dataset.Name,
                    ["edge_table"] = dataset.EdgeTable,
                    ["vertex_table"] = dataset.VertexTable,
                    ["srid"] = dataset.Srid,
                    ["status"] = dataset.Status,
                    ["description"] = dataset.Description,
                    ["updated_by"] = dataset.UpdatedBy,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            return null;
        }

        return await GetAsync(dataset.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        const string sql = "DELETE FROM honua.network_datasets WHERE id = @id;";

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await session.ExecuteAsync(
                sql,
                new Dictionary<string, object?> { ["id"] = id },
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    private static NetworkDatasetRecord MapRecord(IDatabaseRow row) => new(
        row.GetFieldValue<string>(0),
        row.GetFieldValue<string>(1),
        row.GetFieldValue<string>(2),
        row.GetFieldValue<string>(3),
        row.GetFieldValue<int>(4),
        row.GetFieldValue<string>(5),
        row.GetFieldValue<int>(6),
        row.GetFieldValue<bool>(7),
        row.IsNull(8) ? null : row.GetFieldValue<string>(8),
        row.GetFieldValue<DateTimeOffset>(9),
        row.GetFieldValue<DateTimeOffset>(10),
        row.IsNull(11) ? null : row.GetFieldValue<string>(11),
        row.IsNull(12) ? null : row.GetFieldValue<string>(12));
}
