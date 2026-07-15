// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Postgres-backed store for isolated shadow-topology rebuild attempts (#2718) and their
/// multi-node fencing lease (#2720), over the tables provisioned by migration 087. Every
/// mutation that must be fenced (checkpoint write, completion, failure) verifies the
/// caller's fencing token inside a single atomic SQL statement rather than a separate
/// check-then-write round trip, so a stale writer's mutation can never slip in between a
/// verification read and the write it gates.
/// </summary>
internal sealed class PostgresNetworkTopologyRebuildStore : INetworkTopologyRebuildStore
{
    private const string AttemptColumns =
        "dataset_id, generation, attempt, state, operation_id, expected_row_version, expected_source_revision, " +
        "shadow_edge_table, shadow_vertex_table, evidence_digest, failure_code, owner_id, fencing_token, " +
        "lease_expires_at, last_heartbeat_at, created_at, updated_at, completed_at";

    private const string GenerationColumns =
        "dataset_id, generation, source_revision, state, row_version, srid, created_at, updated_at, activated_at, failure_code, " +
        "edge_table, vertex_table";

    private readonly IDatabaseSessionFactory _sessionFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresNetworkTopologyRebuildStore"/> class.
    /// </summary>
    public PostgresNetworkTopologyRebuildStore(IDatabaseSessionFactory sessionFactory)
        => _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));

    /// <inheritdoc />
    public async Task<NetworkTopologyRebuildAttempt> CreateAttemptAsync(
        string datasetId,
        long generation,
        long expectedRowVersion,
        long expectedSourceRevision,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        var current = await transaction.QuerySingleOrDefaultAsync(
                $"""
                SELECT {GenerationColumns}
                FROM honua.network_topology_generations
                WHERE dataset_id = @dataset_id AND generation = @generation
                FOR UPDATE;
                """,
                PostgresNetworkTopologyGenerationStore.MapGeneration,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation },
                cancellationToken)
            .ConfigureAwait(false);

        if (current is null)
        {
            throw new NetworkTopologyRebuildConflictException(
                NetworkTopologyRebuildRejection.GenerationNotFound,
                $"Topology generation {generation} for dataset '{datasetId}' was not found.");
        }

        if (current.SourceRevision != expectedSourceRevision)
        {
            throw new NetworkTopologyRebuildConflictException(
                NetworkTopologyRebuildRejection.StaleSourceRevision,
                $"Generation {generation} source revision has changed since the rebuild was requested; retry with the current revision.");
        }

        if (!NetworkTopologyLifecycle.TryTransition(
                current,
                expectedRowVersion,
                NetworkTopologyGenerationState.Building,
                DateTimeOffset.UtcNow,
                failureCode: null,
                out var updatedGeneration,
                out var transitionFailure))
        {
            throw transitionFailure switch
            {
                NetworkTopologyTransitionFailure.StaleRowVersion => new NetworkTopologyRebuildConflictException(
                    NetworkTopologyRebuildRejection.StaleRowVersion,
                    $"Generation {generation} row version has changed; retry with the current If-Match value."),
                _ => new NetworkTopologyRebuildConflictException(
                    NetworkTopologyRebuildRejection.GenerationNotDirty,
                    $"Generation {generation} is not 'dirty' and cannot start a rebuild."),
            };
        }

        var existingActive = await transaction.QuerySingleOrDefaultAsync<long>(
                """
                SELECT COUNT(*)::bigint FROM honua.network_topology_rebuild_attempts
                WHERE dataset_id = @dataset_id AND generation = @generation AND state = 'building';
                """,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation },
                cancellationToken)
            .ConfigureAwait(false);
        if (existingActive > 0)
        {
            throw new NetworkTopologyRebuildConflictException(
                NetworkTopologyRebuildRejection.AttemptAlreadyActive,
                $"Generation {generation} already has an active rebuild attempt.");
        }

        var affected = await transaction.ExecuteAsync(
                """
                UPDATE honua.network_topology_generations
                SET state = 'building', row_version = @row_version, updated_at = @updated_at
                WHERE dataset_id = @dataset_id AND generation = @generation AND row_version = @expected_row_version;
                """,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["row_version"] = updatedGeneration.RowVersion,
                    ["updated_at"] = updatedGeneration.UpdatedAt,
                    ["expected_row_version"] = expectedRowVersion,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (affected == 0)
        {
            throw new NetworkTopologyRebuildConflictException(
                NetworkTopologyRebuildRejection.StaleRowVersion,
                $"Generation {generation} row version has changed; retry with the current If-Match value.");
        }

        var nextAttempt = await transaction.QuerySingleOrDefaultAsync<long>(
                """
                SELECT COALESCE(MAX(attempt), 0) + 1 FROM honua.network_topology_rebuild_attempts
                WHERE dataset_id = @dataset_id AND generation = @generation;
                """,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation },
                cancellationToken)
            .ConfigureAwait(false);

        var saved = await transaction.QuerySingleOrDefaultAsync(
                $"""
                INSERT INTO honua.network_topology_rebuild_attempts
                    (dataset_id, generation, attempt, state, operation_id, expected_row_version, expected_source_revision,
                     fencing_token, created_at, updated_at)
                VALUES
                    (@dataset_id, @generation, @attempt, 'building', @operation_id, @expected_row_version, @expected_source_revision,
                     0, now(), now())
                RETURNING {AttemptColumns};
                """,
                MapAttempt,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["attempt"] = nextAttempt,
                    ["operation_id"] = operationId,
                    ["expected_row_version"] = expectedRowVersion,
                    ["expected_source_revision"] = expectedSourceRevision,
                },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Rebuild attempt could not be read back after insert.");

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return saved;
    }

    /// <inheritdoc />
    public async Task<NetworkTopologyRebuildAttempt?> GetAttemptAsync(
        string datasetId,
        long generation,
        long attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await session.QuerySingleOrDefaultAsync(
                $"SELECT {AttemptColumns} FROM honua.network_topology_rebuild_attempts WHERE dataset_id=@dataset_id AND generation=@generation AND attempt=@attempt LIMIT 1;",
                MapAttempt,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation, ["attempt"] = attempt },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NetworkTopologyRebuildAttempt?> GetLatestAttemptAsync(
        string datasetId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await session.QuerySingleOrDefaultAsync(
                $"""
                SELECT {AttemptColumns} FROM honua.network_topology_rebuild_attempts
                WHERE dataset_id=@dataset_id AND generation=@generation
                ORDER BY attempt DESC LIMIT 1;
                """,
                MapAttempt,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NetworkTopologyRebuildAttempt>> ListExpiredLeasesAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<NetworkTopologyRebuildAttempt>();
        await foreach (var attempt in session.QueryAsync(
                $"""
                SELECT {AttemptColumns} FROM honua.network_topology_rebuild_attempts
                WHERE state = 'building' AND lease_expires_at IS NOT NULL AND lease_expires_at < @as_of;
                """,
                MapAttempt,
                new Dictionary<string, object?> { ["as_of"] = asOf },
                cancellationToken)
            .ConfigureAwait(false))
        {
            results.Add(attempt);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<NetworkTopologyRebuildAttempt?> TryAcquireOrTakeoverLeaseAsync(
        string datasetId,
        long generation,
        long attempt,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var now = DateTimeOffset.UtcNow;
        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await session.QuerySingleOrDefaultAsync(
                $"""
                UPDATE honua.network_topology_rebuild_attempts
                SET owner_id = @owner_id,
                    fencing_token = fencing_token + 1,
                    lease_expires_at = @lease_expires_at,
                    last_heartbeat_at = @now,
                    updated_at = @now
                WHERE dataset_id = @dataset_id AND generation = @generation AND attempt = @attempt
                  AND state = 'building'
                  AND (owner_id IS NULL OR lease_expires_at IS NULL OR lease_expires_at < @now)
                RETURNING {AttemptColumns};
                """,
                MapAttempt,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["attempt"] = attempt,
                    ["owner_id"] = ownerId,
                    ["lease_expires_at"] = now.Add(leaseDuration),
                    ["now"] = now,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryHeartbeatAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        var now = DateTimeOffset.UtcNow;
        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await session.ExecuteAsync(
                """
                UPDATE honua.network_topology_rebuild_attempts
                SET lease_expires_at = @lease_expires_at, last_heartbeat_at = @now, updated_at = @now
                WHERE dataset_id = @dataset_id AND generation = @generation AND attempt = @attempt
                  AND state = 'building' AND fencing_token = @fencing_token;
                """,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["attempt"] = attempt,
                    ["fencing_token"] = fencingToken,
                    ["lease_expires_at"] = now.Add(leaseDuration),
                    ["now"] = now,
                },
                cancellationToken)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> TryWriteCheckpointAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        NetworkTopologyRebuildStage stage,
        NetworkTopologyRebuildCheckpointStatus status,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await session.ExecuteAsync(
                """
                INSERT INTO honua.network_topology_rebuild_checkpoints (dataset_id, generation, attempt, stage, status, detail, updated_at)
                SELECT @dataset_id, @generation, @attempt, @stage, @status, @detail, now()
                WHERE EXISTS (
                    SELECT 1 FROM honua.network_topology_rebuild_attempts
                    WHERE dataset_id = @dataset_id AND generation = @generation AND attempt = @attempt
                      AND state = 'building' AND fencing_token = @fencing_token)
                ON CONFLICT (dataset_id, generation, attempt, stage)
                DO UPDATE SET status = EXCLUDED.status, detail = EXCLUDED.detail, updated_at = EXCLUDED.updated_at;
                """,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["attempt"] = attempt,
                    ["fencing_token"] = fencingToken,
                    ["stage"] = FormatStage(stage),
                    ["status"] = FormatCheckpointStatus(status),
                    ["detail"] = detail,
                },
                cancellationToken)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NetworkTopologyRebuildCheckpoint>> ListCheckpointsAsync(
        string datasetId,
        long generation,
        long attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<NetworkTopologyRebuildCheckpoint>();
        await foreach (var checkpoint in session.QueryAsync(
                """
                SELECT dataset_id, generation, attempt, stage, status, detail, updated_at
                FROM honua.network_topology_rebuild_checkpoints
                WHERE dataset_id = @dataset_id AND generation = @generation AND attempt = @attempt
                ORDER BY
                    CASE stage
                        WHEN 'snapshot' THEN 1 WHEN 'build' THEN 2 WHEN 'analyze' THEN 3
                        WHEN 'validate' THEN 4 WHEN 'cleanup' THEN 5 ELSE 6
                    END;
                """,
                MapCheckpoint,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation, ["attempt"] = attempt },
                cancellationToken)
            .ConfigureAwait(false))
        {
            results.Add(checkpoint);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<bool> TryCompleteAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        string shadowEdgeTable,
        string shadowVertexTable,
        string evidenceDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(shadowEdgeTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(shadowVertexTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceDigest);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        var attemptAffected = await transaction.ExecuteAsync(
                """
                UPDATE honua.network_topology_rebuild_attempts
                SET state = 'ready', shadow_edge_table = @edge_table, shadow_vertex_table = @vertex_table,
                    evidence_digest = @digest, updated_at = now(), completed_at = now()
                WHERE dataset_id = @dataset_id AND generation = @generation AND attempt = @attempt
                  AND state = 'building' AND fencing_token = @fencing_token;
                """,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["attempt"] = attempt,
                    ["fencing_token"] = fencingToken,
                    ["edge_table"] = shadowEdgeTable,
                    ["vertex_table"] = shadowVertexTable,
                    ["digest"] = evidenceDigest,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (attemptAffected == 0)
        {
            return false;
        }

        if (!await TryTransitionGenerationAsync(
                transaction, datasetId, generation, NetworkTopologyGenerationState.Ready, cancellationToken,
                failureCode: null, edgeTable: shadowEdgeTable, vertexTable: shadowVertexTable)
                .ConfigureAwait(false))
        {
            return false;
        }

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> TryFailAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        var attemptAffected = await transaction.ExecuteAsync(
                """
                UPDATE honua.network_topology_rebuild_attempts
                SET state = 'failed', failure_code = @failure_code, updated_at = now(), completed_at = now()
                WHERE dataset_id = @dataset_id AND generation = @generation AND attempt = @attempt
                  AND state = 'building' AND fencing_token = @fencing_token;
                """,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["attempt"] = attempt,
                    ["fencing_token"] = fencingToken,
                    ["failure_code"] = failureCode,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (attemptAffected == 0)
        {
            return false;
        }

        if (!await TryTransitionGenerationAsync(
                transaction, datasetId, generation, NetworkTopologyGenerationState.Failed, cancellationToken, failureCode)
                .ConfigureAwait(false))
        {
            return false;
        }

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task CleanupOrphanShadowArtifactsAsync(
        string datasetId,
        long generation,
        long? keepAttempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var orphans = new List<(string EdgeTable, string VertexTable)>();
        await foreach (var row in session.QueryAsync(
                """
                SELECT shadow_edge_table, shadow_vertex_table FROM honua.network_topology_rebuild_attempts
                WHERE dataset_id = @dataset_id AND generation = @generation
                  AND (@keep_attempt::bigint IS NULL OR attempt <> @keep_attempt)
                  AND shadow_edge_table IS NOT NULL AND shadow_vertex_table IS NOT NULL;
                """,
                static row => (row.GetFieldValue<string>(0), row.GetFieldValue<string>(1)),
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation, ["keep_attempt"] = keepAttempt },
                cancellationToken)
            .ConfigureAwait(false))
        {
            orphans.Add(row);
        }

        foreach (var (edgeTable, vertexTable) in orphans)
        {
            if (!NetworkDatasetValidation.IsValidTableIdentifier(edgeTable) || !NetworkDatasetValidation.IsValidTableIdentifier(vertexTable))
            {
                continue;
            }

            await session.ExecuteAsync($"DROP TABLE IF EXISTS {edgeTable};", parameters: null, cancellationToken).ConfigureAwait(false);
            await session.ExecuteAsync($"DROP TABLE IF EXISTS {vertexTable};", parameters: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> TryTransitionGenerationAsync(
        IDatabaseSession transaction,
        string datasetId,
        long generation,
        NetworkTopologyGenerationState target,
        CancellationToken cancellationToken,
        string? failureCode = null,
        string? edgeTable = null,
        string? vertexTable = null)
    {
        var current = await transaction.QuerySingleOrDefaultAsync(
                $"""
                SELECT {GenerationColumns} FROM honua.network_topology_generations
                WHERE dataset_id = @dataset_id AND generation = @generation FOR UPDATE;
                """,
                PostgresNetworkTopologyGenerationStore.MapGeneration,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation },
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return false;
        }

        if (!NetworkTopologyLifecycle.TryTransition(
                current, current.RowVersion, target, DateTimeOffset.UtcNow, failureCode, out var updated, out _))
        {
            return false;
        }

        // Ready-transitioning generations stamp the shadow (now attempt-ready) edge/vertex
        // table names onto the generation row itself, so promotion (#2719) and a later
        // rollback need only copy the target generation's own columns into
        // honua.network_datasets rather than re-deriving them from rebuild-attempt history.
        var affected = await transaction.ExecuteAsync(
                """
                UPDATE honua.network_topology_generations
                SET state = @state, row_version = @row_version, updated_at = @updated_at, failure_code = @failure_code,
                    edge_table = COALESCE(@edge_table, edge_table),
                    vertex_table = COALESCE(@vertex_table, vertex_table)
                WHERE dataset_id = @dataset_id AND generation = @generation AND row_version = @expected_row_version;
                """,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["state"] = NetworkTopologyGenerationStateFormat.Format(updated.State),
                    ["row_version"] = updated.RowVersion,
                    ["updated_at"] = updated.UpdatedAt,
                    ["failure_code"] = updated.FailureCode,
                    ["edge_table"] = edgeTable,
                    ["vertex_table"] = vertexTable,
                    ["expected_row_version"] = current.RowVersion,
                },
                cancellationToken)
            .ConfigureAwait(false);
        return affected > 0;
    }

    private static string FormatStage(NetworkTopologyRebuildStage stage) => stage switch
    {
        NetworkTopologyRebuildStage.Snapshot => "snapshot",
        NetworkTopologyRebuildStage.Build => "build",
        NetworkTopologyRebuildStage.Analyze => "analyze",
        NetworkTopologyRebuildStage.Validate => "validate",
        NetworkTopologyRebuildStage.Cleanup => "cleanup",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unrecognised rebuild stage."),
    };

    private static NetworkTopologyRebuildStage ParseStage(string value) => value switch
    {
        "snapshot" => NetworkTopologyRebuildStage.Snapshot,
        "build" => NetworkTopologyRebuildStage.Build,
        "analyze" => NetworkTopologyRebuildStage.Analyze,
        "validate" => NetworkTopologyRebuildStage.Validate,
        "cleanup" => NetworkTopologyRebuildStage.Cleanup,
        _ => throw new InvalidOperationException($"Unrecognised rebuild stage '{value}'."),
    };

    private static string FormatCheckpointStatus(NetworkTopologyRebuildCheckpointStatus status) => status switch
    {
        NetworkTopologyRebuildCheckpointStatus.Pending => "pending",
        NetworkTopologyRebuildCheckpointStatus.InProgress => "in_progress",
        NetworkTopologyRebuildCheckpointStatus.Completed => "completed",
        NetworkTopologyRebuildCheckpointStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unrecognised checkpoint status."),
    };

    private static NetworkTopologyRebuildCheckpointStatus ParseCheckpointStatus(string value) => value switch
    {
        "pending" => NetworkTopologyRebuildCheckpointStatus.Pending,
        "in_progress" => NetworkTopologyRebuildCheckpointStatus.InProgress,
        "completed" => NetworkTopologyRebuildCheckpointStatus.Completed,
        "failed" => NetworkTopologyRebuildCheckpointStatus.Failed,
        _ => throw new InvalidOperationException($"Unrecognised rebuild checkpoint status '{value}'."),
    };

    private static NetworkTopologyRebuildAttemptState ParseAttemptState(string value) => value switch
    {
        "building" => NetworkTopologyRebuildAttemptState.Building,
        "ready" => NetworkTopologyRebuildAttemptState.Ready,
        "failed" => NetworkTopologyRebuildAttemptState.Failed,
        _ => throw new InvalidOperationException($"Unrecognised rebuild attempt state '{value}'."),
    };

    private static NetworkTopologyRebuildAttempt MapAttempt(IDatabaseRow row) => new(
        row.GetFieldValue<string>(0),
        row.GetFieldValue<long>(1),
        row.GetFieldValue<long>(2),
        ParseAttemptState(row.GetFieldValue<string>(3)),
        row.GetFieldValue<string>(4),
        row.GetFieldValue<long>(5),
        row.GetFieldValue<long>(6),
        row.IsNull(7) ? null : row.GetFieldValue<string>(7),
        row.IsNull(8) ? null : row.GetFieldValue<string>(8),
        row.IsNull(9) ? null : row.GetFieldValue<string>(9),
        row.IsNull(10) ? null : row.GetFieldValue<string>(10),
        row.IsNull(11) ? null : row.GetFieldValue<string>(11),
        row.GetFieldValue<long>(12),
        row.IsNull(13) ? null : row.GetFieldValue<DateTimeOffset>(13),
        row.IsNull(14) ? null : row.GetFieldValue<DateTimeOffset>(14),
        row.GetFieldValue<DateTimeOffset>(15),
        row.GetFieldValue<DateTimeOffset>(16),
        row.IsNull(17) ? null : row.GetFieldValue<DateTimeOffset>(17));

    private static NetworkTopologyRebuildCheckpoint MapCheckpoint(IDatabaseRow row) => new(
        row.GetFieldValue<string>(0),
        row.GetFieldValue<long>(1),
        row.GetFieldValue<long>(2),
        ParseStage(row.GetFieldValue<string>(3)),
        ParseCheckpointStatus(row.GetFieldValue<string>(4)),
        row.IsNull(5) ? null : row.GetFieldValue<string>(5),
        row.GetFieldValue<DateTimeOffset>(6));
}
