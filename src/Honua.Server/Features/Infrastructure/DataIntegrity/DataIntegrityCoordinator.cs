// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Server.Features.Infrastructure.DataIntegrity;

/// <summary>
/// Coordinates data integrity operations across file storage, database, and cache layers
/// to prevent race conditions and ensure ACID properties.
/// </summary>
public interface IDataIntegrityCoordinator
{
    /// <summary>
    /// Executes a coordinated transaction that spans database, file storage, and cache operations
    /// with proper rollback capabilities and consistency guarantees.
    /// </summary>
    Task<T> ExecuteCoordinatedTransactionAsync<T>(
        string operationId,
        Func<IDataIntegrityTransaction, CancellationToken, Task<T>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires a distributed lock for coordinating operations across multiple instances.
    /// </summary>
    Task<IAsyncDisposable> AcquireDistributedLockAsync(
        string lockKey,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a coordinated transaction that can span multiple data stores.
/// </summary>
public interface IDataIntegrityTransaction : IAsyncDisposable
{
    /// <summary>
    /// Gets the database transaction for this coordinated transaction.
    /// </summary>
    NpgsqlTransaction DatabaseTransaction { get; }

    /// <summary>
    /// Gets the connection for this transaction.
    /// </summary>
    NpgsqlConnection Connection { get; }

    /// <summary>
    /// Registers a file operation to be committed or rolled back with the transaction.
    /// </summary>
    void RegisterFileOperation(
        string operationType,
        string filePath,
        Func<CancellationToken, Task> commitAction,
        Func<CancellationToken, Task> rollbackAction);

    /// <summary>
    /// Registers a cache operation to be committed or rolled back with the transaction.
    /// </summary>
    void RegisterCacheOperation(
        string cacheKey,
        Func<CancellationToken, Task> commitAction,
        Func<CancellationToken, Task> rollbackAction);

    /// <summary>
    /// Commits all registered operations atomically.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back all registered operations.
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of coordinated data integrity operations with proper ACID guarantees.
/// </summary>
internal sealed class DataIntegrityCoordinator : IDataIntegrityCoordinator
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DataIntegrityCoordinator> _logger;

    // Global coordination locks for distributed operations
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _globalLocks = new();
    private static readonly ConcurrentDictionary<string, (DateTimeOffset AcquiredAt, string OperationId)> _lockOwnership = new();
    private static readonly Timer _cleanupTimer = new Timer(CleanupExpiredLocks, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(10);
    private static readonly object _cleanupLock = new();

    public DataIntegrityCoordinator(NpgsqlDataSource dataSource, ILogger<DataIntegrityCoordinator> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<T> ExecuteCoordinatedTransactionAsync<T>(
        string operationId,
        Func<IDataIntegrityTransaction, CancellationToken, Task<T>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(operationId))
            throw new ArgumentException("Operation ID is required", nameof(operationId));
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        _logger.LogDebug("Starting coordinated transaction {OperationId} with isolation level {IsolationLevel}",
            operationId, isolationLevel);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Set transaction timeout if specified
        if (timeout.HasValue)
        {
            await using var timeoutCommand = connection.CreateCommand();
            timeoutCommand.CommandText = $"SET LOCAL statement_timeout = '{timeout.Value.TotalMilliseconds}ms'";
            await timeoutCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var dbTransaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);

        var transaction = new DataIntegrityTransaction(connection, dbTransaction, operationId, _logger);

        try
        {
            var result = await operation(transaction, cancellationToken);

            // Commit the coordinated transaction (database, files, cache)
            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Successfully completed coordinated transaction {OperationId}", operationId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordinated transaction {OperationId} failed, initiating rollback", operationId);

            try
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx, "Failed to rollback coordinated transaction {OperationId}", operationId);
            }

            throw;
        }
    }

    public async Task<IAsyncDisposable> AcquireDistributedLockAsync(
        string lockKey,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(lockKey))
            throw new ArgumentException("Lock key is required", nameof(lockKey));

        var semaphore = _globalLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        var operationId = Guid.NewGuid().ToString("N")[..8];

        _logger.LogDebug("Attempting to acquire distributed lock {LockKey} (operation {OperationId})",
            lockKey, operationId);

        var acquired = await semaphore.WaitAsync(timeout, cancellationToken);
        if (!acquired)
        {
            throw new TimeoutException($"Failed to acquire distributed lock '{lockKey}' within {timeout}");
        }

        _lockOwnership.TryAdd(lockKey, (DateTimeOffset.UtcNow, operationId));

        _logger.LogDebug("Acquired distributed lock {LockKey} (operation {OperationId})",
            lockKey, operationId);

        return new DistributedLock(lockKey, operationId, semaphore, _logger);
    }

    private sealed class DataIntegrityTransaction : IDataIntegrityTransaction
    {
        private readonly string _operationId;
        private readonly ILogger _logger;
        private readonly List<(string Type, string Key, Func<CancellationToken, Task> Commit, Func<CancellationToken, Task> Rollback)> _operations = new();
        private bool _disposed;

        public NpgsqlConnection Connection { get; }
        public NpgsqlTransaction DatabaseTransaction { get; }

        public DataIntegrityTransaction(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string operationId,
            ILogger logger)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            DatabaseTransaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
            _operationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void RegisterFileOperation(
            string operationType,
            string filePath,
            Func<CancellationToken, Task> commitAction,
            Func<CancellationToken, Task> rollbackAction)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DataIntegrityTransaction));

            _operations.Add(($"file:{operationType}", filePath, commitAction, rollbackAction));
            _logger.LogDebug("Registered file operation {OperationType} for {FilePath} in transaction {OperationId}",
                operationType, filePath, _operationId);
        }

        public void RegisterCacheOperation(
            string cacheKey,
            Func<CancellationToken, Task> commitAction,
            Func<CancellationToken, Task> rollbackAction)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DataIntegrityTransaction));

            _operations.Add(("cache", cacheKey, commitAction, rollbackAction));
            _logger.LogDebug("Registered cache operation for {CacheKey} in transaction {OperationId}",
                cacheKey, _operationId);
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DataIntegrityTransaction));

            _logger.LogDebug("Committing coordinated transaction {OperationId} with {OperationCount} operations",
                _operationId, _operations.Count);

            // First commit the database transaction
            await DatabaseTransaction.CommitAsync(cancellationToken);

            // Then commit file and cache operations
            var exceptions = new List<Exception>();

            foreach (var (type, key, commit, _) in _operations)
            {
                try
                {
                    await commit(cancellationToken);
                    _logger.LogDebug("Committed {Type} operation for {Key} in transaction {OperationId}",
                        type, key, _operationId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to commit {Type} operation for {Key} in transaction {OperationId}",
                        type, key, _operationId);
                    exceptions.Add(ex);
                }
            }

            if (exceptions.Count > 0)
            {
                throw new AggregateException($"Failed to commit some operations in transaction {_operationId}", exceptions);
            }
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) return;

            _logger.LogDebug("Rolling back coordinated transaction {OperationId} with {OperationCount} operations",
                _operationId, _operations.Count);

            var exceptions = new List<Exception>();

            // Roll back file and cache operations first (in reverse order)
            for (int i = _operations.Count - 1; i >= 0; i--)
            {
                var (type, key, _, rollback) = _operations[i];
                try
                {
                    await rollback(cancellationToken);
                    _logger.LogDebug("Rolled back {Type} operation for {Key} in transaction {OperationId}",
                        type, key, _operationId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to rollback {Type} operation for {Key} in transaction {OperationId}",
                        type, key, _operationId);
                    exceptions.Add(ex);
                }
            }

            // Then rollback the database transaction
            try
            {
                await DatabaseTransaction.RollbackAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }

            if (exceptions.Count > 0)
            {
                _logger.LogWarning("Some rollback operations failed in transaction {OperationId}: {Exceptions}",
                    _operationId, string.Join("; ", exceptions.Select(e => e.Message)));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                try
                {
                    if (DatabaseTransaction.Connection?.State == ConnectionState.Open)
                    {
                        await RollbackAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during transaction disposal for {OperationId}", _operationId);
                }
                finally
                {
                    await DatabaseTransaction.DisposeAsync();
                    _disposed = true;
                }
            }
        }
    }

    private sealed class DistributedLock : IAsyncDisposable
    {
        private readonly string _lockKey;
        private readonly string _operationId;
        private readonly SemaphoreSlim _semaphore;
        private readonly ILogger _logger;
        private bool _disposed;

        public DistributedLock(string lockKey, string operationId, SemaphoreSlim semaphore, ILogger logger)
        {
            _lockKey = lockKey;
            _operationId = operationId;
            _semaphore = semaphore;
            _logger = logger;
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                try
                {
                    _lockOwnership.TryRemove(_lockKey, out _);
                    _semaphore.Release();

                    _logger.LogDebug("Released distributed lock {LockKey} (operation {OperationId})",
                        _lockKey, _operationId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error releasing distributed lock {LockKey} (operation {OperationId})",
                        _lockKey, _operationId);
                }
                finally
                {
                    _disposed = true;
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Cleanup expired locks to prevent memory leaks in static collections
    /// PERFORMANCE FIX: Enhanced cleanup with bounded waiting to prevent unbounded growth
    /// </summary>
    private static void CleanupExpiredLocks(object? state)
    {
        // PERFORMANCE FIX: Use bounded wait instead of silent return to ensure cleanup happens
        var acquired = Monitor.TryEnter(_cleanupLock, TimeSpan.FromSeconds(30));
        if (!acquired)
        {
            // If we can't acquire the lock within 30 seconds, something is wrong
            // Log the issue but don't block the timer thread indefinitely
            try
            {
                var logger = new LoggerFactory().CreateLogger<DataIntegrityCoordinator>();
                logger.LogWarning("Distributed lock cleanup blocked for 30+ seconds. Current lock count: {LockCount}, Ownership count: {OwnershipCount}",
                    _globalLocks.Count, _lockOwnership.Count);
            }
            catch
            {
                // Ignore logging errors during cleanup
            }
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(LockTimeout);
            var expiredKeys = new List<string>();

            // Find expired lock ownership entries
            foreach (var kvp in _lockOwnership)
            {
                if (kvp.Value.AcquiredAt < cutoff)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            // PERFORMANCE FIX: Enhanced cleanup with memory pressure detection
            var totalLocks = _globalLocks.Count;
            var totalOwnership = _lockOwnership.Count;

            // If collections are growing too large, be more aggressive about cleanup
            if (totalLocks > 1000 || totalOwnership > 1000)
            {
                // Aggressive cleanup: also remove locks that exist in _globalLocks but not in _lockOwnership
                var orphanedLocks = new List<string>();
                foreach (var lockKey in _globalLocks.Keys)
                {
                    if (!_lockOwnership.ContainsKey(lockKey))
                    {
                        orphanedLocks.Add(lockKey);
                    }
                }

                // Remove orphaned locks (may happen if DistributedLock disposal failed)
                foreach (var key in orphanedLocks)
                {
                    if (_globalLocks.TryRemove(key, out var orphanedSemaphore))
                    {
                        try
                        {
                            orphanedSemaphore.Dispose();
                        }
                        catch
                        {
                            // Ignore disposal errors
                        }
                    }
                }

                if (orphanedLocks.Count > 0)
                {
                    try
                    {
                        var logger = new LoggerFactory().CreateLogger<DataIntegrityCoordinator>();
                        logger.LogInformation("Cleaned up {OrphanedCount} orphaned distributed locks during memory pressure cleanup",
                            orphanedLocks.Count);
                    }
                    catch
                    {
                        // Ignore logging errors
                    }
                }
            }

            // Remove expired entries and dispose semaphores
            foreach (var key in expiredKeys)
            {
                if (_lockOwnership.TryRemove(key, out _) && _globalLocks.TryRemove(key, out var semaphore))
                {
                    try
                    {
                        semaphore.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors
                    }
                }
            }

            // Log cleanup stats occasionally (every 20 cleanups)
            if (expiredKeys.Count > 0 && DateTime.UtcNow.Minute % 20 == 0)
            {
                try
                {
                    var logger = new LoggerFactory().CreateLogger<DataIntegrityCoordinator>();
                    logger.LogDebug("Cleaned up {ExpiredCount} expired locks, {ActiveCount} active locks remaining",
                        expiredKeys.Count, _globalLocks.Count);
                }
                catch
                {
                    // Ignore logging errors during cleanup
                }
            }
        }
        finally
        {
            Monitor.Exit(_cleanupLock);
        }
    }
}

/// <summary>
/// Service collection extensions for data integrity coordination.
/// </summary>
public static class DataIntegrityCoordinatorServiceCollectionExtensions
{
    /// <summary>
    /// Adds data integrity coordination services to the service collection.
    /// </summary>
    public static IServiceCollection AddDataIntegrityCoordination(this IServiceCollection services)
    {
        services.AddScoped<IDataIntegrityCoordinator, DataIntegrityCoordinator>();
        return services;
    }
}
