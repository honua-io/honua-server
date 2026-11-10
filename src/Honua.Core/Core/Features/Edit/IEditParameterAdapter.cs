// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

public interface IEditParameterAdapter<in TProtocolEditRequest>
{
    Task<EditAdapterResult> ConvertAsync(
        TProtocolEditRequest protocolRequest,
        MetadataV2Resource resource,
        CancellationToken cancellationToken = default);

    string ProtocolName { get; }

    ProtocolEditLimits DefaultLimits { get; }

    TransactionSemantics TransactionSemantics { get; }
}

public sealed record EditAdapterResult
{
    public required bool IsSuccess { get; init; }

    public UnifiedEditRequest? EditRequest { get; init; }

    public EditTransaction? Transaction { get; init; }

    public string? ErrorMessage { get; init; }

    public IDictionary<string, object>? Metadata { get; init; }

    public static EditAdapterResult Success(
        UnifiedEditRequest editRequest,
        EditTransaction transaction,
        IDictionary<string, object>? metadata = null)
        => new()
        {
            IsSuccess = true,
            EditRequest = editRequest,
            Transaction = transaction,
            Metadata = metadata
        };

    public static EditAdapterResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}

public sealed record ProtocolEditLimits
{
    public int MaxFeaturesPerEdit { get; init; } = 1000;

    public int MaxEditsPerTransaction { get; init; } = 5000;

    public long MaxFeaturePayloadBytes { get; init; } = 10_000_000; // 10MB

    public int MaxAttributesPerFeature { get; init; } = 100;

    public bool SupportsBatchOperations { get; init; } = true;

    public bool SupportsConditionalOperations { get; init; }

    public bool SupportsPartialUpdates { get; init; }

    public bool RequiresGeometryValidation { get; init; } = true;

    public static ProtocolEditLimits OgcFeatures => new()
    {
        MaxFeaturesPerEdit = 100,
        MaxEditsPerTransaction = 1000,
        MaxFeaturePayloadBytes = 5_000_000, // 5MB
        MaxAttributesPerFeature = 50,
        SupportsBatchOperations = true,
        SupportsConditionalOperations = true,
        SupportsPartialUpdates = true,
        RequiresGeometryValidation = true
    };

    public static ProtocolEditLimits GeoServices => new()
    {
        MaxFeaturesPerEdit = 2000,
        MaxEditsPerTransaction = 10000,
        MaxFeaturePayloadBytes = 20_000_000, // 20MB
        MaxAttributesPerFeature = 100,
        SupportsBatchOperations = true,
        SupportsConditionalOperations = false,
        SupportsPartialUpdates = false,
        RequiresGeometryValidation = true
    };

    public static ProtocolEditLimits Wfs20 => new()
    {
        MaxFeaturesPerEdit = 1000,
        MaxEditsPerTransaction = 5000,
        MaxFeaturePayloadBytes = 10_000_000, // 10MB
        MaxAttributesPerFeature = 100,
        SupportsBatchOperations = true,
        SupportsConditionalOperations = false,
        SupportsPartialUpdates = false,
        RequiresGeometryValidation = true
    };

    public static ProtocolEditLimits OData => new()
    {
        MaxFeaturesPerEdit = 100,
        MaxEditsPerTransaction = 500,
        MaxFeaturePayloadBytes = 2_000_000, // 2MB
        MaxAttributesPerFeature = 50,
        SupportsBatchOperations = true,
        SupportsConditionalOperations = true,
        SupportsPartialUpdates = true,
        RequiresGeometryValidation = false
    };
}

public sealed record TransactionSemantics
{
    public bool SupportsAtomicTransactions { get; init; } = true;

    public bool SupportsRollbackOnFailure { get; init; } = true;

    public bool AllowsPartialSuccess { get; init; }

    public bool RequiresOrderedExecution { get; init; }

    public bool SupportsTransactionHandles { get; init; }

    public TransactionIsolationLevel DefaultIsolationLevel { get; init; } = TransactionIsolationLevel.ReadCommitted;

    public static TransactionSemantics OgcFeatures => new()
    {
        SupportsAtomicTransactions = false, // Individual operations
        SupportsRollbackOnFailure = false,
        AllowsPartialSuccess = true,
        RequiresOrderedExecution = false,
        SupportsTransactionHandles = false,
        DefaultIsolationLevel = TransactionIsolationLevel.ReadCommitted
    };

    public static TransactionSemantics GeoServices => new()
    {
        SupportsAtomicTransactions = true,
        SupportsRollbackOnFailure = true,
        AllowsPartialSuccess = true,
        RequiresOrderedExecution = false,
        SupportsTransactionHandles = false,
        DefaultIsolationLevel = TransactionIsolationLevel.ReadCommitted
    };

    public static TransactionSemantics Wfs20 => new()
    {
        SupportsAtomicTransactions = true,
        SupportsRollbackOnFailure = true,
        AllowsPartialSuccess = false,
        RequiresOrderedExecution = true,
        SupportsTransactionHandles = true,
        DefaultIsolationLevel = TransactionIsolationLevel.Serializable
    };

    public static TransactionSemantics OData => new()
    {
        SupportsAtomicTransactions = true,
        SupportsRollbackOnFailure = true,
        AllowsPartialSuccess = false,
        RequiresOrderedExecution = false,
        SupportsTransactionHandles = false,
        DefaultIsolationLevel = TransactionIsolationLevel.ReadCommitted
    };
}

public enum TransactionIsolationLevel
{
    ReadUncommitted,

    ReadCommitted,

    RepeatableRead,

    Serializable
}
