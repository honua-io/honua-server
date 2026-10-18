// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Features.WorkflowPackages;

internal sealed partial class PostgresWorkflowPackageStore
{
    private readonly Func<NpgsqlTransaction, CancellationToken, Task>? _publicationCommit;
    private readonly NpgsqlConnection? _publicationConnection;
    private readonly NpgsqlTransaction? _publicationTransaction;
    private readonly PublicationLockOwner? _publicationOwner;

    private PostgresWorkflowPackageStore(PostgresWorkflowPackageStore owner, NpgsqlConnection connection, NpgsqlTransaction transaction, PublicationLockOwner lifetime)
    {
        _connectionProvider = owner._connectionProvider; _packagesTable = owner._packagesTable;
        _versionsTable = owner._versionsTable; _publicationsTable = owner._publicationsTable;
        _publicationConnection = connection; _publicationTransaction = transaction; _publicationCommit = owner._publicationCommit; _publicationOwner = lifetime;
    }

    public async Task<TResult> ExecuteSerializedPublicationMutationAsync<TResult>(
        Func<IWorkflowPackageStore, CancellationToken, Task<TResult>> mutation,
        Func<IWorkflowPackageStore, Exception, CancellationToken, Task> reconcile,
        CancellationToken cancellationToken = default)
    {
        if (_publicationConnection is not null)
        {
            _publicationOwner!.RequireActive();
            return await mutation(this, cancellationToken).ConfigureAwait(false);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(60));
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(bounded.Token).ConfigureAwait(false);
        var lockKey = "workflow-publications:" + _publicationsTable;
        try
        {
            await using var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key, 0))", connection) { CommandTimeout = 15 };
            acquire.Parameters.AddWithValue("key", lockKey);
            await acquire.ExecuteNonQueryAsync(bounded.Token).ConfigureAwait(false);
        }
        catch
        {
            // Lost acquisition receipts must not return a possibly lock-owning session to a pool.
            NpgsqlConnection.ClearPool(connection);
            throw;
        }
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(bounded.Token).ConfigureAwait(false);
            try
            {
                var lifetime = new PublicationLockOwner();
                TResult result;
                try { result = await mutation(new PostgresWorkflowPackageStore(this, connection, transaction, lifetime), bounded.Token).ConfigureAwait(false); }
                finally { lifetime.Active = false; }
                if (_publicationCommit is null) await transaction.CommitSafelyAsync(bounded.Token).ConfigureAwait(false);
                else await _publicationCommit(transaction, bounded.Token).ConfigureAwait(false);
                return result;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollbackFailure) when (rollbackFailure is not OutOfMemoryException) { /* Commit may already have succeeded. */ }
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    // A disconnected session no longer owns a usable mutex. Reacquire it on a
                    // fresh authenticated provider connection and reconcile CURRENT committed
                    // state, never stale snapshots of another writer's replacement.
                    await ExecuteSerializedPublicationMutationAsync(async (fresh, token) =>
                    {
                        await reconcile(fresh, exception, token).ConfigureAwait(false);
                        return true;
                    }, static (_, _, _) => Task.CompletedTask, recovery.Token).ConfigureAwait(false);
                    throw;
                }
                await using var recoveryTransaction = await connection.BeginTransactionAsync(recovery.Token).ConfigureAwait(false);
                var lifetime = new PublicationLockOwner();
                try { await reconcile(new PostgresWorkflowPackageStore(this, connection, recoveryTransaction, lifetime), exception, recovery.Token).ConfigureAwait(false); }
                finally { lifetime.Active = false; }
                await recoveryTransaction.CommitSafelyAsync(recovery.Token).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", connection) { CommandTimeout = 5 };
                    release.Parameters.AddWithValue("key", lockKey);
                    await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException) { NpgsqlConnection.ClearPool(connection); }
            }
            else NpgsqlConnection.ClearPool(connection);
        }
    }

    private async Task<ConnectionLease> OpenWorkflowConnectionAsync(CancellationToken cancellationToken)
    {
        if (_publicationConnection is { } connection)
        {
            _publicationOwner!.RequireActive();
            return new(connection, false);
        }
        return new(await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false), true);
    }

    private sealed class PublicationLockOwner
    {
        public bool Active { get; set; } = true;
        public void RequireActive()
        {
            if (!Active) throw new InvalidOperationException("The publication mutation callback has ended.");
        }
    }

    private sealed class ConnectionLease(NpgsqlConnection connection, bool owned) : IAsyncDisposable
    {
        public NpgsqlConnection Connection => connection;
        public ValueTask DisposeAsync() => owned ? connection.DisposeAsync() : ValueTask.CompletedTask;
    }
}
