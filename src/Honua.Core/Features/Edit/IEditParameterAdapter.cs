// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Protocol-specific adapter for converting protocol edit requests to unified edit model.
/// </summary>
/// <typeparam name="TProtocolEditRequest">Protocol-specific edit request type</typeparam>
public interface IEditParameterAdapter<in TProtocolEditRequest>
{
    /// <summary>
    /// Converts protocol-specific edit request to a unified edit request.
    /// </summary>
    /// <param name="protocolRequest">Protocol-specific edit request</param>
    /// <param name="layer">Target layer definition</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unified edit request or error result</returns>
    Task<EditAdapterResult> ConvertAsync(
        TProtocolEditRequest protocolRequest,
        LayerDefinition layer,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the protocol name for this adapter.
    /// </summary>
    string ProtocolName { get; }

    /// <summary>
    /// Gets the default edit limits for this protocol.
    /// </summary>
    ProtocolEditLimits DefaultLimits { get; }

    /// <summary>
    /// Gets the supported transaction semantics for this protocol.
    /// </summary>
    TransactionSemantics TransactionSemantics { get; }
}

/// <summary>
/// Result of protocol edit request conversion to unified edit model.
/// </summary>
public sealed record EditAdapterResult
{
    /// <summary>
    /// Whether the conversion succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Unified edit request if conversion succeeded.
    /// </summary>
    public UnifiedEditRequest? EditRequest { get; init; }

    /// <summary>
    /// Edit transaction if conversion succeeded.
    /// </summary>
    public EditTransaction? Transaction { get; init; }

    /// <summary>
    /// Error message if conversion failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Protocol-specific metadata that may be needed for response formatting.
    /// </summary>
    public IDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Creates a successful conversion result.
    /// </summary>
    /// <param name="editRequest">Unified edit request</param>
    /// <param name="transaction">Edit transaction</param>
    /// <param name="metadata">Optional protocol-specific metadata</param>
    /// <returns>Successful result</returns>
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

    /// <summary>
    /// Creates a failed conversion result.
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <returns>Failed result</returns>
    public static EditAdapterResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Protocol-specific edit limits and constraints.
/// </summary>
public sealed record ProtocolEditLimits
{
    /// <summary>
    /// Maximum number of features per edit operation.
    /// </summary>
    public int MaxFeaturesPerEdit { get; init; } = 1000;

    /// <summary>
    /// Maximum number of edits per transaction.
    /// </summary>
    public int MaxEditsPerTransaction { get; init; } = 5000;

    /// <summary>
    /// Maximum size of feature payload in bytes.
    /// </summary>
    public long MaxFeaturePayloadBytes { get; init; } = 10_000_000; // 10MB

    /// <summary>
    /// Maximum number of attributes per feature.
    /// </summary>
    public int MaxAttributesPerFeature { get; init; } = 100;

    /// <summary>
    /// Whether batch operations are supported.
    /// </summary>
    public bool SupportsBatchOperations { get; init; } = true;

    /// <summary>
    /// Whether conditional operations (ETags, If-Match) are supported.
    /// </summary>
    public bool SupportsConditionalOperations { get; init; } = false;

    /// <summary>
    /// Whether partial updates (PATCH semantics) are supported.
    /// </summary>
    public bool SupportsPartialUpdates { get; init; } = false;

    /// <summary>
    /// Whether geometric validation is required.
    /// </summary>
    public bool RequiresGeometryValidation { get; init; } = true;

    /// <summary>
    /// Standard limits for OGC API Features.
    /// </summary>
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

    /// <summary>
    /// Standard limits for GeoServices (ESRI).
    /// </summary>
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

    /// <summary>
    /// Standard limits for WFS 2.0.
    /// </summary>
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

    /// <summary>
    /// Standard limits for OData.
    /// </summary>
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

/// <summary>
/// Transaction semantics supported by different protocols.
/// </summary>
public sealed record TransactionSemantics
{
    /// <summary>
    /// Whether atomic transactions are supported (all or nothing).
    /// </summary>
    public bool SupportsAtomicTransactions { get; init; } = true;

    /// <summary>
    /// Whether rollback on failure is supported.
    /// </summary>
    public bool SupportsRollbackOnFailure { get; init; } = true;

    /// <summary>
    /// Whether partial success is allowed (some operations succeed, others fail).
    /// </summary>
    public bool AllowsPartialSuccess { get; init; } = false;

    /// <summary>
    /// Whether operation ordering must be preserved.
    /// </summary>
    public bool RequiresOrderedExecution { get; init; } = false;

    /// <summary>
    /// Whether the protocol supports transaction handles/IDs.
    /// </summary>
    public bool SupportsTransactionHandles { get; init; } = false;

    /// <summary>
    /// Default transaction isolation level.
    /// </summary>
    public TransactionIsolationLevel DefaultIsolationLevel { get; init; } = TransactionIsolationLevel.ReadCommitted;

    /// <summary>
    /// OGC API Features transaction semantics.
    /// </summary>
    public static TransactionSemantics OgcFeatures => new()
    {
        SupportsAtomicTransactions = false, // Individual operations
        SupportsRollbackOnFailure = false,
        AllowsPartialSuccess = true,
        RequiresOrderedExecution = false,
        SupportsTransactionHandles = false,
        DefaultIsolationLevel = TransactionIsolationLevel.ReadCommitted
    };

    /// <summary>
    /// GeoServices transaction semantics.
    /// </summary>
    public static TransactionSemantics GeoServices => new()
    {
        SupportsAtomicTransactions = true,
        SupportsRollbackOnFailure = true,
        AllowsPartialSuccess = true,
        RequiresOrderedExecution = false,
        SupportsTransactionHandles = false,
        DefaultIsolationLevel = TransactionIsolationLevel.ReadCommitted
    };

    /// <summary>
    /// WFS 2.0 transaction semantics.
    /// </summary>
    public static TransactionSemantics Wfs20 => new()
    {
        SupportsAtomicTransactions = true,
        SupportsRollbackOnFailure = true,
        AllowsPartialSuccess = false,
        RequiresOrderedExecution = true,
        SupportsTransactionHandles = true,
        DefaultIsolationLevel = TransactionIsolationLevel.Serializable
    };

    /// <summary>
    /// OData transaction semantics.
    /// </summary>
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

/// <summary>
/// Transaction isolation levels.
/// </summary>
public enum TransactionIsolationLevel
{
    /// <summary>
    /// Read uncommitted isolation level.
    /// </summary>
    ReadUncommitted,

    /// <summary>
    /// Read committed isolation level.
    /// </summary>
    ReadCommitted,

    /// <summary>
    /// Repeatable read isolation level.
    /// </summary>
    RepeatableRead,

    /// <summary>
    /// Serializable isolation level.
    /// </summary>
    Serializable
}