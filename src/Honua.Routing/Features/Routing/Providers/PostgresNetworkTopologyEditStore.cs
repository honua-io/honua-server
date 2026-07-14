// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Postgres-backed canonical edit store for batched, transactional edge and
/// turn-restriction content mutations (#2716). Every mutation runs inside one
/// all-or-nothing transaction over the staging tables provisioned by migration 086:
/// <c>honua.network_topology_edge_edits</c>, <c>honua.network_topology_restriction_edits</c>,
/// and the <c>honua.network_topology_edit_idempotency</c> at-most-once ledger.
/// </summary>
/// <remarks>
/// This store deliberately never catches provider-specific SQL exceptions (Honua.Routing
/// has no direct Npgsql dependency by design; see <c>Honua.Routing.csproj</c>). Conflict
/// detection instead uses <c>ON CONFLICT ... DO NOTHING</c> plus affected-row-count checks
/// (mirroring <see cref="PostgresNetworkDatasetStore"/>), and turn-restriction edge
/// references are validated with an explicit pre-check query rather than relying on the
/// foreign-key constraints raising a catchable error. The foreign keys and CHECK
/// constraints in migration 086 remain as a database-enforced safety net in case a future
/// code path bypasses this store.
/// </remarks>
internal sealed class PostgresNetworkTopologyEditStore : INetworkTopologyEditStore
{
    private const string GenerationColumns =
        "dataset_id, generation, source_revision, state, row_version, srid, " +
        "created_at, updated_at, activated_at, failure_code";

    private readonly IDatabaseSessionFactory _sessionFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresNetworkTopologyEditStore"/> class.
    /// </summary>
    public PostgresNetworkTopologyEditStore(IDatabaseSessionFactory sessionFactory)
        => _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));

    /// <inheritdoc />
    public async Task<NetworkTopologyEditResult> ApplyEditBatchAsync(
        string datasetId,
        long generation,
        long expectedRowVersion,
        string idempotencyKey,
        string contentHash,
        NetworkTopologyEditBatch batch,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        using var activity = RoutingTelemetry.Source.StartActivity("network_topology.edit_batch", ActivityKind.Internal);
        activity?.SetTag("honua.routing.dataset_id", datasetId);
        activity?.SetTag("honua.routing.generation", generation);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await session.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken)
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
            throw new NetworkTopologyGenerationNotFoundException(datasetId, generation);
        }

        var replay = await TryGetIdempotentReplayAsync(
                transaction,
                datasetId,
                generation,
                idempotencyKey,
                contentHash,
                cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            activity?.SetTag("honua.routing.idempotent_replay", true);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return replay;
        }

        if (!NetworkTopologyLifecycle.TryApplyContentEdit(
                current,
                expectedRowVersion,
                DateTimeOffset.UtcNow,
                out var updatedGeneration,
                out var rejection))
        {
            throw rejection switch
            {
                NetworkTopologyEditRejection.StaleRowVersion => new NetworkTopologyEditConflictException(
                    NetworkTopologyEditConflictReason.StaleRowVersion,
                    $"Generation {generation} row version has changed; retry with the current If-Match value."),
                NetworkTopologyEditRejection.GenerationNotEditable => new NetworkTopologyEditConflictException(
                    NetworkTopologyEditConflictReason.GenerationNotEditable,
                    $"Generation {generation} is '{FormatState(current.State)}' and does not accept content edits."),
                _ => new InvalidOperationException("Unrecognised topology edit rejection reason."),
            };
        }

        var counts = await ApplyContentAsync(transaction, datasetId, generation, batch, actor, cancellationToken)
            .ConfigureAwait(false);

        var applied = await transaction.QuerySingleOrDefaultAsync(
                $"""
                UPDATE honua.network_topology_generations
                SET state = @state,
                    source_revision = @source_revision,
                    row_version = @row_version,
                    updated_at = @updated_at
                WHERE dataset_id = @dataset_id AND generation = @generation AND row_version = @expected_row_version
                RETURNING {GenerationColumns};
                """,
                PostgresNetworkTopologyGenerationStore.MapGeneration,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["state"] = FormatState(updatedGeneration.State),
                    ["source_revision"] = updatedGeneration.SourceRevision,
                    ["row_version"] = updatedGeneration.RowVersion,
                    ["updated_at"] = updatedGeneration.UpdatedAt,
                    ["expected_row_version"] = expectedRowVersion,
                },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Topology generation content-edit compare-and-swap could not be applied after passing validation.");

        var result = new NetworkTopologyEditResult(
            datasetId,
            generation,
            applied.SourceRevision,
            applied.RowVersion,
            applied.State,
            counts.EdgesAdded,
            counts.EdgesUpdated,
            counts.EdgesDeleted,
            counts.RestrictionsAdded,
            counts.RestrictionsUpdated,
            counts.RestrictionsDeleted,
            WasIdempotentReplay: false);

        await RecordIdempotencyAsync(transaction, datasetId, generation, idempotencyKey, contentHash, result, cancellationToken)
            .ConfigureAwait(false);

        activity?.SetTag("honua.routing.edges_added", counts.EdgesAdded);
        activity?.SetTag("honua.routing.edges_updated", counts.EdgesUpdated);
        activity?.SetTag("honua.routing.edges_deleted", counts.EdgesDeleted);
        activity?.SetTag("honua.routing.restrictions_added", counts.RestrictionsAdded);
        activity?.SetTag("honua.routing.restrictions_updated", counts.RestrictionsUpdated);
        activity?.SetTag("honua.routing.restrictions_deleted", counts.RestrictionsDeleted);

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private static async Task<NetworkTopologyEditResult?> TryGetIdempotentReplayAsync(
        IDatabaseSession transaction,
        string datasetId,
        long generation,
        string idempotencyKey,
        string contentHash,
        CancellationToken cancellationToken)
    {
        var existing = await transaction.QuerySingleOrDefaultAsync(
                """
                SELECT content_hash, result_state, result_row_version, result_source_revision,
                       edges_added, edges_updated, edges_deleted,
                       restrictions_added, restrictions_updated, restrictions_deleted
                FROM honua.network_topology_edit_idempotency
                WHERE dataset_id = @dataset_id AND generation = @generation AND idempotency_key = @idempotency_key
                FOR UPDATE;
                """,
                static row => new IdempotencyRecord(
                    row.GetFieldValue<string>(0),
                    row.GetFieldValue<string>(1),
                    row.GetFieldValue<long>(2),
                    row.GetFieldValue<long>(3),
                    row.GetFieldValue<int>(4),
                    row.GetFieldValue<int>(5),
                    row.GetFieldValue<int>(6),
                    row.GetFieldValue<int>(7),
                    row.GetFieldValue<int>(8),
                    row.GetFieldValue<int>(9)),
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["idempotency_key"] = idempotencyKey,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return null;
        }

        if (!string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal))
        {
            throw new NetworkTopologyEditConflictException(
                NetworkTopologyEditConflictReason.IdempotencyKeyReused,
                $"Idempotency key '{idempotencyKey}' was already used for generation {generation} with a different request payload.");
        }

        return new NetworkTopologyEditResult(
            datasetId,
            generation,
            existing.ResultSourceRevision,
            existing.ResultRowVersion,
            NetworkTopologyGenerationStateFormat.Parse(existing.ResultState),
            existing.EdgesAdded,
            existing.EdgesUpdated,
            existing.EdgesDeleted,
            existing.RestrictionsAdded,
            existing.RestrictionsUpdated,
            existing.RestrictionsDeleted,
            WasIdempotentReplay: true);
    }

    private static async Task RecordIdempotencyAsync(
        IDatabaseSession transaction,
        string datasetId,
        long generation,
        string idempotencyKey,
        string contentHash,
        NetworkTopologyEditResult result,
        CancellationToken cancellationToken)
    {
        await transaction.ExecuteAsync(
                """
                INSERT INTO honua.network_topology_edit_idempotency
                    (dataset_id, generation, idempotency_key, content_hash, result_state,
                     result_row_version, result_source_revision, edges_added, edges_updated,
                     edges_deleted, restrictions_added, restrictions_updated, restrictions_deleted)
                VALUES
                    (@dataset_id, @generation, @idempotency_key, @content_hash, @result_state,
                     @result_row_version, @result_source_revision, @edges_added, @edges_updated,
                     @edges_deleted, @restrictions_added, @restrictions_updated, @restrictions_deleted);
                """,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["idempotency_key"] = idempotencyKey,
                    ["content_hash"] = contentHash,
                    ["result_state"] = FormatState(result.State),
                    ["result_row_version"] = result.RowVersion,
                    ["result_source_revision"] = result.SourceRevision,
                    ["edges_added"] = result.EdgesAdded,
                    ["edges_updated"] = result.EdgesUpdated,
                    ["edges_deleted"] = result.EdgesDeleted,
                    ["restrictions_added"] = result.RestrictionsAdded,
                    ["restrictions_updated"] = result.RestrictionsUpdated,
                    ["restrictions_deleted"] = result.RestrictionsDeleted,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ContentCounts> ApplyContentAsync(
        IDatabaseSession transaction,
        string datasetId,
        long generation,
        NetworkTopologyEditBatch batch,
        string actor,
        CancellationToken cancellationToken)
    {
        // Ordering matters: restrictions are deleted before the edges they may reference,
        // edges are mutated before restrictions that may reference newly added edges, and
        // deleted edges are pre-checked against every *remaining* staged restriction so a
        // dangling reference is rejected deterministically instead of silently orphaned.
        var restrictionsDeleted = await DeleteRestrictionsAsync(transaction, datasetId, generation, batch.DeleteRestrictionIds, cancellationToken)
            .ConfigureAwait(false);
        var edgesDeleted = await DeleteEdgesAsync(transaction, datasetId, generation, batch.DeleteEdgeIds, cancellationToken)
            .ConfigureAwait(false);
        var edgesAdded = await UpsertEdgesAsync(transaction, datasetId, generation, batch.AddEdges, actor, isUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var edgesUpdated = await UpsertEdgesAsync(transaction, datasetId, generation, batch.UpdateEdges, actor, isUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        var restrictionsAdded = await UpsertRestrictionsAsync(transaction, datasetId, generation, batch.AddRestrictions, actor, isUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var restrictionsUpdated = await UpsertRestrictionsAsync(transaction, datasetId, generation, batch.UpdateRestrictions, actor, isUpdate: true, cancellationToken)
            .ConfigureAwait(false);

        return new ContentCounts(edgesAdded, edgesUpdated, edgesDeleted, restrictionsAdded, restrictionsUpdated, restrictionsDeleted);
    }

    private static async Task<int> UpsertEdgesAsync(
        IDatabaseSession transaction,
        string datasetId,
        long generation,
        IReadOnlyList<NetworkEdgeEdit> edges,
        string actor,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        if (edges.Count == 0)
        {
            return 0;
        }

        var sql = isUpdate
            ? """
              UPDATE honua.network_topology_edge_edits
              SET source_vertex_id = @source_vertex_id,
                  target_vertex_id = @target_vertex_id,
                  geometry = ST_SetSRID(ST_GeomFromGeoJSON(@geometry_geojson), @srid),
                  srid = @srid,
                  attributes = @attributes::jsonb,
                  updated_at = now(),
                  updated_by = @actor
              WHERE dataset_id = @dataset_id AND generation = @generation AND edge_id = @edge_id;
              """
            : """
              INSERT INTO honua.network_topology_edge_edits
                  (dataset_id, generation, edge_id, source_vertex_id, target_vertex_id,
                   geometry, srid, attributes, created_by, updated_by)
              VALUES
                  (@dataset_id, @generation, @edge_id, @source_vertex_id, @target_vertex_id,
                   ST_SetSRID(ST_GeomFromGeoJSON(@geometry_geojson), @srid), @srid, @attributes::jsonb, @actor, @actor)
              ON CONFLICT (dataset_id, generation, edge_id) DO NOTHING;
              """;

        foreach (var edge in edges)
        {
            var affected = await transaction.ExecuteAsync(
                    sql,
                    new Dictionary<string, object?>
                    {
                        ["dataset_id"] = datasetId,
                        ["generation"] = generation,
                        ["edge_id"] = edge.EdgeId,
                        ["source_vertex_id"] = edge.SourceVertexId,
                        ["target_vertex_id"] = edge.TargetVertexId,
                        ["geometry_geojson"] = edge.GeometryGeoJson,
                        ["srid"] = edge.Srid,
                        ["attributes"] = SerializeAttributes(edge.Attributes),
                        ["actor"] = actor,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (affected == 0)
            {
                throw new NetworkTopologyEditValidationException(
                    isUpdate
                        ? $"Edge '{edge.EdgeId}' does not exist in this generation and cannot be updated."
                        : $"Edge '{edge.EdgeId}' already exists in this generation.");
            }
        }

        return edges.Count;
    }

    private static async Task<int> DeleteEdgesAsync(
        IDatabaseSession transaction,
        string datasetId,
        long generation,
        IReadOnlyList<string> edgeIds,
        CancellationToken cancellationToken)
    {
        if (edgeIds.Count == 0)
        {
            return 0;
        }

        var blocking = await transaction.QuerySingleOrDefaultAsync<string>(
                """
                SELECT COALESCE(
                    (SELECT from_edge_id FROM honua.network_topology_restriction_edits
                     WHERE dataset_id = @dataset_id AND generation = @generation AND from_edge_id = ANY(@edge_ids)
                     LIMIT 1),
                    (SELECT to_edge_id FROM honua.network_topology_restriction_edits
                     WHERE dataset_id = @dataset_id AND generation = @generation AND to_edge_id = ANY(@edge_ids)
                     LIMIT 1));
                """,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["edge_ids"] = edgeIds.ToArray(),
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (blocking is not null)
        {
            throw new NetworkTopologyEditValidationException(
                $"Edge '{blocking}' cannot be deleted because it is still referenced by a turn restriction in this generation.");
        }

        foreach (var edgeId in edgeIds)
        {
            var affected = await transaction.ExecuteAsync(
                    """
                    DELETE FROM honua.network_topology_edge_edits
                    WHERE dataset_id = @dataset_id AND generation = @generation AND edge_id = @edge_id;
                    """,
                    new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation, ["edge_id"] = edgeId },
                    cancellationToken)
                .ConfigureAwait(false);

            if (affected == 0)
            {
                throw new NetworkTopologyEditValidationException($"Edge '{edgeId}' does not exist in this generation and cannot be deleted.");
            }
        }

        return edgeIds.Count;
    }

    private static async Task<int> UpsertRestrictionsAsync(
        IDatabaseSession transaction,
        string datasetId,
        long generation,
        IReadOnlyList<NetworkTurnRestrictionEdit> restrictions,
        string actor,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        if (restrictions.Count == 0)
        {
            return 0;
        }

        var referencedEdgeIds = restrictions
            .SelectMany(static r => new[] { r.FromEdgeId, r.ToEdgeId })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var existingEdgeIds = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var edgeId in transaction.QueryAsync(
                """
                SELECT edge_id FROM honua.network_topology_edge_edits
                WHERE dataset_id = @dataset_id AND generation = @generation AND edge_id = ANY(@edge_ids);
                """,
                static row => row.GetFieldValue<string>(0),
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = generation,
                    ["edge_ids"] = referencedEdgeIds,
                },
                cancellationToken)
            .ConfigureAwait(false))
        {
            existingEdgeIds.Add(edgeId);
        }

        foreach (var restriction in restrictions)
        {
            if (!existingEdgeIds.Contains(restriction.FromEdgeId))
            {
                throw new NetworkTopologyEditValidationException(
                    $"Turn restriction '{restriction.RestrictionId}' references from-edge '{restriction.FromEdgeId}', which does not exist in this generation.");
            }

            if (!existingEdgeIds.Contains(restriction.ToEdgeId))
            {
                throw new NetworkTopologyEditValidationException(
                    $"Turn restriction '{restriction.RestrictionId}' references to-edge '{restriction.ToEdgeId}', which does not exist in this generation.");
            }
        }

        var sql = isUpdate
            ? """
              UPDATE honua.network_topology_restriction_edits
              SET from_edge_id = @from_edge_id,
                  via_vertex_id = @via_vertex_id,
                  to_edge_id = @to_edge_id,
                  kind = @kind,
                  penalty = @penalty,
                  attributes = @attributes::jsonb,
                  updated_at = now(),
                  updated_by = @actor
              WHERE dataset_id = @dataset_id AND generation = @generation AND restriction_id = @restriction_id;
              """
            : """
              INSERT INTO honua.network_topology_restriction_edits
                  (dataset_id, generation, restriction_id, from_edge_id, via_vertex_id, to_edge_id,
                   kind, penalty, attributes, created_by, updated_by)
              VALUES
                  (@dataset_id, @generation, @restriction_id, @from_edge_id, @via_vertex_id, @to_edge_id,
                   @kind, @penalty, @attributes::jsonb, @actor, @actor)
              ON CONFLICT (dataset_id, generation, restriction_id) DO NOTHING;
              """;

        foreach (var restriction in restrictions)
        {
            var affected = await transaction.ExecuteAsync(
                    sql,
                    new Dictionary<string, object?>
                    {
                        ["dataset_id"] = datasetId,
                        ["generation"] = generation,
                        ["restriction_id"] = restriction.RestrictionId,
                        ["from_edge_id"] = restriction.FromEdgeId,
                        ["via_vertex_id"] = restriction.ViaVertexId,
                        ["to_edge_id"] = restriction.ToEdgeId,
                        ["kind"] = FormatRestrictionKind(restriction.Kind),
                        ["penalty"] = restriction.Penalty,
                        ["attributes"] = SerializeAttributes(restriction.Attributes),
                        ["actor"] = actor,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (affected == 0)
            {
                throw new NetworkTopologyEditValidationException(
                    isUpdate
                        ? $"Turn restriction '{restriction.RestrictionId}' does not exist in this generation and cannot be updated."
                        : $"Turn restriction '{restriction.RestrictionId}' already exists in this generation.");
            }
        }

        return restrictions.Count;
    }

    private static async Task<int> DeleteRestrictionsAsync(
        IDatabaseSession transaction,
        string datasetId,
        long generation,
        IReadOnlyList<string> restrictionIds,
        CancellationToken cancellationToken)
    {
        foreach (var restrictionId in restrictionIds)
        {
            var affected = await transaction.ExecuteAsync(
                    """
                    DELETE FROM honua.network_topology_restriction_edits
                    WHERE dataset_id = @dataset_id AND generation = @generation AND restriction_id = @restriction_id;
                    """,
                    new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation, ["restriction_id"] = restrictionId },
                    cancellationToken)
                .ConfigureAwait(false);

            if (affected == 0)
            {
                throw new NetworkTopologyEditValidationException(
                    $"Turn restriction '{restrictionId}' does not exist in this generation and cannot be deleted.");
            }
        }

        return restrictionIds.Count;
    }

    private static string FormatState(NetworkTopologyGenerationState state) => NetworkTopologyGenerationStateFormat.Format(state);

    private static string FormatRestrictionKind(NetworkTurnRestrictionKind kind) => kind switch
    {
        NetworkTurnRestrictionKind.Prohibited => "prohibited",
        NetworkTurnRestrictionKind.Required => "required",
        NetworkTurnRestrictionKind.Penalty => "penalty",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unrecognised turn restriction kind."),
    };

    /// <summary>
    /// Serializes an edge/restriction attribute dictionary to a compact JSON object using
    /// <see cref="Utf8JsonWriter"/> directly (no reflection-based <c>JsonSerializer</c> call),
    /// keeping this AOT/trim-safe without a source-generated context for a simple
    /// string-to-string map.
    /// </summary>
    internal static string SerializeAttributes(IReadOnlyDictionary<string, string?> attributes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in attributes)
            {
                if (value is null)
                {
                    writer.WriteNull(key);
                }
                else
                {
                    writer.WriteString(key, value);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed record IdempotencyRecord(
        string ContentHash,
        string ResultState,
        long ResultRowVersion,
        long ResultSourceRevision,
        int EdgesAdded,
        int EdgesUpdated,
        int EdgesDeleted,
        int RestrictionsAdded,
        int RestrictionsUpdated,
        int RestrictionsDeleted);

    private readonly record struct ContentCounts(
        int EdgesAdded,
        int EdgesUpdated,
        int EdgesDeleted,
        int RestrictionsAdded,
        int RestrictionsUpdated,
        int RestrictionsDeleted);
}
