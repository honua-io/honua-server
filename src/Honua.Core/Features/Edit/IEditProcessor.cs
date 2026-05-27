// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Unified edit processor that handles edit validation and processing across all protocols.
/// Provides shared edit validation, transaction management, and execution logic.
/// </summary>
public interface IEditProcessor
{
    /// <summary>
    /// Validates a unified edit request against resource constraints and protocol limits.
    /// </summary>
    /// <param name="editRequest">Unified edit request to validate</param>
    /// <param name="resource">Target metadata resource</param>
    /// <returns>Validation result with any errors</returns>
    EditValidationResult ValidateEdit(UnifiedEditRequest editRequest, MetadataV2Resource resource);

    /// <summary>
    /// Optimizes a unified edit request for efficient execution.
    /// </summary>
    /// <param name="editRequest">Edit request to optimize</param>
    /// <param name="resource">Target metadata resource</param>
    /// <returns>Optimized edit request</returns>
    UnifiedEditRequest OptimizeEdit(UnifiedEditRequest editRequest, MetadataV2Resource resource);

    /// <summary>
    /// Converts a unified edit request to a FeatureEditBatch for data access.
    /// </summary>
    /// <param name="editRequest">Unified edit request</param>
    /// <param name="resource">Target metadata resource</param>
    /// <returns>Feature edit batch for data access layer</returns>
    FeatureEditBatch ToFeatureEditBatch(UnifiedEditRequest editRequest, MetadataV2Resource resource);

    /// <summary>
    /// Validates edit transaction semantics and constraints.
    /// </summary>
    /// <param name="transaction">Edit transaction to validate</param>
    /// <param name="resource">Target metadata resource</param>
    /// <returns>Transaction validation result</returns>
    TransactionValidationResult ValidateTransaction(EditTransaction transaction, MetadataV2Resource resource);

    /// <summary>
    /// Determines edit execution strategy based on request characteristics.
    /// </summary>
    /// <param name="editRequest">Unified edit request</param>
    /// <param name="resource">Target metadata resource</param>
    /// <returns>Execution strategy</returns>
    EditExecutionStrategy DetermineExecutionStrategy(UnifiedEditRequest editRequest, MetadataV2Resource resource);

    /// <summary>
    /// Estimates the performance impact of the edit operation.
    /// </summary>
    /// <param name="editRequest">Unified edit request</param>
    /// <param name="resource">Target metadata resource</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Performance estimate</returns>
    Task<EditPerformanceEstimate> EstimatePerformanceAsync(
        UnifiedEditRequest editRequest,
        MetadataV2Resource resource,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of edit validation.
/// </summary>
public sealed record EditValidationResult
{
    /// <summary>
    /// Whether the edit request is valid.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Error message if validation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Validation warnings that don't prevent execution.
    /// </summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>
    /// Protocol-specific validation details.
    /// </summary>
    public IDictionary<string, object>? ValidationDetails { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    /// <param name="warnings">Optional warnings</param>
    /// <param name="validationDetails">Optional validation details</param>
    /// <returns>Successful validation result</returns>
    public static EditValidationResult Success(
        IReadOnlyList<string>? warnings = null,
        IDictionary<string, object>? validationDetails = null)
        => new() { IsValid = true, Warnings = warnings, ValidationDetails = validationDetails };

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <returns>Failed validation result</returns>
    public static EditValidationResult Failure(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of transaction validation.
/// </summary>
public sealed record TransactionValidationResult
{
    /// <summary>
    /// Whether the transaction is valid.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Error message if validation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Transaction warnings.
    /// </summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>
    /// Creates a successful transaction validation result.
    /// </summary>
    /// <param name="warnings">Optional warnings</param>
    /// <returns>Successful validation result</returns>
    public static TransactionValidationResult Success(IReadOnlyList<string>? warnings = null)
        => new() { IsValid = true, Warnings = warnings };

    /// <summary>
    /// Creates a failed transaction validation result.
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <returns>Failed validation result</returns>
    public static TransactionValidationResult Failure(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Edit execution strategy information.
/// </summary>
public sealed record EditExecutionStrategy
{
    /// <summary>
    /// Whether to use transaction isolation.
    /// </summary>
    public bool UseTransaction { get; init; } = true;

    /// <summary>
    /// Whether to enable rollback on failure.
    /// </summary>
    public bool EnableRollback { get; init; } = true;

    /// <summary>
    /// Whether to validate before execution.
    /// </summary>
    public bool ValidateBeforeExecution { get; init; } = true;

    /// <summary>
    /// Whether to enable parallel processing for independent operations.
    /// </summary>
    public bool EnableParallelProcessing { get; init; }

    /// <summary>
    /// Recommended batch size for large edit operations.
    /// </summary>
    public int? RecommendedBatchSize { get; init; }

    /// <summary>
    /// Additional strategy hints.
    /// </summary>
    public IDictionary<string, object>? StrategyHints { get; init; }
}

/// <summary>
/// Edit performance estimation.
/// </summary>
public sealed record EditPerformanceEstimate
{
    /// <summary>
    /// Estimated execution time in milliseconds.
    /// </summary>
    public long EstimatedExecutionTimeMs { get; init; }

    /// <summary>
    /// Estimated memory usage in bytes.
    /// </summary>
    public long EstimatedMemoryUsageBytes { get; init; }

    /// <summary>
    /// Estimated database operations count.
    /// </summary>
    public int EstimatedDatabaseOperations { get; init; }

    /// <summary>
    /// Risk level for the operation.
    /// </summary>
    public EditRiskLevel RiskLevel { get; init; }

    /// <summary>
    /// Performance recommendations.
    /// </summary>
    public IReadOnlyList<string>? Recommendations { get; init; }
}

/// <summary>
/// Risk level for edit operations.
/// </summary>
public enum EditRiskLevel
{
    /// <summary>
    /// Low risk operation.
    /// </summary>
    Low,

    /// <summary>
    /// Medium risk operation.
    /// </summary>
    Medium,

    /// <summary>
    /// High risk operation - may require special handling.
    /// </summary>
    High
}
