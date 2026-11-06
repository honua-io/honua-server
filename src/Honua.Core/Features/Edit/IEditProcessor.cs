// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

public interface IEditProcessor
{
    EditValidationResult ValidateEdit(UnifiedEditRequest editRequest, MetadataV2Resource resource);

    UnifiedEditRequest OptimizeEdit(UnifiedEditRequest editRequest, MetadataV2Resource resource);

    FeatureEditBatch ToFeatureEditBatch(UnifiedEditRequest editRequest, MetadataV2Resource resource);

    TransactionValidationResult ValidateTransaction(EditTransaction transaction, MetadataV2Resource resource);

    EditExecutionStrategy DetermineExecutionStrategy(UnifiedEditRequest editRequest, MetadataV2Resource resource);

    Task<EditPerformanceEstimate> EstimatePerformanceAsync(
        UnifiedEditRequest editRequest,
        MetadataV2Resource resource,
        CancellationToken cancellationToken);
}

public sealed record EditValidationResult
{
    public required bool IsValid { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string>? Warnings { get; init; }

    public IDictionary<string, object>? ValidationDetails { get; init; }

    public static EditValidationResult Success(
        IReadOnlyList<string>? warnings = null,
        IDictionary<string, object>? validationDetails = null)
        => new() { IsValid = true, Warnings = warnings, ValidationDetails = validationDetails };

    public static EditValidationResult Failure(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage };
}

public sealed record TransactionValidationResult
{
    public required bool IsValid { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string>? Warnings { get; init; }

    public static TransactionValidationResult Success(IReadOnlyList<string>? warnings = null)
        => new() { IsValid = true, Warnings = warnings };

    public static TransactionValidationResult Failure(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage };
}

public sealed record EditExecutionStrategy
{
    public bool UseTransaction { get; init; } = true;

    public bool EnableRollback { get; init; } = true;

    public bool ValidateBeforeExecution { get; init; } = true;

    public bool EnableParallelProcessing { get; init; }

    public int? RecommendedBatchSize { get; init; }

    public IDictionary<string, object>? StrategyHints { get; init; }
}

public sealed record EditPerformanceEstimate
{
    public long EstimatedExecutionTimeMs { get; init; }

    public long EstimatedMemoryUsageBytes { get; init; }

    public int EstimatedDatabaseOperations { get; init; }

    public EditRiskLevel RiskLevel { get; init; }

    public IReadOnlyList<string>? Recommendations { get; init; }
}

public enum EditRiskLevel
{
    Low,

    Medium,

    High
}
