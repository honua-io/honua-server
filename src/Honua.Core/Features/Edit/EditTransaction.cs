// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Represents an edit transaction with unified semantics across all protocols.
/// Handles transaction lifecycle, rollback semantics, and commit behavior.
/// </summary>
public readonly record struct EditTransaction
{
    /// <summary>
    /// Unique transaction identifier.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// Protocol that initiated this transaction.
    /// </summary>
    public required string Protocol { get; init; }

    /// <summary>
    /// Edit requests to execute within this transaction.
    /// </summary>
    public required ImmutableArray<UnifiedEditRequest> EditRequests { get; init; }

    /// <summary>
    /// Transaction configuration and constraints.
    /// </summary>
    public required TransactionConfiguration Configuration { get; init; }

    /// <summary>
    /// Transaction state information.
    /// </summary>
    public TransactionState State { get; init; }

    public EditTransaction()
    {
        TransactionId = string.Empty;
        Protocol = string.Empty;
        EditRequests = ImmutableArray<UnifiedEditRequest>.Empty;
        Configuration = TransactionConfiguration.Default;
        State = TransactionState.Pending;
        Metadata = null;
        CreatedAt = DateTimeOffset.UtcNow;
        Principal = null;
    }

    /// <summary>
    /// Transaction metadata and context.
    /// </summary>
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Timestamp when transaction was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// User/principal that initiated the transaction.
    /// </summary>
    public string? Principal { get; init; }

    /// <summary>
    /// Creates a new transaction.
    /// </summary>
    /// <param name="transactionId">Transaction ID</param>
    /// <param name="protocol">Source protocol</param>
    /// <param name="editRequests">Edit requests</param>
    /// <param name="configuration">Transaction configuration</param>
    /// <param name="metadata">Optional metadata</param>
    /// <param name="principal">Optional principal</param>
    /// <returns>Edit transaction</returns>
    public static EditTransaction Create(
        string transactionId,
        string protocol,
        ImmutableArray<UnifiedEditRequest> editRequests,
        TransactionConfiguration configuration,
        ImmutableDictionary<string, object>? metadata = null,
        string? principal = null)
        => new()
        {
            TransactionId = transactionId,
            Protocol = protocol,
            EditRequests = editRequests,
            Configuration = configuration,
            State = TransactionState.Pending,
            Metadata = metadata,
            Principal = principal,
            CreatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>
    /// Creates a simple single-request transaction.
    /// </summary>
    /// <param name="transactionId">Transaction ID</param>
    /// <param name="protocol">Source protocol</param>
    /// <param name="editRequest">Single edit request</param>
    /// <param name="rollbackOnFailure">Whether to rollback on failure</param>
    /// <returns>Edit transaction</returns>
    public static EditTransaction CreateSimple(
        string transactionId,
        string protocol,
        UnifiedEditRequest editRequest,
        bool rollbackOnFailure = false)
        => Create(
            transactionId,
            protocol,
            ImmutableArray.Create(editRequest),
            TransactionConfiguration.Default with { RollbackOnFailure = rollbackOnFailure });

    /// <summary>
    /// Gets the total number of operations across all requests.
    /// </summary>
    public int TotalOperations => EditRequests.Sum(r => r.TotalOperations);

    /// <summary>
    /// Checks if the transaction is empty.
    /// </summary>
    public bool IsEmpty => EditRequests.IsDefaultOrEmpty || EditRequests.All(r => r.IsEmpty);

    /// <summary>
    /// Checks if the transaction requires atomic semantics.
    /// </summary>
    public bool RequiresAtomicSemantics => Configuration.RollbackOnFailure || TotalOperations > 1;

    /// <summary>
    /// Creates a new transaction with updated state.
    /// </summary>
    /// <param name="newState">New transaction state</param>
    /// <returns>Updated transaction</returns>
    public EditTransaction WithState(TransactionState newState)
        => this with { State = newState };
}

/// <summary>
/// Transaction configuration and behavior settings.
/// </summary>
public readonly record struct TransactionConfiguration
{
    /// <summary>
    /// Whether to rollback all changes on any failure.
    /// </summary>
    public bool RollbackOnFailure { get; init; }

    public TransactionConfiguration()
    {
        RollbackOnFailure = false;
        AllowPartialSuccess = true;
        IsolationLevel = TransactionIsolationLevel.ReadCommitted;
        TimeoutMs = 300_000;
        PreserveOperationOrder = false;
        EnableParallelExecution = false;
        MaxBatchSize = 1000;
        RetryPolicy = null;
        ConfigurationOptions = null;
    }

    /// <summary>
    /// Whether partial success is allowed.
    /// </summary>
    public bool AllowPartialSuccess { get; init; } = true;

    /// <summary>
    /// Transaction isolation level.
    /// </summary>
    public TransactionIsolationLevel IsolationLevel { get; init; } = TransactionIsolationLevel.ReadCommitted;

    /// <summary>
    /// Transaction timeout in milliseconds.
    /// </summary>
    public int TimeoutMs { get; init; } = 300_000; // 5 minutes default

    /// <summary>
    /// Whether to preserve operation order.
    /// </summary>
    public bool PreserveOperationOrder { get; init; } = false;

    /// <summary>
    /// Whether to enable parallel execution for independent operations.
    /// </summary>
    public bool EnableParallelExecution { get; init; } = false;

    /// <summary>
    /// Maximum batch size for large transactions.
    /// </summary>
    public int MaxBatchSize { get; init; } = 1000;

    /// <summary>
    /// Retry policy for transient failures.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Additional configuration options.
    /// </summary>
    public ImmutableDictionary<string, object>? ConfigurationOptions { get; init; }

    /// <summary>
    /// Default transaction configuration.
    /// </summary>
    public static TransactionConfiguration Default => new()
    {
        RollbackOnFailure = false,
        AllowPartialSuccess = true,
        IsolationLevel = TransactionIsolationLevel.ReadCommitted,
        TimeoutMs = 300_000,
        PreserveOperationOrder = false,
        EnableParallelExecution = false,
        MaxBatchSize = 1000
    };

    /// <summary>
    /// Atomic transaction configuration (all or nothing).
    /// </summary>
    public static TransactionConfiguration Atomic => new()
    {
        RollbackOnFailure = true,
        AllowPartialSuccess = false,
        IsolationLevel = TransactionIsolationLevel.Serializable,
        TimeoutMs = 300_000,
        PreserveOperationOrder = true,
        EnableParallelExecution = false,
        MaxBatchSize = 1000
    };

    /// <summary>
    /// High-performance transaction configuration.
    /// </summary>
    public static TransactionConfiguration HighPerformance => new()
    {
        RollbackOnFailure = false,
        AllowPartialSuccess = true,
        IsolationLevel = TransactionIsolationLevel.ReadCommitted,
        TimeoutMs = 300_000,
        PreserveOperationOrder = false,
        EnableParallelExecution = true,
        MaxBatchSize = 5000
    };

    /// <summary>
    /// Creates configuration for WFS 2.0 transaction semantics.
    /// </summary>
    public static TransactionConfiguration ForWfs20 => new()
    {
        RollbackOnFailure = true,
        AllowPartialSuccess = false,
        IsolationLevel = TransactionIsolationLevel.Serializable,
        TimeoutMs = 300_000,
        PreserveOperationOrder = true,
        EnableParallelExecution = false,
        MaxBatchSize = 1000
    };

    /// <summary>
    /// Creates configuration for GeoServices edit semantics.
    /// </summary>
    public static TransactionConfiguration ForGeoServices => new()
    {
        RollbackOnFailure = false, // Configurable per request
        AllowPartialSuccess = true,
        IsolationLevel = TransactionIsolationLevel.ReadCommitted,
        TimeoutMs = 600_000, // 10 minutes for large edits
        PreserveOperationOrder = false,
        EnableParallelExecution = true,
        MaxBatchSize = 2000
    };

    /// <summary>
    /// Creates configuration for OGC API Features transaction semantics.
    /// </summary>
    public static TransactionConfiguration ForOgcFeatures => new()
    {
        RollbackOnFailure = false, // Individual operations
        AllowPartialSuccess = true,
        IsolationLevel = TransactionIsolationLevel.ReadCommitted,
        TimeoutMs = 180_000, // 3 minutes
        PreserveOperationOrder = false,
        EnableParallelExecution = true,
        MaxBatchSize = 100
    };

    /// <summary>
    /// Creates configuration for OData transaction semantics.
    /// </summary>
    public static TransactionConfiguration ForOData => new()
    {
        RollbackOnFailure = true,
        AllowPartialSuccess = false,
        IsolationLevel = TransactionIsolationLevel.ReadCommitted,
        TimeoutMs = 120_000, // 2 minutes
        PreserveOperationOrder = false,
        EnableParallelExecution = false,
        MaxBatchSize = 100
    };
}

/// <summary>
/// Transaction state enumeration.
/// </summary>
public enum TransactionState
{
    /// <summary>
    /// Transaction created but not yet started.
    /// </summary>
    Pending,

    /// <summary>
    /// Transaction is currently executing.
    /// </summary>
    Executing,

    /// <summary>
    /// Transaction completed successfully.
    /// </summary>
    Committed,

    /// <summary>
    /// Transaction was rolled back.
    /// </summary>
    RolledBack,

    /// <summary>
    /// Transaction failed with errors.
    /// </summary>
    Failed,

    /// <summary>
    /// Transaction was cancelled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Transaction timed out.
    /// </summary>
    TimedOut
}

/// <summary>
/// Retry policy for transient failures.
/// </summary>
public readonly record struct RetryPolicy
{
    /// <summary>
    /// Maximum number of retry attempts.
    /// </summary>
    public int MaxRetries { get; init; }

    /// <summary>
    /// Delay between retries in milliseconds.
    /// </summary>
    public int DelayMs { get; init; }

    public RetryPolicy()
    {
        MaxRetries = 3;
        DelayMs = 1000;
        BackoffStrategy = RetryBackoffStrategy.Exponential;
        MaxDelayMs = 30000;
    }

    /// <summary>
    /// Backoff strategy for retries.
    /// </summary>
    public RetryBackoffStrategy BackoffStrategy { get; init; } = RetryBackoffStrategy.Exponential;

    /// <summary>
    /// Maximum delay for exponential backoff.
    /// </summary>
    public int MaxDelayMs { get; init; } = 10_000;

    /// <summary>
    /// Error conditions that should trigger retry.
    /// </summary>
    public ImmutableArray<string>? RetryableErrors { get; init; }

    /// <summary>
    /// Default retry policy.
    /// </summary>
    public static RetryPolicy Default => new()
    {
        MaxRetries = 3,
        DelayMs = 1000,
        BackoffStrategy = RetryBackoffStrategy.Exponential,
        MaxDelayMs = 10_000
    };

    /// <summary>
    /// No retry policy.
    /// </summary>
    public static RetryPolicy None => new() { MaxRetries = 0 };
}

/// <summary>
/// Retry backoff strategies.
/// </summary>
public enum RetryBackoffStrategy
{
    /// <summary>
    /// Fixed delay between retries.
    /// </summary>
    Fixed,

    /// <summary>
    /// Exponential backoff with doubling delay.
    /// </summary>
    Exponential,

    /// <summary>
    /// Linear increase in delay.
    /// </summary>
    Linear
}
