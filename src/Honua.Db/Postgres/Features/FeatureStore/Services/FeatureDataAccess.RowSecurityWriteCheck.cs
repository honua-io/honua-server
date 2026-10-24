// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Internal signal that a created or updated row does not satisfy the caller's row-level
/// security predicate (the <c>WITH CHECK</c> half of RLS, honua-server#6103). The write was
/// undone before this was thrown; edit paths translate it into a not-permitted operation result.
/// </summary>
internal sealed class RowSecurityWriteCheckFailedException : Exception
{
    public RowSecurityWriteCheckFailedException(long objectId)
        : base($"Feature {objectId} would fall outside the caller's row-level security scope.")
    {
        ObjectId = objectId;
    }

    public long ObjectId { get; }
}

internal sealed partial class FeatureDataAccess
{
    /// <summary>Client-safe message for a row rejected by the row-level security write check.</summary>
    internal const string RowSecurityWriteCheckFailedMessage =
        "Edit not permitted: the feature would fall outside the row-level security scope of the caller.";

    private const string RowSecurityWriteCheckSavepoint = "honua_rls_write_check";

    /// <summary>
    /// Runs one row write under the caller's row-level security <c>WITH CHECK</c> predicate
    /// (honua-server#6103). With no predicate the write runs unchanged. Otherwise the write
    /// runs behind a savepoint, the written row is re-selected inside the same transaction
    /// with the same parameterized predicate readers apply, and a row that is not visible is
    /// rolled back to the savepoint and reported as <see cref="RowSecurityWriteCheckFailedException"/>.
    /// Re-selecting (instead of evaluating the predicate in memory) honours every predicate
    /// shape the RLS source can translate, against the row exactly as stored.
    /// </summary>
    /// <param name="writeCheck">The caller's RLS predicate, or null when no policy applies.</param>
    /// <param name="connection">Connection the write runs on.</param>
    /// <param name="transaction">Transaction the write runs in; required when a predicate applies.</param>
    /// <param name="tableName">Table the written row lives in (base features or the version overlay).</param>
    /// <param name="layerId">Storage layer id.</param>
    /// <param name="versionId">Branch version id when the row lives in the version overlay.</param>
    /// <param name="write">The row write; returns the written row's object id with its result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<TResult> RunRowSecurityCheckedWriteAsync<TResult>(
        SqlFragment? writeCheck,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tableName,
        int layerId,
        Guid? versionId,
        Func<Task<(long ObjectId, TResult Result)>> write,
        CancellationToken cancellationToken)
    {
        if (writeCheck is null)
        {
            return (await write().ConfigureAwait(false)).Result;
        }

        if (transaction is null)
        {
            // Every caller routes predicate-checked writes through a batch or per-row
            // transaction (requireTransaction). Without one the row would already be
            // committed before the check could reject it.
            throw new InvalidOperationException(
                "Row-level security write checks require an active transaction.");
        }

        await transaction.SaveAsync(RowSecurityWriteCheckSavepoint, cancellationToken).ConfigureAwait(false);
        try
        {
            var (objectId, result) = await write().ConfigureAwait(false);

            if (!await IsRowVisibleUnderWriteCheckAsync(
                    writeCheck, connection, transaction, tableName, layerId, versionId, objectId, cancellationToken)
                .ConfigureAwait(false))
            {
                await transaction.RollbackAsync(RowSecurityWriteCheckSavepoint, CancellationToken.None).ConfigureAwait(false);
                throw new RowSecurityWriteCheckFailedException(objectId);
            }

            await transaction.ReleaseAsync(RowSecurityWriteCheckSavepoint, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is not RowSecurityWriteCheckFailedException and not OutOfMemoryException)
        {
            // A failed statement aborts the PostgreSQL transaction until the savepoint is
            // rolled back. Undo the row (or the aborted statement) so later slots in a
            // partial batch can still run.
            await transaction.RollbackAsync(RowSecurityWriteCheckSavepoint, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private Task<Feature> CreateCheckedAsync(
        int layerId,
        Feature feature,
        SqlFragment? writeCheck,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
        => RunRowSecurityCheckedWriteAsync(
            writeCheck,
            connection,
            transaction,
            _tableName,
            layerId,
            versionId: null,
            async () =>
            {
                var created = await CreateWithConnectionAsync(layerId, feature, connection, transaction, cancellationToken).ConfigureAwait(false);
                return (created.Id, created);
            },
            cancellationToken);

    private Task<Feature> UpdateCheckedAsync(
        int layerId,
        Feature feature,
        SqlFragment? writeCheck,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        OwnerEditGuard? ownerGuard,
        CancellationToken cancellationToken)
        => RunRowSecurityCheckedWriteAsync(
            writeCheck,
            connection,
            transaction,
            _tableName,
            layerId,
            versionId: null,
            async () =>
            {
                var updated = await UpdateWithConnectionAsync(
                    layerId, feature, connection, transaction, ownerGuard, cancellationToken).ConfigureAwait(false);
                return (updated.Id, updated);
            },
            cancellationToken);

    private async Task<bool> IsRowVisibleUnderWriteCheckAsync(
        SqlFragment writeCheck,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        int layerId,
        Guid? versionId,
        long objectId,
        CancellationToken cancellationToken)
    {
        var parameters = new List<object> { layerId, objectId };
        var versionPredicate = string.Empty;
        if (versionId.HasValue)
        {
            parameters.Add(versionId.Value);
            versionPredicate = " AND version_id = $3";
        }

        var paramIndex = parameters.Count + 1;
        var predicate = FeatureQueryBuilder.ConvertNamedParametersToPositional(writeCheck.Sql, ref paramIndex);
        parameters.AddRange(writeCheck.Parameters.Select(static value => value ?? DBNull.Value));

        // The predicate references the row's own columns unqualified (attributes, geometry,
        // objectid), exactly as it does on the read path, so it evaluates against the row
        // as written by this transaction.
        var sql = string.Create(CultureInfo.InvariantCulture, $@"
            SELECT EXISTS (
                SELECT 1 FROM {tableName}
                WHERE layer_id = $1 AND objectid = $2{versionPredicate}
                  AND ({predicate}))");

        await using var command = new NpgsqlCommand(sql, connection) { Transaction = transaction };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var visible = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return visible is true;
    }
}
