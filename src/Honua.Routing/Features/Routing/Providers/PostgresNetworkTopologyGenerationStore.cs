// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Postgres-backed read/allocation store for topology generations (#2716). Reads from the
/// <c>honua.network_topology_generations</c> table provisioned by migration 084 and adds
/// draft-generation allocation. Never changes a dataset's active generation pointer.
/// </summary>
internal sealed class PostgresNetworkTopologyGenerationStore : INetworkTopologyGenerationStore
{
    private const string SelectColumns =
        "dataset_id, generation, source_revision, state, row_version, srid, " +
        "created_at, updated_at, activated_at, failure_code, edge_table, vertex_table";

    private readonly IDatabaseSessionFactory _sessionFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresNetworkTopologyGenerationStore"/> class.
    /// </summary>
    public PostgresNetworkTopologyGenerationStore(IDatabaseSessionFactory sessionFactory)
        => _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));

    /// <inheritdoc />
    public async Task<IReadOnlyList<NetworkTopologyGeneration>> ListAsync(
        string datasetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        var sql = $"""
            SELECT {SelectColumns}
            FROM honua.network_topology_generations
            WHERE dataset_id = @dataset_id
            ORDER BY generation DESC;
            """;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<NetworkTopologyGeneration>();
        await foreach (var record in session.QueryAsync(
                sql,
                MapGeneration,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId },
                cancellationToken)
            .ConfigureAwait(false))
        {
            results.Add(record);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<NetworkTopologyGeneration?> GetAsync(
        string datasetId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        var sql = $"""
            SELECT {SelectColumns}
            FROM honua.network_topology_generations
            WHERE dataset_id = @dataset_id AND generation = @generation
            LIMIT 1;
            """;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await session.QuerySingleOrDefaultAsync(
                sql,
                MapGeneration,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NetworkTopologyGeneration> AllocateDraftAsync(
        string datasetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await session.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken)
            .ConfigureAwait(false);

        // Transaction-scoped advisory lock keyed by dataset id serializes concurrent
        // allocation for the same dataset so two simultaneous callers cannot compute the
        // same "next generation number" and collide on the primary key.
        await transaction.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(hashtext(@dataset_id));",
                new Dictionary<string, object?> { ["dataset_id"] = datasetId },
                cancellationToken)
            .ConfigureAwait(false);

        var active = await transaction.QuerySingleOrDefaultAsync(
                """
                SELECT source_revision, edge_table, vertex_table, srid
                FROM honua.network_topology_generations
                WHERE dataset_id = @dataset_id AND state = 'active'
                LIMIT 1;
                """,
                static row => new ActiveGenerationSeed(
                    row.GetFieldValue<long>(0),
                    row.GetFieldValue<string>(1),
                    row.GetFieldValue<string>(2),
                    row.GetFieldValue<int>(3)),
                new Dictionary<string, object?> { ["dataset_id"] = datasetId },
                cancellationToken)
            .ConfigureAwait(false);

        if (active is null)
        {
            throw new NetworkTopologyActiveGenerationMissingException(datasetId);
        }

        var nextGeneration = await transaction.QuerySingleOrDefaultAsync<long>(
                """
                SELECT COALESCE(MAX(generation), 0) + 1
                FROM honua.network_topology_generations
                WHERE dataset_id = @dataset_id;
                """,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId },
                cancellationToken)
            .ConfigureAwait(false);

        var saved = await transaction.QuerySingleOrDefaultAsync(
                $"""
                INSERT INTO honua.network_topology_generations
                    (dataset_id, generation, source_revision, state, row_version,
                     edge_table, vertex_table, srid, created_at, updated_at)
                VALUES
                    (@dataset_id, @generation, @source_revision, 'draft', 1,
                     @edge_table, @vertex_table, @srid, now(), now())
                RETURNING {SelectColumns};
                """,
                MapGeneration,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = nextGeneration,
                    ["source_revision"] = active.SourceRevision,
                    ["edge_table"] = active.EdgeTable,
                    ["vertex_table"] = active.VertexTable,
                    ["srid"] = active.Srid,
                },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Draft topology generation allocation could not be read back.");

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return saved;
    }

    private sealed record ActiveGenerationSeed(long SourceRevision, string EdgeTable, string VertexTable, int Srid);

    internal static NetworkTopologyGeneration MapGeneration(IDatabaseRow row) => new(
        row.GetFieldValue<string>(0),
        row.GetFieldValue<long>(1),
        row.GetFieldValue<long>(2),
        NetworkTopologyGenerationStateFormat.Parse(row.GetFieldValue<string>(3)),
        row.GetFieldValue<long>(4),
        row.GetFieldValue<int>(5),
        row.GetFieldValue<DateTimeOffset>(6),
        row.GetFieldValue<DateTimeOffset>(7),
        row.IsNull(8) ? null : row.GetFieldValue<DateTimeOffset>(8),
        row.IsNull(9) ? null : row.GetFieldValue<string>(9))
    {
        EdgeTable = row.IsNull(10) ? null : row.GetFieldValue<string>(10),
        VertexTable = row.IsNull(11) ? null : row.GetFieldValue<string>(11),
    };
}

/// <summary>
/// Converts between the provider-neutral <see cref="NetworkTopologyGenerationState"/> enum
/// and the lowercase text values stored in <c>honua.network_topology_generations.state</c>
/// (constrained by migration 084's <c>CHECK</c>).
/// </summary>
internal static class NetworkTopologyGenerationStateFormat
{
    /// <summary>Formats a state as its lowercase storage/wire representation.</summary>
    public static string Format(NetworkTopologyGenerationState state) => state switch
    {
        NetworkTopologyGenerationState.Draft => "draft",
        NetworkTopologyGenerationState.Dirty => "dirty",
        NetworkTopologyGenerationState.Building => "building",
        NetworkTopologyGenerationState.Ready => "ready",
        NetworkTopologyGenerationState.Active => "active",
        NetworkTopologyGenerationState.Failed => "failed",
        NetworkTopologyGenerationState.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unrecognised topology generation state."),
    };

    /// <summary>Parses the lowercase storage/wire representation back to the enum.</summary>
    public static NetworkTopologyGenerationState Parse(string value) => value switch
    {
        "draft" => NetworkTopologyGenerationState.Draft,
        "dirty" => NetworkTopologyGenerationState.Dirty,
        "building" => NetworkTopologyGenerationState.Building,
        "ready" => NetworkTopologyGenerationState.Ready,
        "active" => NetworkTopologyGenerationState.Active,
        "failed" => NetworkTopologyGenerationState.Failed,
        "retired" => NetworkTopologyGenerationState.Retired,
        _ => throw new InvalidOperationException($"Unrecognised topology generation state '{value}'."),
    };
}
