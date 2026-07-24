// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Infrastructure.DataIntegrity;

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
    /// Acquires a named coordination lock. The current implementation is process-local
    /// (a per-key <see cref="SemaphoreSlim"/>) and does NOT provide cross-instance
    /// exclusion; multi-node deployments require a Redis- or database-backed lock.
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

    // Global coordination locks for operations within this process (see the
    // interface remarks: this is process-local, not cross-instance).
    private static readonly ConcurrentDictionary<string, LockEntry> _globalLocks = new();
    private static readonly ConcurrentDictionary<string, (DateTimeOffset AcquiredAt, string OperationId)> _lockOwnership = new();
    private static readonly Timer _cleanupTimer = new Timer(CleanupExpiredLocks, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(10);
    private static readonly object _cleanupLock = new();

    // Logger captured from the first constructed instance so the timer-driven cleanup
    // can emit diagnostics (a provider-less `new LoggerFactory()` would discard them).
    private static ILogger? _cleanupLogger;

    public DataIntegrityCoordinator(NpgsqlDataSource dataSource, ILogger<DataIntegrityCoordinator> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // First-instance-wins capture of a static logger for the shared timer callback. Multiple
        // instances can legitimately be constructed concurrently (DI does not guarantee this type
        // is a singleton), so the write uses CompareExchange instead of a plain '??=' to avoid a
        // benign-looking but still racy read-then-write on shared static state.
        CaptureCleanupLogger(logger);
    }

    private static void CaptureCleanupLogger(ILogger<DataIntegrityCoordinator> logger)
    {
        Interlocked.CompareExchange(ref _cleanupLogger, logger, null);
    }

    /// <summary>
    /// Per-key lock state. <see cref="RefCount"/> counts active holders and waiters and
    /// is mutated under a lock on the entry; the cleanup timer only removes and disposes
    /// entries whose ref count is zero, so a semaphore can never be disposed out from
    /// under a live holder or queued waiter (which would silently break mutual exclusion).
    /// </summary>
    private sealed class LockEntry : IDisposable
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
        public bool Removed;

        public void Dispose() => Semaphore.Dispose();
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
        ArgumentNullException.ThrowIfNull(operation);

        DataIntegrityCoordinatorLog.StartingCoordinatedTransaction(_logger, operationId, isolationLevel);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var dbTransaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);

        // Set transaction timeout if specified. SET LOCAL only takes effect inside a
        // transaction block (outside one PostgreSQL warns and discards the setting),
        // so this must run after BeginTransactionAsync.
        if (timeout.HasValue)
        {
            var timeoutMilliseconds = (long)timeout.Value.TotalMilliseconds;
            await using var timeoutCommand = connection.CreateCommand();
            timeoutCommand.Transaction = dbTransaction;
            timeoutCommand.CommandText = string.Create(
                CultureInfo.InvariantCulture,
                $"SET LOCAL statement_timeout = '{timeoutMilliseconds}ms'");
            await timeoutCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var transaction = new DataIntegrityTransaction(connection, dbTransaction, operationId, _logger);

        try
        {
            var result = await operation(transaction, cancellationToken);

            // Commit the coordinated transaction (database, files, cache)
            await transaction.CommitAsync(cancellationToken);

            DataIntegrityCoordinatorLog.CompletedCoordinatedTransaction(_logger, operationId);
            return result;
        }
        catch (Exception ex)
        {
            DataIntegrityCoordinatorLog.CoordinatedTransactionFailed(_logger, operationId, ex);

            try
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            // Intentional catch-all: we are already inside the handler for a failed
            // transaction. A failure while rolling back must not replace or mask the
            // original exception, which is rethrown below; log it and continue.
            catch (Exception rollbackEx) when (rollbackEx is not OutOfMemoryException)
            {
                DataIntegrityCoordinatorLog.CoordinatedTransactionRollbackFailed(_logger, operationId, rollbackEx);
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

        var operationId = Guid.NewGuid().ToString("N")[..8];

        DataIntegrityCoordinatorLog.AttemptingDistributedLock(_logger, lockKey, operationId);

        // Register as a holder/waiter before touching the semaphore so the cleanup
        // timer cannot remove and dispose the entry while we wait on it. If cleanup
        // won the race and marked the entry removed, retry against a fresh entry.
        LockEntry entry;
        while (true)
        {
            entry = _globalLocks.GetOrAdd(lockKey, static _ => new LockEntry());
            lock (entry)
            {
                if (entry.Removed)
                {
                    continue;
                }

                entry.RefCount++;
                break;
            }
        }

        var acquired = false;
        try
        {
            acquired = await entry.Semaphore.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            if (!acquired)
            {
                lock (entry)
                {
                    entry.RefCount--;
                }
            }
        }

        if (!acquired)
        {
            throw new TimeoutException($"Failed to acquire distributed lock '{lockKey}' within {timeout}");
        }

        _lockOwnership[lockKey] = (DateTimeOffset.UtcNow, operationId);

        DataIntegrityCoordinatorLog.DistributedLockAcquired(_logger, lockKey, operationId);

        return new DistributedLock(lockKey, operationId, entry, _logger);
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
            ObjectDisposedException.ThrowIf(_disposed, this);

            _operations.Add(($"file:{operationType}", filePath, commitAction, rollbackAction));
            DataIntegrityCoordinatorLog.FileOperationRegistered(_logger, operationType, filePath, _operationId);
        }

        public void RegisterCacheOperation(
            string cacheKey,
            Func<CancellationToken, Task> commitAction,
            Func<CancellationToken, Task> rollbackAction)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _operations.Add(("cache", cacheKey, commitAction, rollbackAction));
            DataIntegrityCoordinatorLog.CacheOperationRegistered(_logger, cacheKey, _operationId);
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            DataIntegrityCoordinatorLog.CommittingCoordinatedTransaction(_logger, _operationId, _operations.Count);

            // First commit the database transaction (CommitSafelyAsync guards against the
            // phantom-commit race: checks cancellation before COMMIT, then uses None so the
            // in-flight round-trip is never interrupted — see HN0001).
            await DatabaseTransaction.CommitSafelyAsync(cancellationToken);

            // Then commit file and cache operations
            var exceptions = new List<Exception>();

            foreach (var (type, key, commit, _) in _operations)
            {
                try
                {
                    await commit(cancellationToken);
                    DataIntegrityCoordinatorLog.OperationCommitted(_logger, type, key, _operationId);
                }
                // Intentional catch-all: per-item loop over registered file/cache commit
                // actions. One operation's failure must not prevent the others from
                // attempting to commit; failures are collected and raised together below.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DataIntegrityCoordinatorLog.OperationCommitFailed(_logger, type, key, _operationId, ex);
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

            DataIntegrityCoordinatorLog.RollingBackCoordinatedTransaction(_logger, _operationId, _operations.Count);

            var exceptions = new List<Exception>();

            // Roll back file and cache operations first (in reverse order)
            for (int i = _operations.Count - 1; i >= 0; i--)
            {
                var (type, key, _, rollback) = _operations[i];
                try
                {
                    await rollback(cancellationToken);
                    DataIntegrityCoordinatorLog.OperationRolledBack(_logger, type, key, _operationId);
                }
                // Intentional catch-all: per-item loop over registered file/cache rollback
                // actions. One operation's failure must not stop the remaining operations
                // from also attempting to roll back; failures are collected and logged below.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DataIntegrityCoordinatorLog.OperationRollbackFailed(_logger, type, key, _operationId, ex);
                    exceptions.Add(ex);
                }
            }

            // Then rollback the database transaction
            try
            {
                await DatabaseTransaction.RollbackAsync(cancellationToken);
            }
            // Intentional catch-all: this is a best-effort rollback of the last remaining
            // resource (the database transaction) after file/cache rollbacks above; the
            // failure is collected with the others and reported via the summary log below
            // rather than thrown, so callers still observe the outcome of all operations.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                exceptions.Add(ex);
            }

            if (exceptions.Count > 0)
            {
                var messages = string.Join("; ", exceptions.Select(static e => e.Message));
                DataIntegrityCoordinatorLog.RollbackOperationsFailed(_logger, _operationId, messages);
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
                // Intentional catch-all: this is a best-effort rollback attempt during
                // disposal. A failure here must not prevent the transaction from being
                // disposed in the finally block below; log and continue.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DataIntegrityCoordinatorLog.TransactionDisposalFailed(_logger, _operationId, ex);
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
        private readonly LockEntry _entry;
        private readonly ILogger _logger;
        private bool _disposed;

        public DistributedLock(string lockKey, string operationId, LockEntry entry, ILogger logger)
        {
            _lockKey = lockKey;
            _operationId = operationId;
            _entry = entry;
            _logger = logger;
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                try
                {
                    _lockOwnership.TryRemove(_lockKey, out _);
                    _entry.Semaphore.Release();

                    DataIntegrityCoordinatorLog.DistributedLockReleased(_logger, _lockKey, _operationId);
                }
                // Intentional catch-all: this is a best-effort lock release during dispose.
                // A failure here must not prevent the finally block below from decrementing
                // the entry's ref count, which would otherwise leak the lock entry.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DataIntegrityCoordinatorLog.DistributedLockReleaseFailed(_logger, _lockKey, _operationId, ex);
                }
                finally
                {
                    lock (_entry)
                    {
                        _entry.RefCount--;
                    }

                    _disposed = true;
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Cleanup idle locks to prevent memory leaks in the static collections.
    /// Only entries with no holders or waiters (ref count zero) are removed and
    /// disposed: disposing a held semaphore would make the holder's Release()
    /// throw and let a concurrent caller mint a fresh semaphore for the same key,
    /// silently breaking mutual exclusion.
    /// </summary>
    private static void CleanupExpiredLocks(object? state)
    {
        // Use bounded wait instead of silent return to ensure cleanup happens
        var acquired = Monitor.TryEnter(_cleanupLock, TimeSpan.FromSeconds(30));
        if (!acquired)
        {
            // If we can't acquire the lock within 30 seconds, something is wrong.
            // Log the issue but don't block the timer thread indefinitely.
            if (_cleanupLogger is { } blockedLogger)
            {
                DataIntegrityCoordinatorLog.CleanupBlocked(blockedLogger, _globalLocks.Count, _lockOwnership.Count);
            }

            return;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(LockTimeout);

            // Find expired lock ownership entries
            var expiredKeys = _lockOwnership
                .Where(kvp => kvp.Value.AcquiredAt < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            // Remove expired entries whose locks are idle. Entries that are still
            // referenced (a legitimately long-running holder or queued waiters)
            // are left untouched and revisited on a later sweep.
            var removedCount = 0;
            foreach (var key in expiredKeys.Where(TryRemoveIdleEntry))
            {
                _lockOwnership.TryRemove(key, out _);
                removedCount++;
            }

            // If collections are growing too large, also sweep locks that have no
            // ownership entry (e.g. an acquire timed out or disposal failed),
            // using the same ref-count guard.
            if (_globalLocks.Count > 1000 || _lockOwnership.Count > 1000)
            {
                var orphanedRemoved = _globalLocks.Keys.Count(lockKey =>
                    !_lockOwnership.ContainsKey(lockKey) &&
                    TryRemoveIdleEntry(lockKey));

                if (orphanedRemoved > 0 && _cleanupLogger is { } orphanLogger)
                {
                    DataIntegrityCoordinatorLog.OrphanedLocksCleanedUp(orphanLogger, orphanedRemoved);
                }
            }

            if (removedCount > 0 && _cleanupLogger is { } expiredLogger)
            {
                DataIntegrityCoordinatorLog.ExpiredLocksCleanedUp(expiredLogger, removedCount, _globalLocks.Count);
            }
        }
        finally
        {
            Monitor.Exit(_cleanupLock);
        }
    }

    /// <summary>
    /// Removes and disposes the lock entry for <paramref name="lockKey"/> only when it
    /// has no holders or waiters. Marks the entry as removed under its lock so a racing
    /// acquirer that fetched the same instance retries against a fresh entry.
    /// </summary>
    private static bool TryRemoveIdleEntry(string lockKey)
    {
        if (!_globalLocks.TryGetValue(lockKey, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            if (entry.RefCount > 0 || entry.Removed)
            {
                return false;
            }

            entry.Removed = true;
            _globalLocks.TryRemove(lockKey, out _);
            entry.Dispose();
            return true;
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
