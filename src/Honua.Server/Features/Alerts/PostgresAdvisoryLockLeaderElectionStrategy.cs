// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Alerts;

internal sealed class PostgresAdvisoryLockLeaderElectionStrategy : ILeaderElectionStrategy, IAsyncDisposable
{
    internal const long LockKey = 8_044_282_257_919_950_152;

    // pg_try_advisory_lock(bigint) is stored with the high half in classid, the low
    // half in objid, and objsubid = 1. See PostgreSQL pg_locks.
    internal static uint LockClassId => unchecked((uint)(LockKey >> 32));

    internal static uint LockObjectId => unchecked((uint)LockKey);

    private readonly NpgsqlDataSource _lockDataSource;
    private readonly ILogger<PostgresAdvisoryLockLeaderElectionStrategy> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NpgsqlConnection? _lockConnection;

    public PostgresAdvisoryLockLeaderElectionStrategy(
        NpgsqlDataSource dataSource,
        ILogger<PostgresAdvisoryLockLeaderElectionStrategy> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Session advisory locks are not pinned to a multiplexed connection, and a
        // multiplexed pool forces keepalive off. Hold the lock on a dedicated
        // non-multiplexed source so a dead backend cannot keep reporting leadership.
        _lockDataSource = CreateDedicatedLockDataSource(dataSource);
        InstanceId = $"{Environment.MachineName}-{Environment.ProcessId}";
    }

    public bool IsLeader { get; private set; }

    /// <inheritdoc />
    public bool LastAcquireFaulted { get; private set; }

    public string InstanceId { get; }

    /// <summary>
    /// Builds a one-session data source for the advisory lock. Multiplexing is forced
    /// off and TCP keepalive is enabled even when the application pool has both the
    /// other way around.
    /// </summary>
    internal static NpgsqlDataSource CreateDedicatedLockDataSource(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString)
        {
            Multiplexing = false,
            PersistSecurityInfo = true,
            MaxPoolSize = 2,
            MinPoolSize = 0,
        };
        builder.KeepAlive = 30;
        builder.TcpKeepAliveTime = 30;
        builder.TcpKeepAliveInterval = 2;
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsLeader && await TryConfirmLeadershipAsync(cancellationToken).ConfigureAwait(false))
            {
                LastAcquireFaulted = false;
                return true;
            }

            await DisposeLockConnectionAsync().ConfigureAwait(false);

            var connection = await _lockDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lock_key);", connection);
                command.Parameters.AddWithValue("lock_key", NpgsqlDbType.Bigint, LockKey);

                var acquired = (bool?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false;

                // A determinate result (acquired or cleanly lost the race) clears the fault flag.
                LastAcquireFaulted = false;

                if (!acquired)
                {
                    await DisposeQuietlyAsync(connection).ConfigureAwait(false);
                    IsLeader = false;
                    return false;
                }

                _lockConnection = connection;
                IsLeader = true;
                return true;
            }
            catch
            {
                await DisposeQuietlyAsync(connection).ConfigureAwait(false);
                throw;
            }
        }
        // Intentional catch-all: the acquire attempt itself errored (e.g. pool exhaustion /
        // outage); we cannot tell whether a leader exists. Flag the fault so the evaluation
        // loop can surface a no-leader stall instead of the exception escaping.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastAcquireFaulted = true;
            IsLeader = false;
            PostgresAdvisoryLockLeaderElectionLog.AdvisoryLockAcquireFailed(_logger, ex);
            await DisposeLockConnectionAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = _lockConnection;
            _lockConnection = null;
            IsLeader = false;
            if (connection is null)
            {
                return;
            }

            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_key);", connection);
                    unlock.Parameters.AddWithValue("lock_key", NpgsqlDbType.Bigint, LockKey);
                    _ = await unlock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        await _lockDataSource.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task<bool> TryConfirmLeadershipAsync(CancellationToken cancellationToken)
    {
        if (_lockConnection is null)
        {
            return false;
        }

        try
        {
            return await IsLockHeldAsync(_lockConnection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // The client can still report State=Open after the backend was terminated.
            // A failed probe means this session no longer holds the lock.
            PostgresAdvisoryLockLeaderElectionLog.AdvisoryLockLost(_logger, ex);
            return false;
        }
    }

    private static async Task<bool> IsLockHeldAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return false;
        }

        const string sql = """
            SELECT 1
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND granted
              AND objsubid = 1
              AND pid = pg_backend_pid()
              AND classid::bigint = @class_id
              AND objid::bigint = @obj_id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = 5;
        // Pass the unsigned halves as bigint and cast in SQL. The low half does not
        // fit in a signed int, and Npgsql's oid parameter is easy to truncate.
        command.Parameters.AddWithValue("class_id", NpgsqlDbType.Bigint, (long)LockClassId);
        command.Parameters.AddWithValue("obj_id", NpgsqlDbType.Bigint, (long)LockObjectId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    private async Task DisposeLockConnectionAsync()
    {
        var connection = _lockConnection;
        _lockConnection = null;
        IsLeader = false;
        if (connection is not null)
        {
            await DisposeQuietlyAsync(connection).ConfigureAwait(false);
        }
    }

    private static async Task DisposeQuietlyAsync(NpgsqlConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A terminated backend can fail disposal. The session lock is already gone.
        }
    }
}
