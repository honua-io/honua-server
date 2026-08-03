// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

using Honua.Postgres.Features.Infrastructure;
namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Internal signal that a <see cref="FeatureEditPrecondition"/> attached to an edit batch
/// no longer matched the stored row when re-validated inside the write transaction. The
/// edit paths translate this into <see cref="EditOperationResult.PreconditionFailed"/>.
/// </summary>
internal sealed class FeatureEditPreconditionFailedException : Exception
{
    public FeatureEditPreconditionFailedException(long objectId)
        : base($"Precondition failed for feature {objectId}: the stored row was modified by another writer.")
    {
        ObjectId = objectId;
    }

    public long ObjectId { get; }
}

/// <summary>
/// Signals that COMMIT started but its acknowledgement was not observed. The transaction may
/// have committed server-side even when a subsequent rollback reports that it already completed.
/// </summary>
internal sealed class FeatureEditCommitOutcomeUnknownException : Exception
{
    public FeatureEditCommitOutcomeUnknownException(Exception innerException)
        : base("The feature edit transaction commit outcome is unknown.", innerException)
    {
    }
}

/// <summary>
/// Internal signal that a create was rejected because the layer's geometry column is
/// non-nullable (a database NOT NULL constraint fired) and the feature supplied no
/// geometry. The edit paths translate this into a clean, actionable validation error
/// ("Geometry is required for create operation on a spatial layer") instead of leaking a
/// raw provider constraint violation.
/// </summary>
/// <remarks>
/// BH-014 fix-forward (#2423 / reverted by #2433): the original fix added a blanket
/// client-side pre-check that rejected null geometry on ANY spatial layer, which wrongly
/// failed valid attribute-only creates on nullable geometry columns (Issue #45) and
/// regressed ~10 CI shards. Mapping the actual database NOT NULL violation preserves the
/// clean-error UX for genuinely non-nullable geometry columns while never rejecting a
/// legitimate null-geometry create on a nullable column.
/// </remarks>
internal sealed class GeometryRequiredForCreateException : Exception
{
    public GeometryRequiredForCreateException(Exception innerException)
        : base(FeatureDataAccess.GeometryRequiredForCreateMessage, innerException)
    {
    }
}

internal sealed partial class FeatureDataAccess
{
    /// <summary>
    /// Client-safe validation message surfaced when a create supplies no geometry for a
    /// layer whose geometry column is non-nullable (database NOT NULL constraint).
    /// </summary>
    internal const string GeometryRequiredForCreateMessage =
        "Geometry is required for create operation on a spatial layer";

    /// <summary>
    /// Recognizes the common PostGIS geometry column names so a NOT NULL constraint
    /// violation on the geometry column can be mapped to a clean validation error.
    /// </summary>
    internal static bool IsGeometryColumn(string? columnName)
    {
        if (string.IsNullOrEmpty(columnName))
        {
            return false;
        }

        return columnName.Equals("geometry", StringComparison.OrdinalIgnoreCase)
            || columnName.Equals("geom", StringComparison.OrdinalIgnoreCase)
            || columnName.Equals("shape", StringComparison.OrdinalIgnoreCase)
            || columnName.Equals("the_geom", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a provider exception is a database NOT NULL constraint violation on the
    /// geometry column, i.e. the layer's geometry column is non-nullable and the create
    /// supplied no geometry. Used to map the raw provider error to a clean validation
    /// message without a client-side pre-check (BH-014 fix-forward).
    /// </summary>
    internal static bool IsGeometryNotNullViolation(PostgresException ex)
        => ex.SqlState == PostgresErrorCodes.NotNullViolation && IsGeometryColumn(ex.ColumnName);

    public async Task<Feature> CreateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
    {
        // When an outbox scope is active and the provider supports transactional outbox,
        // wrap the single-row insert in an explicit transaction so the outbox row commits
        // atomically with the feature row. This closes the post-commit append gap that
        // single-row autocommit paths would otherwise expose to a process crash.
        if (TryUseTransactionalOutbox(out var outboxScope))
        {
            return await ExecuteSingleRowMutationWithOutboxAsync(
                "create",
                outboxScope!,
                async (connection, transaction, ct) =>
                {
                    var created = await CreateWithConnectionAsync(layerId, feature, connection, transaction, ct).ConfigureAwait(false);
                    return (created.Id, (Feature?)created);
                },
                cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await CreateWithConnectionAsync(layerId, feature, connection, transaction: null, cancellationToken);
    }

    public async Task<Feature> UpdateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
    {
        if (TryUseTransactionalOutbox(out var outboxScope))
        {
            return await ExecuteSingleRowMutationWithOutboxAsync(
                "update",
                outboxScope!,
                async (connection, transaction, ct) =>
                {
                    var updated = await UpdateWithConnectionAsync(layerId, feature, connection, transaction, ct).ConfigureAwait(false);
                    return (updated.Id, (Feature?)updated);
                },
                cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await UpdateWithConnectionAsync(layerId, feature, connection, transaction: null, cancellationToken);
    }

    public async Task<bool> DeleteFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
    {
        if (TryUseTransactionalOutbox(out var outboxScope))
        {
            // Single-row delete path with outbox: open a transaction so the DELETE ... RETURNING
            // capture and the outbox INSERT commit together. The recorder runs only when the
            // RETURNING row was actually deleted; missing rows skip the outbox write entirely.
            var (Connection, Transaction) = await _connectionProvider.OpenTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await using var _ = Connection;
            await using var __ = Transaction;
            var npgsql = Connection.RequireNpgsqlConnection();
            var npgsqlTransaction = (NpgsqlTransaction)Transaction;

            var deleted = await DeleteWithConnectionAsync(layerId, featureId, npgsql, npgsqlTransaction, cancellationToken).ConfigureAwait(false);
            if (deleted.Deleted)
            {
                await TryWriteOutboxRowAsync(
                    outboxScope!,
                    Connection,
                    Transaction,
                    featureId,
                    "delete",
                    deleted.Snapshot,
                    cancellationToken).ConfigureAwait(false);
            }

            await Transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
            return deleted.Deleted;
        }

        await using var conn = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var simpleDelete = await DeleteWithConnectionAsync(layerId, featureId, conn, transaction: null, cancellationToken).ConfigureAwait(false);
        return simpleDelete.Deleted;
    }

    private bool TryUseTransactionalOutbox([NotNullWhen(true)] out FeatureMutationOutboxScopeData? scope)
    {
        scope = FeatureMutationOutboxScope.Current;
        if (scope is null)
        {
            return false;
        }

        // A scope without a registered outbox repository is a wiring error: the protocol
        // layer opened a scope expecting durable CDC intent, so falling through to the
        // autocommit fast path would silently drop the envelope the scope was set up to
        // record â€” the exact gap the transactional outbox is meant to close. Fail loud
        // instead so the misconfiguration surfaces as a mutation failure rather than as
        // silent CDC loss. Capability provider and repository must be registered together.
        if (_outboxRepository is null)
        {
            throw new InvalidOperationException(
                "Feature mutation outbox scope is active but no IFeatureChangeOutboxWriter " +
                "is registered. The Postgres feature-store DI must register both the outbox " +
                "writer and the capability provider together so an opened scope can durably " +
                "commit its CDC envelope.");
        }

        return true;
    }

    private async Task<Feature> ExecuteSingleRowMutationWithOutboxAsync(
        string operation,
        FeatureMutationOutboxScopeData scope,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<(long ObjectId, Feature? Snapshot)>> mutate,
        CancellationToken cancellationToken)
    {
        var (Connection, Transaction) = await _connectionProvider.OpenTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var _ = Connection;
        await using var __ = Transaction;
        var npgsql = Connection.RequireNpgsqlConnection();
        var npgsqlTransaction = (NpgsqlTransaction)Transaction;

        var (objectId, snapshot) = await mutate(npgsql, npgsqlTransaction, cancellationToken).ConfigureAwait(false);

        await TryWriteOutboxRowAsync(
            scope,
            Connection,
            Transaction,
            objectId,
            operation,
            snapshot,
            cancellationToken).ConfigureAwait(false);

        await Transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return snapshot ?? throw new InvalidOperationException("Mutation returned no snapshot.");
    }

    private async Task TryWriteOutboxRowAsync(
        FeatureMutationOutboxScopeData scope,
        DbConnection connection,
        DbTransaction transaction,
        long objectId,
        string operation,
        Feature? snapshot,
        CancellationToken cancellationToken)
    {
        if (_outboxRepository is null)
        {
            return;
        }

        var entry = scope.EntryFactory(objectId, operation, snapshot);
        if (entry is null)
        {
            return;
        }

        await _outboxRepository.WriteOutboxRowAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken)
    {
        if (editBatch.IsEmpty)
        {
            return FeatureEditResult.Success(0, 0, 0);
        }

        // Branch-versioned edits route to the overlay write path (#1272 Track B, ADR-0051). DEFAULT
        // (null/IsDefault) falls through to the byte-identical base write path below, untouched.
        if (editBatch.VersionContext is { IsDefault: false } version)
        {
            return await ApplyVersionedEditsAsync(layerId, editBatch, version, cancellationToken).ConfigureAwait(false);
        }

        DbConnection dbConnection;
        NpgsqlTransaction? transaction;

        if (editBatch.RollbackOnFailure)
        {
            // Use RepeatableRead isolation level for feature edits to prevent phantom reads during batch operations
            var (txConnection, dbTransaction) = await _connectionProvider.OpenTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            dbConnection = txConnection;
            transaction = (NpgsqlTransaction)dbTransaction;
        }
        else
        {
            dbConnection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            transaction = null;
        }

        await using var _ = dbConnection;
        var connection = dbConnection.RequireNpgsqlConnection();
        await using var __ = transaction;

        var preconditions = BuildPreconditionMap(editBatch);

        // BH7-021: hoist partial-result accumulators outside the try so the outer catch for
        // the non-transactional path can report already-committed rows as successes rather than
        // re-marking them as failures (which drives clients to re-submit, creating duplicates).
        var partialCreatedIds = ImmutableArray<long>.Empty;
        var partialCreateResults = ImmutableArray<EditOperationResult>.Empty;
        var partialUpdateResults = ImmutableArray<EditOperationResult>.Empty;
        var partialDeleteResults = ImmutableArray<EditOperationResult>.Empty;

        try
        {
            if (!editBatch.Operations.IsDefaultOrEmpty)
            {
                return await ApplyOrderedEditsAsync(
                    layerId,
                    editBatch,
                    connection,
                    transaction,
                    preconditions,
                    cancellationToken).ConfigureAwait(false);
            }

            var (createdIds, createResults) = await ProcessCreatesWithResultsAsync(
                layerId,
                editBatch.Creates,
                connection,
                transaction,
                cancellationToken);
            partialCreatedIds = createdIds;
            partialCreateResults = createResults;

            var (updatedCount, updateResults) = await ProcessUpdatesWithResultsAsync(
                layerId,
                editBatch.Updates,
                connection,
                transaction,
                preconditions,
                cancellationToken);
            partialUpdateResults = updateResults;

            var (deletedCount, deleteResults) = await ProcessDeletesWithResultsAsync(
                layerId,
                editBatch.Deletes,
                connection,
                transaction,
                preconditions,
                cancellationToken);
            partialDeleteResults = deleteResults;

            var hasErrors = System.Linq.Enumerable.Any(createResults, r => !r.IsSuccess) ||
                            System.Linq.Enumerable.Any(updateResults, r => !r.IsSuccess) ||
                            System.Linq.Enumerable.Any(deleteResults, r => !r.IsSuccess);

            if (hasErrors && editBatch.RollbackOnFailure)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                return FeatureEditResult.Rollback(createResults, updateResults, deleteResults);
            }

            if (transaction != null)
            {
                await CommitEditTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
            }

            if (hasErrors)
            {
                return FeatureEditResult.Success(
                    System.Linq.Enumerable.Count(createResults, r => r.IsSuccess),
                    System.Linq.Enumerable.Count(updateResults, r => r.IsSuccess),
                    System.Linq.Enumerable.Count(deleteResults, r => r.IsSuccess),
                    createdIds,
                    createResults,
                    updateResults,
                    deleteResults,
                    wasRolledBack: false);
            }

            return FeatureEditResult.Success(
                createdIds.Length,
                updatedCount,
                deletedCount,
                createdIds,
                createResults,
                updateResults,
                deleteResults,
                wasRolledBack: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally broad: this is the top-level edit-batch error handler; any
        // unexpected failure must still roll back the transaction (if any) and return a
        // FeatureEditResult describing which operations succeeded before the failure,
        // rather than letting the exception propagate past the batch boundary.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.ApplyEditsFailed(_logger, layerId, editBatch.TotalOperations, ex);

            if (transaction != null)
            {
                await RollbackIfNeededAsync(transaction).ConfigureAwait(false);
                var commitOutcomeUnknown = ex is FeatureEditCommitOutcomeUnknownException;
                var errorMessage = commitOutcomeUnknown
                    ? "Transaction commit outcome is unknown."
                    : "Transaction failed.";
                var (createResults, updateResults, deleteResults) = CreateFailedOperationResults(
                    editBatch,
                    errorMessage,
                    commitOutcomeUnknown);

                return commitOutcomeUnknown
                    ? FeatureEditResult.FailureWithUnknownCommitOutcome(createResults, updateResults, deleteResults)
                    : FeatureEditResult.Rollback(createResults, updateResults, deleteResults);
            }

            // BH7-021: for the non-transactional path the partial accumulators above capture
            // every result that was recorded before the exception. Only operations that did
            // not yet produce a result (the unprocessed tail) should be reported as failed.
            // Reporting all as failed previously drove clients to re-submit already-committed
            // rows, creating duplicates.
            var tailCreateFailures = editBatch.Creates.Length > partialCreateResults.Length
                ? System.Linq.Enumerable
                    .Repeat(EditOperationResult.FailureWithUnknownCommitOutcome("Edit batch failed."), editBatch.Creates.Length - partialCreateResults.Length)
                    .ToImmutableArray()
                : ImmutableArray<EditOperationResult>.Empty;
            var tailUpdateFailures = editBatch.Updates.Length > partialUpdateResults.Length
                ? editBatch.Updates
                    .Skip(partialUpdateResults.Length)
                    .Select(f => EditOperationResult.FailureWithUnknownCommitOutcome("Edit batch failed.", objectId: f.Id))
                    .ToImmutableArray()
                : ImmutableArray<EditOperationResult>.Empty;
            var tailDeleteFailures = editBatch.Deletes.Length > partialDeleteResults.Length
                ? editBatch.Deletes
                    .Skip(partialDeleteResults.Length)
                    .Select(id => EditOperationResult.FailureWithUnknownCommitOutcome("Edit batch failed.", objectId: id))
                    .ToImmutableArray()
                : ImmutableArray<EditOperationResult>.Empty;

            var finalCreateResults = partialCreateResults.AddRange(tailCreateFailures);
            var finalUpdateResults = partialUpdateResults.AddRange(tailUpdateFailures);
            var finalDeleteResults = partialDeleteResults.AddRange(tailDeleteFailures);

            return FeatureEditResult.Success(
                System.Linq.Enumerable.Count(finalCreateResults, r => r.IsSuccess),
                System.Linq.Enumerable.Count(finalUpdateResults, r => r.IsSuccess),
                System.Linq.Enumerable.Count(finalDeleteResults, r => r.IsSuccess),
                partialCreatedIds,
                finalCreateResults,
                finalUpdateResults,
                finalDeleteResults,
                wasRolledBack: false);
        }
    }

    /// <summary>
    /// Builds an object-id keyed lookup of expected state tokens from the batch's
    /// preconditions. Returns null when the batch carries no preconditions so the hot
    /// path stays allocation-free.
    /// </summary>
    private static Dictionary<long, string>? BuildPreconditionMap(FeatureEditBatch editBatch)
    {
        if (editBatch.Preconditions.IsDefaultOrEmpty)
        {
            return null;
        }

        var map = new Dictionary<long, string>(editBatch.Preconditions.Length);
        foreach (var precondition in editBatch.Preconditions.Where(p => !string.IsNullOrEmpty(p.ExpectedStateToken)))
        {
            map[precondition.ObjectId] = precondition.ExpectedStateToken!;
        }

        return map.Count == 0 ? null : map;
    }

    /// <summary>
    /// Re-validates an optimistic-concurrency precondition inside the active write
    /// transaction. Locks the target row with SELECT ... FOR UPDATE (so a concurrent
    /// writer cannot modify it between this check and the subsequent UPDATE/DELETE),
    /// recomputes the canonical <see cref="FeatureStateToken"/> from the locked row, and
    /// throws when the stored state no longer matches the caller's snapshot token.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The row no longer exists.</exception>
    /// <exception cref="FeatureEditPreconditionFailedException">The stored row state changed since the caller's snapshot.</exception>
    private async Task EnsurePreconditionSatisfiedAsync(
        int layerId,
        long objectId,
        string expectedStateToken,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());
        var sql = $@"
            SELECT objectid, {geometrySelect}, attributes
            FROM {_tableName}
            WHERE layer_id = $1 AND objectid = $2
            FOR UPDATE";

        Feature current;
        await using (var command = new NpgsqlCommand(sql, connection) { Transaction = transaction })
        {
            ApplyCommandTimeout(command, _queryTimeoutSeconds);
            command.Parameters.AddWithValue(layerId);
            command.Parameters.AddWithValue(objectId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new ResourceNotFoundException($"Feature with ID {objectId} not found in layer {layerId}");
            }

            current = await ReadFeatureAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(FeatureStateToken.Compute(current), expectedStateToken, StringComparison.Ordinal))
        {
            throw new FeatureEditPreconditionFailedException(objectId);
        }
    }

    private async Task<FeatureEditResult> ApplyOrderedEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Dictionary<long, string>? preconditions,
        CancellationToken cancellationToken)
    {
        var createdIds = ImmutableArray.CreateBuilder<long>();
        var createResults = ImmutableArray.CreateBuilder<EditOperationResult>();
        var updateResults = ImmutableArray.CreateBuilder<EditOperationResult>();
        var deleteResults = ImmutableArray.CreateBuilder<EditOperationResult>();

        foreach (var operation in editBatch.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operationSucceeded = await ApplyOrderedEditOperationAsync(
                layerId,
                operation,
                connection,
                transaction,
                preconditions,
                createdIds,
                createResults,
                updateResults,
                deleteResults,
                cancellationToken).ConfigureAwait(false);

            if (!operationSucceeded && editBatch.RollbackOnFailure)
            {
                if (transaction != null)
                {
                    await RollbackIfNeededAsync(transaction).ConfigureAwait(false);
                }

                return FeatureEditResult.Rollback(
                    createResults.ToImmutable(),
                    updateResults.ToImmutable(),
                    deleteResults.ToImmutable());
            }
        }

        if (transaction != null)
        {
            await CommitEditTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        }

        var immutableCreatedIds = createdIds.ToImmutable();
        var immutableCreateResults = createResults.ToImmutable();
        var immutableUpdateResults = updateResults.ToImmutable();
        var immutableDeleteResults = deleteResults.ToImmutable();

        return FeatureEditResult.Success(
            immutableCreatedIds.Length,
            immutableUpdateResults.Count(static result => result.IsSuccess),
            immutableDeleteResults.Count(static result => result.IsSuccess),
            immutableCreatedIds,
            immutableCreateResults,
            immutableUpdateResults,
            immutableDeleteResults,
            wasRolledBack: false);
    }

    private async Task<bool> ApplyOrderedEditOperationAsync(
        int layerId,
        FeatureEditOperation operation,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Dictionary<long, string>? preconditions,
        ImmutableArray<long>.Builder createdIds,
        ImmutableArray<EditOperationResult>.Builder createResults,
        ImmutableArray<EditOperationResult>.Builder updateResults,
        ImmutableArray<EditOperationResult>.Builder deleteResults,
        CancellationToken cancellationToken)
    {
        switch (operation.Kind)
        {
            case FeatureEditOperationKind.Create:
                {
                    var feature = operation.Feature
                        ?? throw new InvalidOperationException("Ordered create operation is missing the feature payload.");

                    // Advance per-operation outbox metadata once per attempt so a failed
                    // mutation consumes its queued row context even if the EntryFactory is
                    // never reached. Without this, the next successful row of the same kind
                    // would inherit the failed row's GeometryChanged / requestId.
                    FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("create");
                    try
                    {
                        var created = await RunRowMutationAtomicallyAsync(
                            connection,
                            transaction,
                            async (conn, tx, ct) =>
                            {
                                var c = await CreateWithConnectionAsync(layerId, feature, conn, tx, ct).ConfigureAwait(false);
                                await TryWriteOutboxRowForBatchAsync(conn, tx, c.Id, "create", c, ct).ConfigureAwait(false);
                                return c;
                            },
                            cancellationToken).ConfigureAwait(false);
                        createdIds.Add(created.Id);
                        createResults.Add(EditOperationResult.Success(
                            created.Id,
                            feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
                        return true;
                    }
                    // Intentionally broad: per-operation failure in an ordered batch; sanitize via
                    // GetSafeEditOperationError (avoids leaking SQL/provider internals) and let the
                    // rest of the batch continue.
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        createResults.Add(CreateFailedOperationResult(ex, "Create"));
                        return false;
                    }
                }

            case FeatureEditOperationKind.Update:
                {
                    var feature = operation.Feature
                        ?? throw new InvalidOperationException("Ordered update operation is missing the feature payload.");

                    string? expectedStateToken = null;
                    preconditions?.TryGetValue(feature.Id, out expectedStateToken);

                    FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("update");
                    try
                    {
                        var updated = await RunRowMutationAtomicallyAsync(
                            connection,
                            transaction,
                            async (conn, tx, ct) =>
                            {
                                if (expectedStateToken is not null)
                                {
                                    // requireTransaction guarantees tx is non-null here.
                                    await EnsurePreconditionSatisfiedAsync(layerId, feature.Id, expectedStateToken, conn, tx!, ct).ConfigureAwait(false);
                                }

                                var u = await UpdateWithConnectionAsync(layerId, feature, conn, tx, ct).ConfigureAwait(false);
                                await TryWriteOutboxRowForBatchAsync(conn, tx, u.Id, "update", u, ct).ConfigureAwait(false);
                                return u;
                            },
                            cancellationToken,
                            requireTransaction: expectedStateToken is not null).ConfigureAwait(false);
                        updateResults.Add(EditOperationResult.Success(
                            updated.Id,
                            feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
                        return true;
                    }
                    catch (FeatureEditPreconditionFailedException)
                    {
                        updateResults.Add(EditOperationResult.PreconditionFailed(feature.Id));
                        return false;
                    }
                    // Intentionally broad: per-operation failure in an ordered batch; sanitize via
                    // GetSafeEditOperationError and let the rest of the batch continue.
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        updateResults.Add(CreateFailedOperationResult(ex, "Update", feature.Id));
                        return false;
                    }
                }

            case FeatureEditOperationKind.Delete:
                {
                    var objectId = operation.ObjectId
                        ?? throw new InvalidOperationException("Ordered delete operation is missing the target object ID.");

                    string? expectedStateToken = null;
                    preconditions?.TryGetValue(objectId, out expectedStateToken);

                    FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("delete");
                    try
                    {
                        var deleted = await RunRowMutationAtomicallyAsync(
                            connection,
                            transaction,
                            async (conn, tx, ct) =>
                            {
                                if (expectedStateToken is not null)
                                {
                                    // requireTransaction guarantees tx is non-null here.
                                    await EnsurePreconditionSatisfiedAsync(layerId, objectId, expectedStateToken, conn, tx!, ct).ConfigureAwait(false);
                                }

                                var d = await DeleteWithConnectionAsync(layerId, objectId, conn, tx, ct).ConfigureAwait(false);
                                if (d.Deleted)
                                {
                                    await TryWriteOutboxRowForBatchAsync(conn, tx, objectId, "delete", d.Snapshot, ct).ConfigureAwait(false);
                                }
                                return d;
                            },
                            cancellationToken,
                            requireTransaction: expectedStateToken is not null).ConfigureAwait(false);
                        if (deleted.Deleted)
                        {
                            deleteResults.Add(EditOperationResult.Success(objectId));
                            return true;
                        }

                        deleteResults.Add(EditOperationResult.Failure($"Feature {objectId} not found", objectId: objectId));
                        return false;
                    }
                    catch (FeatureEditPreconditionFailedException)
                    {
                        deleteResults.Add(EditOperationResult.PreconditionFailed(objectId));
                        return false;
                    }
                    // Intentionally broad: per-operation failure in an ordered batch; sanitize via
                    // GetSafeEditOperationError and let the rest of the batch continue.
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        deleteResults.Add(CreateFailedOperationResult(ex, "Delete", objectId));
                        return false;
                    }
                }

            default:
                throw new InvalidOperationException($"Unsupported ordered edit operation kind '{operation.Kind}'.");
        }
    }

    private static (
        ImmutableArray<EditOperationResult> createResults,
        ImmutableArray<EditOperationResult> updateResults,
        ImmutableArray<EditOperationResult> deleteResults) CreateFailedOperationResults(
        FeatureEditBatch editBatch,
        string errorMessage,
        bool commitOutcomeUnknown = false)
    {
        if (!editBatch.Operations.IsDefaultOrEmpty)
        {
            var createResults = ImmutableArray.CreateBuilder<EditOperationResult>();
            var updateResults = ImmutableArray.CreateBuilder<EditOperationResult>();
            var deleteResults = ImmutableArray.CreateBuilder<EditOperationResult>();

            foreach (var operation in editBatch.Operations)
            {
                switch (operation.Kind)
                {
                    case FeatureEditOperationKind.Create:
                        createResults.Add(CreateFailedOperationResult(
                            errorMessage,
                            objectId: null,
                            commitOutcomeUnknown: commitOutcomeUnknown));
                        break;
                    case FeatureEditOperationKind.Update:
                        updateResults.Add(CreateFailedOperationResult(
                            errorMessage,
                            operation.Feature?.Id,
                            commitOutcomeUnknown));
                        break;
                    case FeatureEditOperationKind.Delete:
                        deleteResults.Add(CreateFailedOperationResult(
                            errorMessage,
                            operation.ObjectId,
                            commitOutcomeUnknown));
                        break;
                }
            }

            return (
                createResults.ToImmutable(),
                updateResults.ToImmutable(),
                deleteResults.ToImmutable());
        }

        return (
            System.Linq.Enumerable.Select(editBatch.Creates, _ => CreateFailedOperationResult(
                errorMessage,
                objectId: null,
                commitOutcomeUnknown: commitOutcomeUnknown)).ToImmutableArray(),
            System.Linq.Enumerable.Select(editBatch.Updates, feature => CreateFailedOperationResult(errorMessage, feature.Id, commitOutcomeUnknown)).ToImmutableArray(),
            System.Linq.Enumerable.Select(editBatch.Deletes, id => CreateFailedOperationResult(errorMessage, id, commitOutcomeUnknown)).ToImmutableArray());
    }

    private static EditOperationResult CreateFailedOperationResult(
        string errorMessage,
        long? objectId,
        bool commitOutcomeUnknown)
        => commitOutcomeUnknown
            ? EditOperationResult.FailureWithUnknownCommitOutcome(errorMessage, objectId: objectId)
            : EditOperationResult.Failure(errorMessage, objectId: objectId);

    internal static async Task CommitEditTransactionAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        // Do not start COMMIT after caller cancellation, and never pass a live token once it does
        // start. A server error response proves PostgreSQL rejected the commit. Other provider
        // failures remain ambiguous because the commit may have completed before the connection
        // or acknowledgement was lost.
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (PostgresException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new FeatureEditCommitOutcomeUnknownException(ex);
        }
    }

    private static async Task RollbackIfNeededAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("has completed", StringComparison.Ordinal))
        {
            // The provider has already completed the transaction, so there is nothing left to
            // roll back. Commit-time callers retain their separate unknown-outcome classification.
        }
    }

    private async Task<Feature> CreateWithConnectionAsync(
        int layerId,
        Feature feature,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);
        ValidateGeometrySrid(feature.Geometry, layerSrid);
        var geometryValueExpression = _geometryProcessor.GetGeometryWriteExpression(geometryStorageType, "$2", layerSrid);

        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());
        var sql = $@"
            INSERT INTO {_tableName} (layer_id, geometry, attributes)
            VALUES ($1, {geometryValueExpression}, $3)
            RETURNING objectid, {geometrySelect}, attributes";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(layerId);
        var geometryParam = new NpgsqlParameter
        {
            Value = feature.Geometry ?? (object)DBNull.Value,
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea
        };
        command.Parameters.Add(geometryParam);

        // Serialize to JSON string and pass as JSONB parameter (AOT-compatible with source generators)
        var attributesDictionary = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var attributesJson = SerializeToJsonString(attributesDictionary);
        var attributesParam = new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb };
        command.Parameters.Add(attributesParam);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Failed to create feature: no result returned");
            }

            return await ReadFeatureAsync(reader, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ResourceConflictException("Feature creation conflicted with existing data.", ex);
        }
        catch (PostgresException ex) when (IsGeometryNotNullViolation(ex))
        {
            // BH-014 fix-forward: a null-geometry create against a non-nullable geometry
            // column surfaces as a clean validation error rather than a raw provider
            // constraint violation. This maps only the database's own decision (the
            // NOT NULL constraint actually fired), so nullable geometry columns still
            // accept valid attribute-only creates (Issue #45) with no client-side gate.
            throw new GeometryRequiredForCreateException(ex);
        }
    }

    private async Task<Feature> UpdateWithConnectionAsync(
        int layerId,
        Feature feature,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);
        ValidateGeometrySrid(feature.Geometry, layerSrid);
        var geometryValueExpression = _geometryProcessor.GetGeometryWriteExpression(geometryStorageType, "$3", layerSrid);

        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());
        var sql = $@"
            UPDATE {_tableName}
            SET geometry = {geometryValueExpression}, attributes = $4
            WHERE layer_id = $1 AND objectid = $2
            RETURNING objectid, {geometrySelect}, attributes";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(feature.Id);
        var geometryParam = new NpgsqlParameter
        {
            Value = feature.Geometry ?? (object)DBNull.Value,
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea
        };
        command.Parameters.Add(geometryParam);

        var attributesDictionary = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var attributesJson = SerializeToJsonString(attributesDictionary);
        var attributesParam = new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb };
        command.Parameters.Add(attributesParam);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ResourceNotFoundException($"Feature with ID {feature.Id} not found in layer {layerId}");
        }

        return await ReadFeatureAsync(reader, cancellationToken);
    }

    private async Task<DeleteOutcome> DeleteWithConnectionAsync(
        int layerId,
        long featureId,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        // When an outbox scope is active we use DELETE ... RETURNING to capture
        // the pre-delete snapshot inside the same statement so the dispatcher can
        // emit a delete event whose payload reflects the row that actually existed.
        // This avoids a second round-trip and removes the TOCTOU window where a
        // concurrent vacuum or a follow-up delete could erase the snapshot.
        // TryUseTransactionalOutbox throws when scope is active but the repository is
        // missing so the wiring error surfaces here instead of silently downgrading to
        // a no-snapshot DELETE that would still skip the outbox row.
        var captureSnapshot = TryUseTransactionalOutbox(out _);

        if (captureSnapshot)
        {
            var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
            var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());
            var sqlReturning = $@"
                DELETE FROM {_tableName}
                WHERE layer_id = $1 AND objectid = $2
                RETURNING objectid, {geometrySelect}, attributes";

            await using var commandReturning = new NpgsqlCommand(sqlReturning, connection)
            {
                Transaction = transaction
            };
            ApplyCommandTimeout(commandReturning, _queryTimeoutSeconds);
            commandReturning.Parameters.AddWithValue(layerId);
            commandReturning.Parameters.AddWithValue(featureId);

            await using var reader = await commandReturning.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new DeleteOutcome(false, null);
            }

            var snapshot = await ReadFeatureAsync(reader, cancellationToken).ConfigureAwait(false);
            return new DeleteOutcome(true, snapshot);
        }

        var sql = $@"
            DELETE FROM {_tableName}
            WHERE layer_id = $1 AND objectid = $2";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return new DeleteOutcome(rowsAffected > 0, null);
    }

    private readonly record struct DeleteOutcome(bool Deleted, Feature? Snapshot);

    private static void ValidateGeometrySrid(byte[]? geometry, int? layerSrid)
    {
        if (geometry == null || geometry.Length == 0 || !layerSrid.HasValue || layerSrid.Value <= 0)
        {
            return;
        }

        var srid = GetGeometrySrid(geometry);
        if (!srid.HasValue || srid.Value == 0)
        {
            return;
        }

        if (srid.Value != layerSrid.Value)
        {
            throw new ArgumentException(
                $"Geometry SRID {srid.Value} does not match layer SRID {layerSrid.Value}.");
        }
    }

    private static int? GetGeometrySrid(byte[] geometry)
    {
        try
        {
            var reader = new WKBReader();
            var parsed = reader.Read(geometry);
            return parsed.SRID > 0 ? parsed.SRID : 0;
        }
        catch (Exception ex) when (ex is ParseException or FormatException)
        {
            throw new ArgumentException("Invalid geometry WKB.", ex);
        }
    }

    private async Task<(ImmutableArray<long> createdIds, ImmutableArray<EditOperationResult> results)> ProcessCreatesWithResultsAsync(
        int layerId,
        ImmutableArray<Feature> features,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (features.Length == 0)
        {
            return (ImmutableArray<long>.Empty, ImmutableArray<EditOperationResult>.Empty);
        }

        // When an outbox scope is active we serialize creates so the per-row snapshot
        // returned by CreateWithConnectionAsync can drive the outbox factory. The fast
        // bulk INSERT path returns ids only and would force a second projection just to
        // satisfy the outbox payload â€” single-row inserts give us the snapshot directly
        // and remain inside the same connection/transaction so atomicity is preserved.
        // TryUseTransactionalOutbox throws when scope is active but the repository is
        // missing so the wiring error surfaces here instead of silently picking the bulk
        // path that would skip the outbox writes.
        var outboxActive = TryUseTransactionalOutbox(out _);

        if (transaction == null && features.Length > 1 && !outboxActive)
        {
            return await ExecuteAdaptiveNonTransactionalCreateBatchAsync(
                features,
                (batch, ct) => CreateBatchWithConnectionAsync(layerId, batch, connection, transaction, ct),
                async (feature, ct) =>
                {
                    try
                    {
                        var created = await CreateWithConnectionAsync(layerId, feature, connection, transaction, ct).ConfigureAwait(false);
                        return (
                            created.Id,
                            EditOperationResult.Success(
                                created.Id,
                                feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
                    }
                    // Intentionally broad: per-feature failure in an adaptive batch; sanitize via
                    // GetSafeEditOperationError and let the remaining features in the batch continue.
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        return (null, CreateFailedOperationResult(ex, "Create"));
                    }
                },
                (feature, createdId) => EditOperationResult.Success(
                    createdId,
                    feature.Attributes.GetValueOrDefault("globalId")?.ToString()),
                cancellationToken).ConfigureAwait(false);
        }

        var createdIds = new List<long>();
        var results = new List<EditOperationResult>();

        foreach (var feature in features)
        {
            // Advance per-operation outbox metadata once per attempt; a failed mutation
            // must consume its slot so the next successful row of the same kind picks
            // up its own GeometryChanged / requestId, not the failed row's.
            FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("create");
            try
            {
                var created = await RunRowMutationAtomicallyAsync(
                    connection,
                    transaction,
                    async (conn, tx, ct) =>
                    {
                        var c = await CreateWithConnectionAsync(layerId, feature, conn, tx, ct).ConfigureAwait(false);
                        await TryWriteOutboxRowForBatchAsync(conn, tx, c.Id, "create", c, ct).ConfigureAwait(false);
                        return c;
                    },
                    cancellationToken).ConfigureAwait(false);
                createdIds.Add(created.Id);
                results.Add(EditOperationResult.Success(created.Id, feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // BH7-020: propagate cancellation so the server stops executing remaining
                // batch items after a client abort, mirroring the update/delete path fixes.
                throw;
            }
            // Intentionally broad: per-feature failure in the row-by-row create path; sanitize
            // via GetSafeEditOperationError and let the rest of the batch continue.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                results.Add(CreateFailedOperationResult(ex, "Create"));
            }
        }

        return (createdIds.ToImmutableArray(), results.ToImmutableArray());
    }

    internal static async Task<(ImmutableArray<long> createdIds, ImmutableArray<EditOperationResult> results)> ExecuteAdaptiveNonTransactionalCreateBatchAsync<TFeature>(
        ImmutableArray<TFeature> features,
        Func<ImmutableArray<TFeature>, CancellationToken, Task<ImmutableArray<long>>> batchCreateAsync,
        Func<TFeature, CancellationToken, Task<(long? createdId, EditOperationResult result)>> singleCreateAsync,
        Func<TFeature, long, EditOperationResult> createSuccessResult,
        CancellationToken cancellationToken)
    {
        if (features.Length == 0)
        {
            return (ImmutableArray<long>.Empty, ImmutableArray<EditOperationResult>.Empty);
        }

        if (features.Length == 1)
        {
            var singleOutcome = await singleCreateAsync(features[0], cancellationToken).ConfigureAwait(false);
            return singleOutcome.createdId.HasValue
                ? ([singleOutcome.createdId.Value], [singleOutcome.result])
                : (ImmutableArray<long>.Empty, [singleOutcome.result]);
        }

        try
        {
            var batchCreatedIds = await batchCreateAsync(features, cancellationToken).ConfigureAwait(false);
            var batchResults = new EditOperationResult[batchCreatedIds.Length];
            for (var i = 0; i < batchCreatedIds.Length; i++)
            {
                batchResults[i] = createSuccessResult(features[i], batchCreatedIds[i]);
            }

            return (batchCreatedIds, batchResults.ToImmutableArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally broad: adaptive bisection on any batch-insert failure, recursively split in
        // half and retry each half so a single bad row can't fail the whole batch. No logging here
        // by design — the recursion bottoms out at singleCreateAsync's single-row path, which
        // sanitizes and reports the actual failing row via GetSafeEditOperationError.
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            var midpoint = features.Length / 2;
            var left = Slice(features, 0, midpoint);
            var right = Slice(features, midpoint, features.Length - midpoint);

            var leftOutcome = await ExecuteAdaptiveNonTransactionalCreateBatchAsync(
                left,
                batchCreateAsync,
                singleCreateAsync,
                createSuccessResult,
                cancellationToken).ConfigureAwait(false);
            var rightOutcome = await ExecuteAdaptiveNonTransactionalCreateBatchAsync(
                right,
                batchCreateAsync,
                singleCreateAsync,
                createSuccessResult,
                cancellationToken).ConfigureAwait(false);

            return (
                leftOutcome.createdIds.AddRange(rightOutcome.createdIds),
                leftOutcome.results.AddRange(rightOutcome.results));
        }
    }

    private static ImmutableArray<T> Slice<T>(ImmutableArray<T> source, int start, int length)
    {
        var builder = ImmutableArray.CreateBuilder<T>(length);
        for (var index = start; index < start + length; index++)
        {
            builder.Add(source[index]);
        }

        return builder.MoveToImmutable();
    }

    private async Task<ImmutableArray<long>> CreateBatchWithConnectionAsync(
        int layerId,
        ImmutableArray<Feature> features,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);
        var geometryPayload = new byte[]?[features.Length];
        var attributesPayload = new string[features.Length];

        for (var i = 0; i < features.Length; i++)
        {
            var feature = features[i];
            ValidateGeometrySrid(feature.Geometry, layerSrid);
            geometryPayload[i] = feature.Geometry;

            var attributesDictionary = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            attributesPayload[i] = SerializeToJsonString(attributesDictionary);
        }

        var geometryValueExpression = _geometryProcessor.GetGeometryWriteExpression(geometryStorageType, "payload.geometry", layerSrid);
        var sql = $@"
            WITH payload(geometry, attributes, ordinality) AS (
                SELECT source.geometry, source.attributes, source.ordinality
                FROM unnest($2::bytea[], $3::jsonb[]) WITH ORDINALITY AS source(geometry, attributes, ordinality)
            ),
            inserted AS (
                INSERT INTO {_tableName} (layer_id, geometry, attributes)
                SELECT $1, {geometryValueExpression}, payload.attributes
                FROM payload
                ORDER BY payload.ordinality
                RETURNING objectid
            ),
            numbered AS (
                -- BH3-020: ORDER BY ctid diverges from insertion order on tables with
                -- free pages from prior deletes. Use ROW_NUMBER() OVER () instead:
                -- PostgreSQL returns RETURNING rows in insertion order, which we control
                -- via ORDER BY payload.ordinality above, so row numbers map 1:1 to input
                -- feature array indices.
                SELECT objectid, ROW_NUMBER() OVER () AS rn
                FROM inserted
            )
            SELECT objectid
            FROM numbered
            ORDER BY rn";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = geometryPayload,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = attributesPayload,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb
        });

        var createdIds = ImmutableArray.CreateBuilder<long>(features.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            createdIds.Add(reader.GetInt64(0));
        }

        if (createdIds.Count != features.Length)
        {
            throw new InvalidOperationException("Failed to create all features in batch.");
        }

        return createdIds.MoveToImmutable();
    }

    private async Task<(int updatedCount, ImmutableArray<EditOperationResult> results)> ProcessUpdatesWithResultsAsync(
        int layerId,
        ImmutableArray<Feature> features,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Dictionary<long, string>? preconditions,
        CancellationToken cancellationToken)
    {
        if (features.Length == 0)
        {
            return (0, ImmutableArray<EditOperationResult>.Empty);
        }

        var updatedCount = 0;
        var results = new List<EditOperationResult>();

        foreach (var feature in features)
        {
            string? expectedStateToken = null;
            preconditions?.TryGetValue(feature.Id, out expectedStateToken);

            // Advance per-operation outbox metadata once per attempt; failed updates must
            // consume their queued slot so the next successful update gets its own
            // GeometryChanged / requestId rather than the failed row's.
            FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("update");
            try
            {
                var updated = await RunRowMutationAtomicallyAsync(
                    connection,
                    transaction,
                    async (conn, tx, ct) =>
                    {
                        if (expectedStateToken is not null)
                        {
                            // requireTransaction guarantees tx is non-null here.
                            await EnsurePreconditionSatisfiedAsync(layerId, feature.Id, expectedStateToken, conn, tx!, ct).ConfigureAwait(false);
                        }

                        var u = await UpdateWithConnectionAsync(layerId, feature, conn, tx, ct).ConfigureAwait(false);
                        await TryWriteOutboxRowForBatchAsync(conn, tx, u.Id, "update", u, ct).ConfigureAwait(false);
                        return u;
                    },
                    cancellationToken,
                    requireTransaction: expectedStateToken is not null).ConfigureAwait(false);
                updatedCount++;
                results.Add(EditOperationResult.Success(updated.Id, feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // BH7-020: propagate cancellation so the server stops executing the remaining
                // batch items after a client abort. Swallowing this was wasting CPU/connections
                // and misreporting any committed row as a failure.
                throw;
            }
            catch (FeatureEditPreconditionFailedException)
            {
                results.Add(EditOperationResult.PreconditionFailed(feature.Id));
            }
            // Intentionally broad: per-row failure in the batch update path; sanitize via
            // GetSafeEditOperationError and let the rest of the batch continue.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                results.Add(CreateFailedOperationResult(ex, "Update", feature.Id));
            }
        }

        return (updatedCount, results.ToImmutableArray());
    }

    private async Task<(int deletedCount, ImmutableArray<EditOperationResult> results)> ProcessDeletesWithResultsAsync(
        int layerId,
        ImmutableArray<long> featureIds,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Dictionary<long, string>? preconditions,
        CancellationToken cancellationToken)
    {
        if (featureIds.Length == 0)
        {
            return (0, ImmutableArray<EditOperationResult>.Empty);
        }

        var deletedCount = 0;
        var results = new List<EditOperationResult>();

        foreach (var featureId in featureIds)
        {
            string? expectedStateToken = null;
            preconditions?.TryGetValue(featureId, out expectedStateToken);

            // Advance per-operation outbox metadata once per attempt; rows that throw or
            // miss the snapshot (no RETURNING) must consume their slot so the next
            // successful delete gets its own queued requestId.
            FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("delete");
            try
            {
                var deleted = await RunRowMutationAtomicallyAsync(
                    connection,
                    transaction,
                    async (conn, tx, ct) =>
                    {
                        if (expectedStateToken is not null)
                        {
                            // requireTransaction guarantees tx is non-null here.
                            await EnsurePreconditionSatisfiedAsync(layerId, featureId, expectedStateToken, conn, tx!, ct).ConfigureAwait(false);
                        }

                        var d = await DeleteWithConnectionAsync(layerId, featureId, conn, tx, ct).ConfigureAwait(false);
                        if (d.Deleted)
                        {
                            await TryWriteOutboxRowForBatchAsync(conn, tx, featureId, "delete", d.Snapshot, ct).ConfigureAwait(false);
                        }
                        return d;
                    },
                    cancellationToken,
                    requireTransaction: expectedStateToken is not null).ConfigureAwait(false);
                if (deleted.Deleted)
                {
                    deletedCount++;
                    results.Add(EditOperationResult.Success(featureId));
                }
                else
                {
                    results.Add(EditOperationResult.Failure($"Feature {featureId} not found", objectId: featureId));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // BH7-020: propagate cancellation so the server stops executing remaining
                // batch items after a client abort, mirroring the update path fix.
                throw;
            }
            catch (FeatureEditPreconditionFailedException)
            {
                results.Add(EditOperationResult.PreconditionFailed(featureId));
            }
            // Intentionally broad: per-row failure in the batch delete path; sanitize via
            // GetSafeEditOperationError and let the rest of the batch continue.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                results.Add(CreateFailedOperationResult(ex, "Delete", featureId));
            }
        }

        return (deletedCount, results.ToImmutableArray());
    }

    /// <summary>
    /// Writes an outbox row inside the in-flight mutation transaction (or in a new auto-commit
    /// statement on the same connection if no transaction is active). Used by the batch and
    /// ordered-edit paths where the connection/transaction are already managed by ApplyEdits.
    /// </summary>
    private async Task TryWriteOutboxRowForBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long objectId,
        string operation,
        Feature? snapshot,
        CancellationToken cancellationToken)
    {
        // Use TryUseTransactionalOutbox so a scope-set/repository-missing combination
        // throws here too: the batch/ordered-edit paths can reach this helper through
        // RunRowMutationAtomicallyAsync without going through TryUseTransactionalOutbox
        // first (when the caller already holds a batch transaction), so the same guard
        // is needed to prevent the wiring error from silently dropping the outbox row.
        if (!TryUseTransactionalOutbox(out var scope))
        {
            return;
        }

        var entry = scope.EntryFactory(objectId, operation, snapshot);
        if (entry is null)
        {
            return;
        }

        if (transaction is null)
        {
            // Callers in the active-outbox batch path must wrap the row mutation and this
            // INSERT in a per-row transaction via RunRowMutationAtomicallyAsync so the two
            // commit atomically. Reaching this branch indicates a coding error; throwing
            // here surfaces it loudly rather than silently splitting the mutation and the
            // outbox row across separate connections (which would expose the CDC durability
            // gap the outbox is meant to close).
            throw new InvalidOperationException(
                "Outbox writes require an active transaction when a feature mutation scope is open.");
        }

        // TryUseTransactionalOutbox above already guaranteed _outboxRepository is non-null
        // (it throws on the scope-set/repository-missing combination). The bang!-suppress
        // is just a compiler hint; nullable analysis cannot see across the helper.
        await _outboxRepository!.WriteOutboxRowAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a row mutation paired with its outbox insert atomically. When the caller already
    /// holds a batch transaction the work runs inside it; when the batch is non-transactional
    /// (RollbackOnFailure=false) but an outbox scope is active, a per-row transaction wraps
    /// the mutation and outbox INSERT so a crash between them cannot leave a committed feature
    /// row without its CDC envelope. When no outbox scope is active the work runs directly on
    /// the connection so the autocommit fast path is preserved. Per-row failures bubble up to
    /// the caller's try/catch and are recorded as edit operation failures while preserving
    /// partial-success semantics.
    /// Callers enforcing an optimistic-concurrency precondition pass
    /// <paramref name="requireTransaction"/> so the SELECT ... FOR UPDATE re-validation and
    /// the row mutation always share a transaction even on the otherwise-autocommit path;
    /// the row lock would be released immediately without one.
    /// </summary>
    private async Task<TResult> RunRowMutationAtomicallyAsync<TResult>(
        NpgsqlConnection connection,
        NpgsqlTransaction? batchTransaction,
        Func<NpgsqlConnection, NpgsqlTransaction?, CancellationToken, Task<TResult>> mutateAndAppendOutbox,
        CancellationToken cancellationToken,
        bool requireTransaction = false)
    {
        if (batchTransaction is not null || (!requireTransaction && !TryUseTransactionalOutbox(out _)))
        {
            return await mutateAndAppendOutbox(connection, batchTransaction, cancellationToken).ConfigureAwait(false);
        }

        await using var rowTransaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var result = await mutateAndAppendOutbox(connection, (NpgsqlTransaction)rowTransaction, cancellationToken).ConfigureAwait(false);
        await rowTransaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal static string GetSafeEditOperationError(Exception ex, string operation)
    {
        return ex switch
        {
            ResourceNotFoundException => "Feature not found.",
            FeatureEditPreconditionFailedException => "The feature was modified by another writer; the supplied precondition no longer matches.",
            ResourceConflictException => "The operation conflicted with existing data.",
            // GeometryRequiredForCreateException carries a fixed, client-safe message
            // (no provider internals) so it is surfaced verbatim.
            GeometryRequiredForCreateException => GeometryRequiredForCreateMessage,
            ValidationException => "Invalid feature data.",
            ArgumentException or InvalidOperationException => "Invalid feature data.",
            _ => $"{operation} failed."
        };
    }

    /// <summary>
    /// Creates a sanitized row failure while distinguishing a server-rejected statement from a
    /// transport or acknowledgement failure. PostgreSQL error responses and local validation
    /// failures prove that the row did not commit; other provider failures remain unknown.
    /// </summary>
    internal static EditOperationResult CreateFailedOperationResult(
        Exception exception,
        string operation,
        long? objectId = null)
    {
        var errorMessage = GetSafeEditOperationError(exception, operation);
        return IsKnownNonCommitFailure(exception)
            ? EditOperationResult.Failure(errorMessage, objectId: objectId)
            : EditOperationResult.FailureWithUnknownCommitOutcome(errorMessage, objectId: objectId);
    }

    private static bool IsKnownNonCommitFailure(Exception exception)
        => exception is PostgresException
            or ResourceNotFoundException
            or FeatureEditPreconditionFailedException
            or ResourceConflictException
            or GeometryRequiredForCreateException
            or ValidationException
            or ArgumentException;
}
