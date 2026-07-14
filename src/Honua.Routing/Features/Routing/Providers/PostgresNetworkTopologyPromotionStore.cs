// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Postgres-backed atomic promotion/rollback store (#2719). Both operations share one
/// transactional helper: lock the active and target generation rows, verify
/// state/evidence/artifact preconditions, retire the old active generation, activate the
/// target, repoint <c>honua.network_datasets</c> to the target's own edge/vertex/srid
/// columns (which migration 087's rebuild-completion path already stamps onto every
/// <c>ready</c> generation), and record immutable history.
/// </summary>
/// <remarks>
/// Repointing <c>honua.network_datasets</c> deliberately runs with the legacy
/// <c>network_datasets_track_legacy_mapping_update</c> trigger from migration 084 disabled
/// for the duration of the transaction: that trigger exists to keep the old admin
/// registry-PUT path safe by auto-retiring the active generation and forking a brand new
/// one whenever <c>edge_table</c>/<c>vertex_table</c>/<c>srid</c> changes. This transaction
/// already performs that retire-and-activate sequence explicitly against an *existing*
/// candidate/target generation (not a freshly forked one), so the trigger's own retire step
/// would find zero active rows and raise its invariant-violation exception. Disabling it
/// only within this transaction (transactional DDL; automatically rolled back with the rest
/// of the transaction on any failure) leaves it fully intact for the legacy path.
/// </remarks>
internal sealed class PostgresNetworkTopologyPromotionStore : INetworkTopologyPromotionStore
{
    private const string GenerationColumns =
        "dataset_id, generation, source_revision, state, row_version, srid, created_at, updated_at, activated_at, failure_code, " +
        "edge_table, vertex_table";

    private const string PromotionColumns =
        "promotion_id, dataset_id, from_generation, to_generation, kind, actor, reason, idempotency_key, evidence_digest, promoted_at";

    private readonly IDatabaseSessionFactory _sessionFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresNetworkTopologyPromotionStore"/> class.
    /// </summary>
    public PostgresNetworkTopologyPromotionStore(IDatabaseSessionFactory sessionFactory)
        => _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));

    /// <inheritdoc />
    public async Task<NetworkTopologyPromotionRecord> PromoteAsync(
        string datasetId,
        long candidateGeneration,
        long expectedActiveGeneration,
        long expectedActiveRowVersion,
        string actor,
        string? reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        await transaction.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(hashtext(@dataset_id));",
                new Dictionary<string, object?> { ["dataset_id"] = datasetId },
                cancellationToken)
            .ConfigureAwait(false);

        var replay = await TryGetIdempotentReplayAsync(transaction, datasetId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return replay;
        }

        var activeRow = await LockGenerationAsync(transaction, datasetId, expectedActiveGeneration, cancellationToken).ConfigureAwait(false);
        var active = EnsureExpectedActiveIsCurrent(activeRow, datasetId, expectedActiveGeneration, expectedActiveRowVersion);

        var candidate = await LockGenerationAsync(transaction, datasetId, candidateGeneration, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            throw new NetworkTopologyPromotionConflictException(
                NetworkTopologyPromotionRejection.CandidateNotFound,
                $"Candidate generation {candidateGeneration} was not found.");
        }

        if (candidate.State != NetworkTopologyGenerationState.Ready)
        {
            throw new NetworkTopologyPromotionConflictException(
                NetworkTopologyPromotionRejection.CandidateNotReady,
                $"Candidate generation {candidateGeneration} is '{FormatState(candidate.State)}', not 'ready'.");
        }

        if (!await ArtifactsExistAsync(transaction, candidate, cancellationToken).ConfigureAwait(false))
        {
            throw new NetworkTopologyPromotionConflictException(
                NetworkTopologyPromotionRejection.EvidenceUnavailable,
                $"Candidate generation {candidateGeneration} shadow topology artifacts are missing.");
        }

        var record = await ApplyPromotionAsync(
                transaction, datasetId, active, candidate, NetworkTopologyPromotionKind.Promote, actor, reason, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return record;
    }

    /// <inheritdoc />
    public async Task<NetworkTopologyPromotionRecord> RollbackAsync(
        string datasetId,
        long targetGeneration,
        long expectedActiveGeneration,
        long expectedActiveRowVersion,
        string actor,
        string? reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        await transaction.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(hashtext(@dataset_id));",
                new Dictionary<string, object?> { ["dataset_id"] = datasetId },
                cancellationToken)
            .ConfigureAwait(false);

        var replay = await TryGetIdempotentReplayAsync(transaction, datasetId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return replay;
        }

        var activeRow = await LockGenerationAsync(transaction, datasetId, expectedActiveGeneration, cancellationToken).ConfigureAwait(false);
        var active = EnsureExpectedActiveIsCurrent(activeRow, datasetId, expectedActiveGeneration, expectedActiveRowVersion);

        var target = await LockGenerationAsync(transaction, datasetId, targetGeneration, cancellationToken).ConfigureAwait(false);
        if (target is null || target.State != NetworkTopologyGenerationState.Retired)
        {
            throw new NetworkTopologyPromotionConflictException(
                NetworkTopologyPromotionRejection.RollbackTargetNotEligible,
                $"Rollback target generation {targetGeneration} is not an eligible retired generation.");
        }

        if (!await ArtifactsExistAsync(transaction, target, cancellationToken).ConfigureAwait(false))
        {
            throw new NetworkTopologyPromotionConflictException(
                NetworkTopologyPromotionRejection.RollbackArtifactsMissing,
                $"Rollback target generation {targetGeneration} artifacts are no longer present (retention-expired).");
        }

        var record = await ApplyPromotionAsync(
                transaction, datasetId, active, target, NetworkTopologyPromotionKind.Rollback, actor, reason, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return record;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NetworkTopologyPromotionRecord>> ListHistoryAsync(
        string datasetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<NetworkTopologyPromotionRecord>();
        await foreach (var record in session.QueryAsync(
                $"""
                SELECT {PromotionColumns} FROM honua.network_topology_promotions
                WHERE dataset_id = @dataset_id
                ORDER BY promoted_at DESC;
                """,
                MapPromotion,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId },
                cancellationToken)
            .ConfigureAwait(false))
        {
            results.Add(record);
        }

        return results;
    }

    private static async Task<NetworkTopologyPromotionRecord> ApplyPromotionAsync(
        IDatabaseSession transaction,
        string datasetId,
        NetworkTopologyGeneration active,
        NetworkTopologyGeneration target,
        NetworkTopologyPromotionKind kind,
        string actor,
        string? reason,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!NetworkTopologyLifecycle.TryTransition(
                active, active.RowVersion, NetworkTopologyGenerationState.Retired, now, failureCode: null, out var retiredActive, out _))
        {
            throw new NetworkTopologyPromotionConflictException(
                NetworkTopologyPromotionRejection.StaleActiveGeneration,
                $"Active generation {active.Generation} could not be retired.");
        }

        if (!NetworkTopologyLifecycle.TryTransition(
                target, target.RowVersion, NetworkTopologyGenerationState.Active, now, failureCode: null, out var activatedTarget, out _))
        {
            throw new NetworkTopologyPromotionConflictException(
                kind == NetworkTopologyPromotionKind.Promote
                    ? NetworkTopologyPromotionRejection.CandidateNotReady
                    : NetworkTopologyPromotionRejection.RollbackTargetNotEligible,
                $"Generation {target.Generation} could not be activated.");
        }

        await ExecuteGenerationUpdateAsync(transaction, datasetId, retiredActive, active.RowVersion, cancellationToken).ConfigureAwait(false);
        await ExecuteGenerationUpdateAsync(transaction, datasetId, activatedTarget, target.RowVersion, cancellationToken).ConfigureAwait(false);

        await transaction.ExecuteAsync(
                "ALTER TABLE honua.network_datasets DISABLE TRIGGER network_datasets_track_legacy_mapping_update;",
                parameters: null,
                cancellationToken)
            .ConfigureAwait(false);

        // The target generation row (already stamped with its own solve tables by rebuild
        // completion, #2718) is the single source of truth here — copy its edge/vertex/srid
        // directly rather than threading them through as separate parameters.
        await transaction.ExecuteAsync(
                """
                UPDATE honua.network_datasets
                SET edge_table = gen.edge_table, vertex_table = gen.vertex_table, srid = gen.srid,
                    topology_version = gen.generation, updated_at = @now, updated_by = @actor
                FROM honua.network_topology_generations gen
                WHERE honua.network_datasets.id = @dataset_id
                  AND gen.dataset_id = @dataset_id AND gen.generation = @generation;
                """,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = target.Generation,
                    ["now"] = now,
                    ["actor"] = actor,
                },
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.ExecuteAsync(
                "ALTER TABLE honua.network_datasets ENABLE TRIGGER network_datasets_track_legacy_mapping_update;",
                parameters: null,
                cancellationToken)
            .ConfigureAwait(false);

        var evidenceDigest = await transaction.QuerySingleOrDefaultAsync<string>(
                """
                SELECT evidence_digest FROM honua.network_topology_rebuild_attempts
                WHERE dataset_id = @dataset_id AND generation = @generation AND state = 'ready'
                ORDER BY attempt DESC LIMIT 1;
                """,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = target.Generation },
                cancellationToken)
            .ConfigureAwait(false);

        var promotionId = $"promo_{Guid.NewGuid():N}";
        var saved = await transaction.QuerySingleOrDefaultAsync(
                $"""
                INSERT INTO honua.network_topology_promotions
                    (promotion_id, dataset_id, from_generation, to_generation, kind, actor, reason, idempotency_key, evidence_digest, promoted_at)
                VALUES
                    (@promotion_id, @dataset_id, @from_generation, @to_generation, @kind, @actor, @reason, @idempotency_key, @evidence_digest, @now)
                RETURNING {PromotionColumns};
                """,
                MapPromotion,
                new Dictionary<string, object?>
                {
                    ["promotion_id"] = promotionId,
                    ["dataset_id"] = datasetId,
                    ["from_generation"] = active.Generation,
                    ["to_generation"] = target.Generation,
                    ["kind"] = FormatKind(kind),
                    ["actor"] = actor,
                    ["reason"] = reason,
                    ["idempotency_key"] = idempotencyKey,
                    ["evidence_digest"] = evidenceDigest,
                    ["now"] = now,
                },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Promotion history entry could not be read back after insert.");

        return saved;
    }

    private static async Task ExecuteGenerationUpdateAsync(
        IDatabaseSession transaction,
        string datasetId,
        NetworkTopologyGeneration updated,
        long expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var affected = await transaction.ExecuteAsync(
                """
                UPDATE honua.network_topology_generations
                SET state = @state, row_version = @row_version, updated_at = @updated_at, activated_at = @activated_at
                WHERE dataset_id = @dataset_id AND generation = @generation AND row_version = @expected_row_version;
                """,
                new Dictionary<string, object?>
                {
                    ["dataset_id"] = datasetId,
                    ["generation"] = updated.Generation,
                    ["state"] = NetworkTopologyGenerationStateFormat.Format(updated.State),
                    ["row_version"] = updated.RowVersion,
                    ["updated_at"] = updated.UpdatedAt,
                    ["activated_at"] = updated.ActivatedAt,
                    ["expected_row_version"] = expectedRowVersion,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (affected == 0)
        {
            throw new NetworkTopologyPromotionConflictException(
                NetworkTopologyPromotionRejection.StaleActiveGeneration,
                $"Generation {updated.Generation} could not be updated (concurrent modification).");
        }
    }

    /// <summary>
    /// Validates the caller-supplied optimistic-concurrency precondition against the locked
    /// active-generation row, distinguishing a truly missing row (404) from a row that exists
    /// but is no longer <c>active</c> or whose row version has moved on (409) — the latter is
    /// the expected shape of a lost promotion/rollback race, not a not-found error.
    /// </summary>
    private static NetworkTopologyGeneration EnsureExpectedActiveIsCurrent(
        NetworkTopologyGeneration? active, string datasetId, long expectedActiveGeneration, long expectedActiveRowVersion)
    {
        if (active is null)
        {
            throw new NetworkTopologyPromotionConflictException(
                NetworkTopologyPromotionRejection.ActiveGenerationNotFound,
                $"Dataset '{datasetId}' has no generation matching {expectedActiveGeneration}.");
        }

        if (active.State != NetworkTopologyGenerationState.Active)
        {
            throw new NetworkTopologyPromotionConflictException(
                NetworkTopologyPromotionRejection.ActiveGenerationChanged,
                $"Generation {expectedActiveGeneration} is no longer the active generation for dataset '{datasetId}' (concurrent promotion/rollback already applied).");
        }

        if (active.RowVersion != expectedActiveRowVersion)
        {
            throw new NetworkTopologyPromotionConflictException(
                NetworkTopologyPromotionRejection.StaleActiveGeneration,
                $"Active generation {expectedActiveGeneration} row version has changed; retry with the current value.");
        }

        return active;
    }

    private static async Task<NetworkTopologyGeneration?> LockGenerationAsync(
        IDatabaseSession transaction, string datasetId, long generation, CancellationToken cancellationToken)
        => await transaction.QuerySingleOrDefaultAsync(
                $"""
                SELECT {GenerationColumns} FROM honua.network_topology_generations
                WHERE dataset_id = @dataset_id AND generation = @generation FOR UPDATE;
                """,
                PostgresNetworkTopologyGenerationStore.MapGeneration,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["generation"] = generation },
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<bool> ArtifactsExistAsync(
        IDatabaseSession transaction, NetworkTopologyGeneration generation, CancellationToken cancellationToken)
    {
        if (!NetworkDatasetValidation.IsValidTableIdentifier(generation.EdgeTable) ||
            !NetworkDatasetValidation.IsValidTableIdentifier(generation.VertexTable))
        {
            return false;
        }

        return await transaction.QuerySingleOrDefaultAsync<bool>(
                "SELECT to_regclass(@edge_table) IS NOT NULL AND to_regclass(@vertex_table) IS NOT NULL;",
                new Dictionary<string, object?>
                {
                    ["edge_table"] = generation.EdgeTable,
                    ["vertex_table"] = generation.VertexTable,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<NetworkTopologyPromotionRecord?> TryGetIdempotentReplayAsync(
        IDatabaseSession transaction, string datasetId, string idempotencyKey, CancellationToken cancellationToken)
        => await transaction.QuerySingleOrDefaultAsync(
                $"""
                SELECT {PromotionColumns} FROM honua.network_topology_promotions
                WHERE dataset_id = @dataset_id AND idempotency_key = @idempotency_key
                LIMIT 1;
                """,
                MapPromotion,
                new Dictionary<string, object?> { ["dataset_id"] = datasetId, ["idempotency_key"] = idempotencyKey },
                cancellationToken)
            .ConfigureAwait(false);

    private static string FormatState(NetworkTopologyGenerationState state) => NetworkTopologyGenerationStateFormat.Format(state);

    private static string FormatKind(NetworkTopologyPromotionKind kind) => kind switch
    {
        NetworkTopologyPromotionKind.Promote => "promote",
        NetworkTopologyPromotionKind.Rollback => "rollback",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unrecognised promotion kind."),
    };

    private static NetworkTopologyPromotionKind ParseKind(string value) => value switch
    {
        "promote" => NetworkTopologyPromotionKind.Promote,
        "rollback" => NetworkTopologyPromotionKind.Rollback,
        _ => throw new InvalidOperationException($"Unrecognised promotion kind '{value}'."),
    };

    private static NetworkTopologyPromotionRecord MapPromotion(IDatabaseRow row) => new(
        row.GetFieldValue<string>(0),
        row.GetFieldValue<string>(1),
        row.IsNull(2) ? null : row.GetFieldValue<long>(2),
        row.GetFieldValue<long>(3),
        ParseKind(row.GetFieldValue<string>(4)),
        row.GetFieldValue<string>(5),
        row.IsNull(6) ? null : row.GetFieldValue<string>(6),
        row.GetFieldValue<string>(7),
        row.IsNull(8) ? null : row.GetFieldValue<string>(8),
        row.GetFieldValue<DateTimeOffset>(9));
}
