// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    private sealed class VersionedBatchSession
    {
        public required int LayerId { get; init; }
        public required Guid VersionId { get; init; }
        public required FeatureEditBatch EditBatch { get; init; }
        public required Dictionary<long, FeatureEditPrecondition>? Preconditions { get; init; }
        public required NpgsqlConnection Connection { get; init; }
        public required NpgsqlTransaction Transaction { get; init; }
        public ImmutableArray<long>.Builder CreatedIds { get; } = ImmutableArray.CreateBuilder<long>();
        public ImmutableArray<EditOperationResult>.Builder CreateResults { get; } = ImmutableArray.CreateBuilder<EditOperationResult>();
        public ImmutableArray<EditOperationResult>.Builder UpdateResults { get; } = ImmutableArray.CreateBuilder<EditOperationResult>();
        public ImmutableArray<EditOperationResult>.Builder DeleteResults { get; } = ImmutableArray.CreateBuilder<EditOperationResult>();

        public RowSecurityContext CreateRowContext(long objectId)
            => new(Connection, Transaction, new(VersionEditsTableName, LayerId, (VersionId, objectId)));

        public FeatureEditPrecondition? FindPrecondition(long objectId)
            => Preconditions is not null && Preconditions.TryGetValue(objectId, out var precondition)
                ? precondition
                : null;
    }

    private static IsolationLevel ResolveVersionedIsolationLevel(FeatureEditBatch editBatch)
    {
        var singleUpdate = editBatch.Updates.Length == 1 &&
            editBatch.Creates.IsDefaultOrEmpty && editBatch.Deletes.IsDefaultOrEmpty &&
            editBatch.Operations.IsDefaultOrEmpty;
        return singleUpdate || (!editBatch.Preconditions.IsDefaultOrEmpty && editBatch.Preconditions.Any(p => p.ExpectedRowAbsent))
            ? IsolationLevel.ReadCommitted
            : IsolationLevel.RepeatableRead;
    }

    private async Task<FeatureEditResult> ExecuteVersionedBatchBodyAsync(
        VersionedBatchSession session,
        CancellationToken cancellationToken)
    {
        var failed = !session.EditBatch.Operations.IsDefaultOrEmpty
            ? await ExecuteOrderedVersionedOperationsAsync(session, cancellationToken).ConfigureAwait(false)
            : await ExecuteUnorderedVersionedEditsAsync(session, cancellationToken).ConfigureAwait(false);

        if (failed && session.EditBatch.RollbackOnFailure)
        {
            await RollbackIfNeededAsync(session.Transaction).ConfigureAwait(false);
            return FeatureEditResult.Rollback(
                session.CreateResults.ToImmutable(),
                session.UpdateResults.ToImmutable(),
                session.DeleteResults.ToImmutable());
        }

        var versionedResult = BuildVersionedSuccessResult(session);
        return await CommitStagedResultAsync(session.Connection, session.Transaction, versionedResult, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> ExecuteOrderedVersionedOperationsAsync(
        VersionedBatchSession session,
        CancellationToken cancellationToken)
    {
        var failed = false;
        foreach (var operation in session.EditBatch.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ApplyVersionedOperationAsync(session, operation, cancellationToken).ConfigureAwait(false))
            {
                failed = true;
                if (session.EditBatch.RollbackOnFailure)
                {
                    break;
                }
            }
        }

        return failed;
    }

    private async Task<bool> ExecuteUnorderedVersionedEditsAsync(
        VersionedBatchSession session,
        CancellationToken cancellationToken)
    {
        var rollbackOnFailure = session.EditBatch.RollbackOnFailure;
        var failed = false;
        foreach (var feature in session.EditBatch.Creates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await TryVersionedCreateAsync(session, feature, cancellationToken).ConfigureAwait(false))
            {
                failed = true;
                if (rollbackOnFailure) return true;
            }
        }

        foreach (var feature in session.EditBatch.Updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await TryVersionedUpdateAsync(session, feature, cancellationToken).ConfigureAwait(false))
            {
                failed = true;
                if (rollbackOnFailure) return true;
            }
        }

        foreach (var objectId in session.EditBatch.Deletes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await TryVersionedDeleteAsync(session, objectId, cancellationToken).ConfigureAwait(false))
            {
                failed = true;
                if (rollbackOnFailure) return true;
            }
        }

        return failed;
    }

    private static FeatureEditResult BuildVersionedSuccessResult(VersionedBatchSession session)
    {
        var immutableCreateResults = session.CreateResults.ToImmutable();
        var immutableUpdateResults = session.UpdateResults.ToImmutable();
        var immutableDeleteResults = session.DeleteResults.ToImmutable();
        return FeatureEditResult.Success(
            immutableCreateResults.Count(static r => r.IsSuccess),
            immutableUpdateResults.Count(static r => r.IsSuccess),
            immutableDeleteResults.Count(static r => r.IsSuccess),
            session.CreatedIds.ToImmutable(),
            immutableCreateResults,
            immutableUpdateResults,
            immutableDeleteResults,
            wasRolledBack: false);
    }

    private static FeatureEditResult BuildVersionedFailureResult(FeatureEditBatch editBatch, Exception ex)
    {
        var commitOutcomeUnknown = ex is FeatureEditCommitOutcomeUnknownException;
        var errorMessage = commitOutcomeUnknown
            ? "Versioned transaction commit outcome is unknown."
            : "Versioned edit batch failed.";
        var (c, u, d) = CreateFailedOperationResults(editBatch, errorMessage, commitOutcomeUnknown);
        return commitOutcomeUnknown
            ? FeatureEditResult.FailureWithUnknownCommitOutcome(c, u, d)
            : FeatureEditResult.Rollback(c, u, d);
    }

    private async Task<(long CreatedId, EditOperationResult Result)> InsertVersionedRowAsync(
        VersionedBatchSession session,
        Feature feature,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var layerSrid = await _cacheManager.GetLayerSridAsync(session.LayerId, cancellationToken).ConfigureAwait(false);
        ValidateGeometrySrid(feature.Geometry, layerSrid);
        var geometryValueExpression = _geometryProcessor.GetGeometryWriteExpression(geometryStorageType, "$3", layerSrid);
        var attributesJson = SerializeToJsonString(feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
        var sql = BuildVersionedInsertSql(geometryValueExpression);

        return await RunVersionedRowWithSavepointAsync(
            session.Transaction,
            async () =>
            {
                await using var command = new NpgsqlCommand(sql, session.Connection) { Transaction = session.Transaction };
                ApplyCommandTimeout(command, _queryTimeoutSeconds);
                command.Parameters.AddWithValue(session.VersionId);
                command.Parameters.AddWithValue(session.LayerId);
                command.Parameters.Add(new NpgsqlParameter { Value = feature.Geometry ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
                command.Parameters.Add(new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlDbType.Jsonb });

                var (createdId, evidence) = await ExecuteVersionedInsertCommandAsync(session, command, cancellationToken)
                    .ConfigureAwait(false);
                var opResult = EditOperationResult.Success(createdId, feature.Attributes.GetValueOrDefault("globalId")?.ToString()) with { OperationEvidence = evidence };
                return (createdId, opResult);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private string BuildVersionedInsertSql(string geometryValueExpression)
        => string.Create(CultureInfo.InvariantCulture, $@"
            INSERT INTO {VersionEditsTableName}
                (version_id, layer_id, objectid, operation, geometry, attributes, base_geometry, base_attributes)
            VALUES (
                $1, $2,
                nextval(pg_get_serial_sequence('{_tableName}', 'objectid')),
                1, {geometryValueExpression}, $4, NULL, NULL)
            RETURNING objectid");

    private async Task<(long CreatedId, ImmutableArray<FeatureOperationEvidence> Evidence)> ExecuteVersionedInsertCommandAsync(
        VersionedBatchSession session,
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await BeginOperationEvidenceAsync(session.Connection, session.Transaction, cancellationToken).ConfigureAwait(false);
        var createdId = await RunRowSecurityCheckedWriteAsync(
            session.EditBatch.RowSecurityWriteCheck,
            session.Connection,
            session.Transaction,
            VersionEditsTableName,
            session.LayerId,
            session.VersionId,
            async () =>
            {
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                var id = Convert.ToInt64(result, CultureInfo.InvariantCulture);
                return (id, id);
            },
            cancellationToken).ConfigureAwait(false);
        var evidence = await ReadOperationEvidenceAsync(session.Connection, session.Transaction, cancellationToken).ConfigureAwait(false);
        return (createdId, evidence);
    }

    private async Task<(Feature WrittenFeature, EditOperationResult Result)> ExecuteVersionedUpdateRowAsync(
        VersionedBatchSession session,
        Feature feature,
        CancellationToken cancellationToken)
    {
        var layerSrid = await _cacheManager.GetLayerSridAsync(session.LayerId, cancellationToken).ConfigureAwait(false);
        ValidateGeometrySrid(feature.Geometry, layerSrid);

        return await RunVersionedRowWithSavepointAsync(
            session.Transaction,
            async () =>
            {
                var update = await PrepareVersionedUpdateAsync(
                    feature,
                    new(session.CreateRowContext(feature.Id), session.EditBatch.RowSecurityWriteCheck, session.FindPrecondition(feature.Id)),
                    cancellationToken).ConfigureAwait(false);

                var (committedFeature, evidence) = await CommitVersionedUpdateOverlayAsync(session, update, cancellationToken)
                    .ConfigureAwait(false);
                var opResult = EditOperationResult.Success(feature.Id, feature.Attributes.GetValueOrDefault("globalId")?.ToString()) with { OperationEvidence = evidence };
                return (committedFeature, opResult);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(Feature CommittedFeature, ImmutableArray<FeatureOperationEvidence> Evidence)> CommitVersionedUpdateOverlayAsync(
        VersionedBatchSession session,
        Feature update,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var layerSrid = await _cacheManager.GetLayerSridAsync(session.LayerId, cancellationToken).ConfigureAwait(false);
        var ownerGuard = OwnerEditGuard.Find(session.EditBatch.OwnerGuards, update.Id);

        await BeginOperationEvidenceAsync(session.Connection, session.Transaction, cancellationToken).ConfigureAwait(false);
        var committedFeature = await RunRowSecurityCheckedWriteAsync(
            session.EditBatch.RowSecurityWriteCheck,
            session.Connection,
            session.Transaction,
            VersionEditsTableName,
            session.LayerId,
            session.VersionId,
            async () =>
            {
                var returnedFeature = await UpsertOverlayRowAsync(
                    session.LayerId, session.VersionId, update.Id, operation: 2, update.Geometry, update.Attributes,
                    geometryStorageType, layerSrid, isDelete: false, ownerGuard, session.Connection, session.Transaction, cancellationToken).ConfigureAwait(false);
                return returnedFeature.HasValue
                    ? (update.Id, returnedFeature.Value)
                    : throw new ResourceNotFoundException($"Feature {update.Id} not found");
            },
            cancellationToken).ConfigureAwait(false);

        var evidence = await ReadOperationEvidenceAsync(session.Connection, session.Transaction, cancellationToken).ConfigureAwait(false);
        return (committedFeature, evidence);
    }

    private async Task<EditOperationResult> ExecuteVersionedDeleteRowAsync(
        VersionedBatchSession session,
        long objectId,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var layerSrid = await _cacheManager.GetLayerSridAsync(session.LayerId, cancellationToken).ConfigureAwait(false);
        var ownerGuard = OwnerEditGuard.Find(session.EditBatch.OwnerGuards, objectId);

        return await RunVersionedRowWithSavepointAsync(
            session.Transaction,
            async () =>
            {
                var earlyOutcome = await PrepareVersionedDeleteRowAsync(session, objectId, cancellationToken)
                    .ConfigureAwait(false);
                if (earlyOutcome.HasValue)
                {
                    return earlyOutcome.Value;
                }

                await BeginOperationEvidenceAsync(session.Connection, session.Transaction, cancellationToken).ConfigureAwait(false);
                var deletedRow = await UpsertOverlayRowAsync(
                    session.LayerId, session.VersionId, objectId, operation: 3, geometry: null, attributes: null,
                    geometryStorageType, layerSrid, isDelete: true, ownerGuard, session.Connection, session.Transaction, cancellationToken).ConfigureAwait(false);

                if (!deletedRow.HasValue)
                {
                    return EditOperationResult.Failure($"Feature {objectId} not found", objectId: objectId);
                }

                var evidence = await ReadOperationEvidenceAsync(session.Connection, session.Transaction, cancellationToken).ConfigureAwait(false);
                return EditOperationResult.Success(objectId) with { OperationEvidence = evidence };
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<EditOperationResult?> PrepareVersionedDeleteRowAsync(
        VersionedBatchSession session,
        long objectId,
        CancellationToken cancellationToken)
    {
        var rowContext = session.CreateRowContext(objectId);
        var precondition = session.FindPrecondition(objectId);
        if (precondition is { ExpectedRowAbsent: true })
        {
            await ReserveExpectedAbsenceKeyAsync(rowContext, cancellationToken).ConfigureAwait(false);
        }

        var current = await ReadEffectiveVersionRowForUpdateAsync(rowContext, cancellationToken).ConfigureAwait(false);
        if (precondition is { ExpectedRowAbsent: true } && !current.Feature.HasValue)
        {
            return EditOperationResult.Success(objectId);
        }

        await EnsureExistingRowVisibleAsync(
            session.EditBatch.RowSecurityWriteCheck,
            new RowSecurityContext(session.Connection, session.Transaction, new(current.TableName, session.LayerId, (current.VersionId, objectId))),
            cancellationToken).ConfigureAwait(false);
        if (precondition.HasValue)
        {
            ValidatePreconditionAgainstRow(session.LayerId, objectId, current.Feature, precondition.Value);
        }
        else if (!current.Feature.HasValue)
        {
            return EditOperationResult.Failure($"Feature {objectId} not found", objectId: objectId);
        }

        return null;
    }
}
