// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Alerts;

internal sealed class PostgresAdvisoryLockLeaderElectionStrategy : ILeaderElectionStrategy, IAsyncDisposable
{
    private const long LockKey = 8_044_282_257_919_950_152;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresAdvisoryLockLeaderElectionStrategy> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NpgsqlConnection? _lockConnection;

    public PostgresAdvisoryLockLeaderElectionStrategy(
        NpgsqlDataSource dataSource,
        ILogger<PostgresAdvisoryLockLeaderElectionStrategy> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InstanceId = $"{Environment.MachineName}-{Environment.ProcessId}";
    }

    public bool IsLeader { get; private set; }

    public string InstanceId { get; }

    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsLeader && _lockConnection is { State: System.Data.ConnectionState.Open })
            {
                return true;
            }

            var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lock_key);", connection);
            command.Parameters.AddWithValue("lock_key", NpgsqlDbType.Bigint, LockKey);

            var acquired = (bool?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false;
            if (!acquired)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            _lockConnection = connection;
            IsLeader = true;
            return true;
        }
        catch (Exception ex)
        {
            PostgresAdvisoryLockLeaderElectionLog.AdvisoryLockAcquireFailed(_logger, ex);
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
            if (_lockConnection is not { State: System.Data.ConnectionState.Open } connection)
            {
                IsLeader = false;
                return;
            }

            try
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_key);", connection);
                unlock.Parameters.AddWithValue("lock_key", NpgsqlDbType.Bigint, LockKey);
                _ = await unlock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                _lockConnection = null;
                IsLeader = false;
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
        _gate.Dispose();
    }
}
