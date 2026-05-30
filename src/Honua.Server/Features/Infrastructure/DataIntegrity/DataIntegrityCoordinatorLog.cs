// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.DataIntegrity;

internal static partial class DataIntegrityCoordinatorLog
{
    [LoggerMessage(EventId = 9600, Level = LogLevel.Debug, Message = "Starting coordinated transaction {OperationId} with isolation level {IsolationLevel}")]
    public static partial void StartingCoordinatedTransaction(ILogger logger, string operationId, IsolationLevel isolationLevel);

    [LoggerMessage(EventId = 9601, Level = LogLevel.Debug, Message = "Successfully completed coordinated transaction {OperationId}")]
    public static partial void CompletedCoordinatedTransaction(ILogger logger, string operationId);

    [LoggerMessage(EventId = 9602, Level = LogLevel.Error, Message = "Coordinated transaction {OperationId} failed, initiating rollback")]
    public static partial void CoordinatedTransactionFailed(ILogger logger, string operationId, Exception exception);

    [LoggerMessage(EventId = 9603, Level = LogLevel.Error, Message = "Failed to rollback coordinated transaction {OperationId}")]
    public static partial void CoordinatedTransactionRollbackFailed(ILogger logger, string operationId, Exception exception);

    [LoggerMessage(EventId = 9604, Level = LogLevel.Debug, Message = "Attempting to acquire distributed lock {LockKey} (operation {OperationId})")]
    public static partial void AttemptingDistributedLock(ILogger logger, string lockKey, string operationId);

    [LoggerMessage(EventId = 9605, Level = LogLevel.Debug, Message = "Acquired distributed lock {LockKey} (operation {OperationId})")]
    public static partial void DistributedLockAcquired(ILogger logger, string lockKey, string operationId);

    [LoggerMessage(EventId = 9606, Level = LogLevel.Debug, Message = "Registered file operation {OperationType} for {FilePath} in transaction {OperationId}")]
    public static partial void FileOperationRegistered(ILogger logger, string operationType, string filePath, string operationId);

    [LoggerMessage(EventId = 9607, Level = LogLevel.Debug, Message = "Registered cache operation for {CacheKey} in transaction {OperationId}")]
    public static partial void CacheOperationRegistered(ILogger logger, string cacheKey, string operationId);

    [LoggerMessage(EventId = 9608, Level = LogLevel.Debug, Message = "Committing coordinated transaction {OperationId} with {OperationCount} operations")]
    public static partial void CommittingCoordinatedTransaction(ILogger logger, string operationId, int operationCount);

    [LoggerMessage(EventId = 9609, Level = LogLevel.Debug, Message = "Committed {Type} operation for {Key} in transaction {OperationId}")]
    public static partial void OperationCommitted(ILogger logger, string type, string key, string operationId);

    [LoggerMessage(EventId = 9610, Level = LogLevel.Error, Message = "Failed to commit {Type} operation for {Key} in transaction {OperationId}")]
    public static partial void OperationCommitFailed(ILogger logger, string type, string key, string operationId, Exception exception);

    [LoggerMessage(EventId = 9611, Level = LogLevel.Debug, Message = "Rolling back coordinated transaction {OperationId} with {OperationCount} operations")]
    public static partial void RollingBackCoordinatedTransaction(ILogger logger, string operationId, int operationCount);

    [LoggerMessage(EventId = 9612, Level = LogLevel.Debug, Message = "Rolled back {Type} operation for {Key} in transaction {OperationId}")]
    public static partial void OperationRolledBack(ILogger logger, string type, string key, string operationId);

    [LoggerMessage(EventId = 9613, Level = LogLevel.Error, Message = "Failed to rollback {Type} operation for {Key} in transaction {OperationId}")]
    public static partial void OperationRollbackFailed(ILogger logger, string type, string key, string operationId, Exception exception);

    [LoggerMessage(EventId = 9614, Level = LogLevel.Warning, Message = "Some rollback operations failed in transaction {OperationId}: {Exceptions}")]
    public static partial void RollbackOperationsFailed(ILogger logger, string operationId, string exceptions);

    [LoggerMessage(EventId = 9615, Level = LogLevel.Error, Message = "Error during transaction disposal for {OperationId}")]
    public static partial void TransactionDisposalFailed(ILogger logger, string operationId, Exception exception);

    [LoggerMessage(EventId = 9616, Level = LogLevel.Debug, Message = "Released distributed lock {LockKey} (operation {OperationId})")]
    public static partial void DistributedLockReleased(ILogger logger, string lockKey, string operationId);

    [LoggerMessage(EventId = 9617, Level = LogLevel.Error, Message = "Error releasing distributed lock {LockKey} (operation {OperationId})")]
    public static partial void DistributedLockReleaseFailed(ILogger logger, string lockKey, string operationId, Exception exception);

    [LoggerMessage(EventId = 9618, Level = LogLevel.Warning, Message = "Distributed lock cleanup blocked for 30+ seconds. Current lock count: {LockCount}, Ownership count: {OwnershipCount}")]
    public static partial void CleanupBlocked(ILogger logger, int lockCount, int ownershipCount);

    [LoggerMessage(EventId = 9619, Level = LogLevel.Information, Message = "Cleaned up {OrphanedCount} orphaned distributed locks during memory pressure cleanup")]
    public static partial void OrphanedLocksCleanedUp(ILogger logger, int orphanedCount);

    [LoggerMessage(EventId = 9620, Level = LogLevel.Debug, Message = "Cleaned up {ExpiredCount} expired locks, {ActiveCount} active locks remaining")]
    public static partial void ExpiredLocksCleanedUp(ILogger logger, int expiredCount, int activeCount);
}
