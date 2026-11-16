// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed class RowSecurityWriteCheckFailedException : ResourceAccessDeniedException
{
    public RowSecurityWriteCheckFailedException(long objectId)
    {
        ObjectId = objectId;
    }

    public long ObjectId { get; }
}

internal sealed partial class FeatureDataAccess
{
    internal const string RowSecurityWriteCheckFailedMessage =
        "Edit not permitted: the feature would fall outside the row-level security scope of the caller.";

    private const string RowSecurityWriteCheckSavepoint = "honua_rls_write_check";

    private sealed record RowSecurityTarget(string TableName, int LayerId, (Guid? VersionId, long ObjectId) Row);
    private sealed record RowSecurityContext(NpgsqlConnection Connection, NpgsqlTransaction? Transaction, RowSecurityTarget Target);


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

            if (!await IsRowVisibleAndLockedAsync(writeCheck,
                    new RowSecurityContext(connection, transaction, new(tableName, layerId, (versionId, objectId))), cancellationToken)
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

    private async Task EnsureExistingRowVisibleAsync(
        SqlFragment? writeCheck, RowSecurityContext context, CancellationToken cancellationToken)
    {
        if (writeCheck is null)
        {
            return;
        }
        if (context.Transaction is null)
        {
            throw new InvalidOperationException("Row-level security write checks require an active transaction.");
        }
        if (!await IsRowVisibleAndLockedAsync(writeCheck, context, cancellationToken).ConfigureAwait(false))
        {
            throw new RowSecurityWriteCheckFailedException(context.Target.Row.ObjectId);
        }
    }

    private async Task<bool> IsRowVisibleAndLockedAsync(
        SqlFragment writeCheck, RowSecurityContext context, CancellationToken cancellationToken)
    {
        var (connection, transaction, target) = context;
        var (tableName, layerId, row) = target;
        var (versionId, objectId) = row;
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
                  AND ({predicate}) FOR UPDATE)");

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
