// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Edit;

public readonly record struct EditTransaction
{
    public required string TransactionId { get; init; }

    public required string Protocol { get; init; }

    public required ImmutableArray<UnifiedEditRequest> EditRequests { get; init; }

    public required TransactionConfiguration Configuration { get; init; }

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

    public ImmutableDictionary<string, object>? Metadata { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string? Principal { get; init; }

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

    public int TotalOperations => EditRequests.Sum(r => r.TotalOperations);

    public bool IsEmpty => EditRequests.IsDefaultOrEmpty || EditRequests.All(r => r.IsEmpty);

    public bool RequiresAtomicSemantics => Configuration.RollbackOnFailure || TotalOperations > 1;

    public EditTransaction WithState(TransactionState newState)
        => this with { State = newState };
}

public readonly record struct TransactionConfiguration
{
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

    public bool AllowPartialSuccess { get; init; } = true;

    public TransactionIsolationLevel IsolationLevel { get; init; } = TransactionIsolationLevel.ReadCommitted;

    public int TimeoutMs { get; init; } = 300_000; // 5 minutes default

    public bool PreserveOperationOrder { get; init; } = false;

    public bool EnableParallelExecution { get; init; } = false;

    public int MaxBatchSize { get; init; } = 1000;

    public RetryPolicy? RetryPolicy { get; init; }

    public ImmutableDictionary<string, object>? ConfigurationOptions { get; init; }

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

public enum TransactionState
{
    Pending,

    Executing,

    Committed,

    RolledBack,

    Failed,

    Cancelled,

    TimedOut
}

public readonly record struct RetryPolicy
{
    public int MaxRetries { get; init; }

    public int DelayMs { get; init; }

    public RetryPolicy()
    {
        MaxRetries = 3;
        DelayMs = 1000;
        BackoffStrategy = RetryBackoffStrategy.Exponential;
        MaxDelayMs = 30000;
    }

    public RetryBackoffStrategy BackoffStrategy { get; init; } = RetryBackoffStrategy.Exponential;

    public int MaxDelayMs { get; init; } = 10_000;

    public ImmutableArray<string>? RetryableErrors { get; init; }

    public static RetryPolicy Default => new()
    {
        MaxRetries = 3,
        DelayMs = 1000,
        BackoffStrategy = RetryBackoffStrategy.Exponential,
        MaxDelayMs = 10_000
    };

    public static RetryPolicy None => new() { MaxRetries = 0 };
}

public enum RetryBackoffStrategy
{
    Fixed,

    Exponential,

    Linear
}
