// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Default implementation of unified edit processor.
/// Handles edit validation, optimization, and conversion across all protocols.
/// </summary>
public sealed class EditProcessor : IEditProcessor
{
    private readonly ILogger<EditProcessor> _logger;

    public EditProcessor(ILogger<EditProcessor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public EditValidationResult ValidateEdit(UnifiedEditRequest editRequest, LayerDefinition layer)
    {
        try
        {
            var warnings = new List<string>();

            // Check if edit request is empty
            if (editRequest.IsEmpty)
            {
                return EditValidationResult.Failure("Edit request cannot be empty");
            }

            // Validate operation counts against layer limits
            var totalOperations = editRequest.TotalOperations;
            if (totalOperations > GetMaxOperationsForLayer(layer))
            {
                return EditValidationResult.Failure(
                    $"Too many operations ({totalOperations}). Maximum allowed: {GetMaxOperationsForLayer(layer)}");
            }

            // Validate individual operations
            if (editRequest.Creates?.IsDefaultOrEmpty == false)
            {
                var createValidation = ValidateCreateOperations(editRequest.Creates.Value, layer);
                if (!createValidation.IsValid)
                {
                    return createValidation;
                }
                warnings.AddRange(createValidation.Warnings ?? []);
            }

            if (editRequest.Updates?.IsDefaultOrEmpty == false)
            {
                var updateValidation = ValidateUpdateOperations(editRequest.Updates.Value, layer);
                if (!updateValidation.IsValid)
                {
                    return updateValidation;
                }
                warnings.AddRange(updateValidation.Warnings ?? []);
            }

            if (editRequest.Deletes?.IsDefaultOrEmpty == false)
            {
                var deleteValidation = ValidateDeleteOperations(editRequest.Deletes.Value, layer);
                if (!deleteValidation.IsValid)
                {
                    return deleteValidation;
                }
                warnings.AddRange(deleteValidation.Warnings ?? []);
            }

            if (editRequest.Operations?.IsDefaultOrEmpty == false)
            {
                var operationsValidation = ValidateOrderedOperations(editRequest.Operations.Value, layer);
                if (!operationsValidation.IsValid)
                {
                    return operationsValidation;
                }
                warnings.AddRange(operationsValidation.Warnings ?? []);
            }

            // Validate transaction constraints
            if (editRequest.TransactionOptions.HasValue)
            {
                var transactionValidation = ValidateTransactionOptions(editRequest.TransactionOptions.Value, layer);
                if (!transactionValidation.IsValid)
                {
                    return transactionValidation;
                }
                warnings.AddRange(transactionValidation.Warnings ?? []);
            }

            return EditValidationResult.Success(warnings.Count > 0 ? warnings : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating edit request for layer {LayerId}", layer.Id);
            return EditValidationResult.Failure("Failed to validate edit request");
        }
    }

    public UnifiedEditRequest OptimizeEdit(UnifiedEditRequest editRequest, LayerDefinition layer)
    {
        try
        {
            var optimizedRequest = editRequest;

            // Optimize geometry handling
            optimizedRequest = OptimizeGeometryOperations(optimizedRequest, layer);

            // Optimize attribute operations
            optimizedRequest = OptimizeAttributeOperations(optimizedRequest, layer);

            // Optimize operation ordering
            optimizedRequest = OptimizeOperationOrder(optimizedRequest, layer);

            // Add performance hints
            optimizedRequest = AddPerformanceHints(optimizedRequest, layer);

            _logger.LogDebug("Optimized edit request for layer {LayerId}: {OriginalOps} -> {OptimizedOps} operations",
                layer.Id, editRequest.TotalOperations, optimizedRequest.TotalOperations);

            return optimizedRequest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to optimize edit request for layer {LayerId}, returning original",
                layer.Id);
            return editRequest;
        }
    }

    public FeatureEditBatch ToFeatureEditBatch(UnifiedEditRequest editRequest, LayerDefinition layer)
    {
        try
        {
            var creates = ConvertToFeatures(editRequest.Creates, layer, isCreate: true);
            var updates = ConvertToFeatures(editRequest.Updates, layer, isCreate: false);
            var deletes = editRequest.Deletes ?? ImmutableArray<long>.Empty;

            var rollbackOnFailure = editRequest.TransactionOptions?.RollbackOnFailure ?? false;
            var useGlobalIds = HasGlobalIds(editRequest);

            // Handle ordered operations if present
            if (editRequest.Operations?.IsDefaultOrEmpty == false)
            {
                var operations = ConvertToFeatureOperations(editRequest.Operations.Value, layer);
                return FeatureEditBatch.Create(
                    operations: operations,
                    rollbackOnFailure: rollbackOnFailure,
                    useGlobalIds: useGlobalIds);
            }

            return FeatureEditBatch.Create(
                creates: creates,
                updates: updates,
                deletes: deletes,
                rollbackOnFailure: rollbackOnFailure,
                useGlobalIds: useGlobalIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting unified edit request to feature edit batch for layer {LayerId}",
                layer.Id);
            throw;
        }
    }

    public TransactionValidationResult ValidateTransaction(EditTransaction transaction, LayerDefinition layer)
    {
        try
        {
            var warnings = new List<string>();

            // Validate transaction state
            if (transaction.State != TransactionState.Pending)
            {
                return TransactionValidationResult.Failure(
                    $"Transaction is in invalid state: {transaction.State}");
            }

            // Validate transaction configuration
            if (transaction.Configuration.TimeoutMs <= 0)
            {
                return TransactionValidationResult.Failure("Transaction timeout must be positive");
            }

            if (transaction.Configuration.MaxBatchSize <= 0)
            {
                return TransactionValidationResult.Failure("Transaction batch size must be positive");
            }

            // Validate total operations across all requests
            var totalOperations = transaction.TotalOperations;
            var maxOperations = transaction.Configuration.MaxBatchSize * 10; // Conservative limit

            if (totalOperations > maxOperations)
            {
                return TransactionValidationResult.Failure(
                    $"Transaction has too many operations ({totalOperations}). Maximum: {maxOperations}");
            }

            // Validate individual edit requests
            foreach (var editRequest in transaction.EditRequests)
            {
                var editValidation = ValidateEdit(editRequest, layer);
                if (!editValidation.IsValid)
                {
                    return TransactionValidationResult.Failure(
                        $"Invalid edit request in transaction: {editValidation.ErrorMessage}");
                }

                if (editValidation.Warnings != null)
                {
                    warnings.AddRange(editValidation.Warnings.Select(w => $"Edit request warning: {w}"));
                }
            }

            // Validate compatibility with layer capabilities
            var compatibilityValidation = ValidateLayerCompatibility(transaction, layer);
            if (!compatibilityValidation.IsValid)
            {
                return compatibilityValidation;
            }

            if (compatibilityValidation.Warnings != null)
            {
                warnings.AddRange(compatibilityValidation.Warnings);
            }

            return TransactionValidationResult.Success(warnings.Count > 0 ? warnings : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating transaction {TransactionId} for layer {LayerId}",
                transaction.TransactionId, layer.Id);
            return TransactionValidationResult.Failure("Failed to validate transaction");
        }
    }

    public EditExecutionStrategy DetermineExecutionStrategy(UnifiedEditRequest editRequest, LayerDefinition layer)
    {
        var totalOperations = editRequest.TotalOperations;
        var hasTransaction = editRequest.RequiresTransaction;

        // Determine batch size
        var recommendedBatchSize = totalOperations switch
        {
            <= 100 => (int?)null, // Execute in single batch
            <= 1000 => 100,
            <= 5000 => 250,
            _ => 500
        };

        // Determine parallelization
        var enableParallel = !hasTransaction && totalOperations > 50;

        // Determine validation strategy
        var validateBeforeExecution = editRequest.ValidationOptions?.Mode != ValidationMode.Skip;

        return new EditExecutionStrategy
        {
            UseTransaction = hasTransaction,
            EnableRollback = editRequest.TransactionOptions?.RollbackOnFailure ?? false,
            ValidateBeforeExecution = validateBeforeExecution,
            EnableParallelProcessing = enableParallel,
            RecommendedBatchSize = recommendedBatchSize
        };
    }

    public async Task<EditPerformanceEstimate> EstimatePerformanceAsync(
        UnifiedEditRequest editRequest,
        LayerDefinition layer,
        CancellationToken cancellationToken)
    {
        try
        {
            var totalOperations = editRequest.TotalOperations;

            // Estimate execution time (rough heuristics)
            var baseTimePerOperation = 10; // 10ms base time per operation
            var geometryPenalty = HasComplexGeometry(editRequest) ? 5 : 0;
            var transactionPenalty = editRequest.RequiresTransaction ? 100 : 0;

            var estimatedTimeMs = (totalOperations * (baseTimePerOperation + geometryPenalty)) + transactionPenalty;

            // Estimate memory usage
            var baseMemoryPerOperation = 1024; // 1KB base memory per operation
            var geometryMemoryPenalty = HasComplexGeometry(editRequest) ? 4096 : 0;
            var estimatedMemoryBytes = totalOperations * (baseMemoryPerOperation + geometryMemoryPenalty);

            // Estimate database operations
            var dbOperations = totalOperations + (editRequest.RequiresTransaction ? 2 : 0); // +2 for begin/commit

            // Determine risk level
            var riskLevel = totalOperations switch
            {
                <= 100 => EditRiskLevel.Low,
                <= 1000 => EditRiskLevel.Medium,
                _ => EditRiskLevel.High
            };

            var recommendations = new List<string>();
            if (totalOperations > 1000)
            {
                recommendations.Add("Consider batching operations for better performance");
            }
            if (HasComplexGeometry(editRequest))
            {
                recommendations.Add("Complex geometries detected - consider geometry simplification");
            }
            if (editRequest.RequiresTransaction && totalOperations > 500)
            {
                recommendations.Add("Large transaction detected - monitor for lock contention");
            }

            return new EditPerformanceEstimate
            {
                EstimatedExecutionTimeMs = estimatedTimeMs,
                EstimatedMemoryUsageBytes = estimatedMemoryBytes,
                EstimatedDatabaseOperations = dbOperations,
                RiskLevel = riskLevel,
                Recommendations = recommendations.Count > 0 ? recommendations : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to estimate performance for edit request on layer {LayerId}", layer.Id);
            throw;
        }
    }

    private EditValidationResult ValidateCreateOperations(
        ImmutableArray<EditFeature> creates,
        LayerDefinition layer)
    {
        var warnings = new List<string>();

        foreach (var feature in creates)
        {
            if (feature.ObjectId.HasValue)
            {
                warnings.Add("Object ID specified for create operation will be ignored");
            }

            // Validate attributes exist if required
            if (feature.Attributes?.IsEmpty != false && RequiresAttributes(layer))
            {
                return EditValidationResult.Failure("Attributes required for create operation");
            }

            // Validate geometry if layer requires it
            if (feature.Geometry == null && RequiresGeometry(layer))
            {
                return EditValidationResult.Failure("Geometry required for create operation");
            }
        }

        return EditValidationResult.Success(warnings.Count > 0 ? warnings : null);
    }

    private EditValidationResult ValidateUpdateOperations(
        ImmutableArray<EditFeature> updates,
        LayerDefinition layer)
    {
        foreach (var feature in updates)
        {
            if (!feature.ObjectId.HasValue)
            {
                return EditValidationResult.Failure("Object ID required for update operation");
            }

            if (feature.ObjectId.Value <= 0)
            {
                return EditValidationResult.Failure("Invalid Object ID for update operation");
            }
        }

        return EditValidationResult.Success();
    }

    private EditValidationResult ValidateDeleteOperations(
        ImmutableArray<long> deletes,
        LayerDefinition layer)
    {
        foreach (var objectId in deletes)
        {
            if (objectId <= 0)
            {
                return EditValidationResult.Failure("Invalid Object ID for delete operation");
            }
        }

        return EditValidationResult.Success();
    }

    private EditValidationResult ValidateOrderedOperations(
        ImmutableArray<UnifiedEditOperation> operations,
        LayerDefinition layer)
    {
        foreach (var operation in operations)
        {
            switch (operation.Type)
            {
                case EditOperationType.Create:
                    if (operation.Feature == null)
                    {
                        return EditValidationResult.Failure("Feature required for create operation");
                    }
                    break;
                case EditOperationType.Update:
                    if (operation.Feature == null || !operation.Feature.Value.ObjectId.HasValue)
                    {
                        return EditValidationResult.Failure("Feature with Object ID required for update operation");
                    }
                    break;
                case EditOperationType.Delete:
                    if (!operation.ObjectId.HasValue || operation.ObjectId.Value <= 0)
                    {
                        return EditValidationResult.Failure("Valid Object ID required for delete operation");
                    }
                    break;
            }
        }

        return EditValidationResult.Success();
    }

    private EditValidationResult ValidateTransactionOptions(
        EditTransactionOptions options,
        LayerDefinition layer)
    {
        var warnings = new List<string>();

        if (options.TimeoutMs.HasValue && options.TimeoutMs.Value <= 0)
        {
            return EditValidationResult.Failure("Transaction timeout must be positive");
        }

        if (options.TimeoutMs.HasValue && options.TimeoutMs.Value > 3_600_000) // 1 hour
        {
            warnings.Add("Transaction timeout is very long (> 1 hour)");
        }

        return EditValidationResult.Success(warnings.Count > 0 ? warnings : null);
    }

    private TransactionValidationResult ValidateLayerCompatibility(EditTransaction transaction, LayerDefinition layer)
    {
        var warnings = new List<string>();

        // Check for protocol-specific constraints
        if (transaction.Protocol == "WFS20" && !transaction.Configuration.RollbackOnFailure)
        {
            warnings.Add("WFS 2.0 typically requires rollback on failure for transaction semantics");
        }

        if (transaction.Protocol == "OData" && transaction.Configuration.EnableParallelExecution)
        {
            warnings.Add("OData may not benefit from parallel execution due to protocol constraints");
        }

        // Check layer-specific constraints
        // Note: For now we assume all layers support transactions
        // In the future, this could be checked via layer.Metadata or a dedicated capabilities system

        return TransactionValidationResult.Success(warnings.Count > 0 ? warnings : null);
    }

    private UnifiedEditRequest OptimizeGeometryOperations(UnifiedEditRequest request, LayerDefinition layer)
    {
        // Implementation would include geometry simplification, validation caching, etc.
        return request;
    }

    private UnifiedEditRequest OptimizeAttributeOperations(UnifiedEditRequest request, LayerDefinition layer)
    {
        // Implementation would include attribute validation caching, default value handling, etc.
        return request;
    }

    private UnifiedEditRequest OptimizeOperationOrder(UnifiedEditRequest request, LayerDefinition layer)
    {
        // Implementation would include reordering operations for optimal database performance
        return request;
    }

    private UnifiedEditRequest AddPerformanceHints(UnifiedEditRequest request, LayerDefinition layer)
    {
        // Implementation would add hints for query optimization, caching, etc.
        return request;
    }

    private ImmutableArray<Feature> ConvertToFeatures(
        ImmutableArray<EditFeature>? editFeatures,
        LayerDefinition layer,
        bool isCreate)
    {
        if (editFeatures?.IsDefaultOrEmpty != false)
        {
            return ImmutableArray<Feature>.Empty;
        }

        var features = new List<Feature>();
        foreach (var editFeature in editFeatures)
        {
            var objectId = isCreate ? 0 : (editFeature.ObjectId ?? 0);
            var attributes = editFeature.Attributes ?? ImmutableDictionary<string, object?>.Empty;

            features.Add(Feature.Create(objectId, editFeature.Geometry, attributes));
        }

        return features.ToImmutableArray();
    }

    private ImmutableArray<FeatureEditOperation> ConvertToFeatureOperations(
        ImmutableArray<UnifiedEditOperation> operations,
        LayerDefinition layer)
    {
        var featureOperations = new List<FeatureEditOperation>();

        foreach (var operation in operations)
        {
            switch (operation.Type)
            {
                case EditOperationType.Create:
                    if (operation.Feature.HasValue)
                    {
                        var feature = ConvertEditFeatureToFeature(operation.Feature.Value, layer, isCreate: true);
                        featureOperations.Add(FeatureEditOperation.Create(feature));
                    }
                    break;

                case EditOperationType.Update:
                    if (operation.Feature.HasValue)
                    {
                        var feature = ConvertEditFeatureToFeature(operation.Feature.Value, layer, isCreate: false);
                        featureOperations.Add(FeatureEditOperation.Update(feature));
                    }
                    break;

                case EditOperationType.Delete:
                    if (operation.ObjectId.HasValue)
                    {
                        featureOperations.Add(FeatureEditOperation.Delete(operation.ObjectId.Value));
                    }
                    break;
            }
        }

        return featureOperations.ToImmutableArray();
    }

    private Feature ConvertEditFeatureToFeature(EditFeature editFeature, LayerDefinition layer, bool isCreate)
    {
        var objectId = isCreate ? 0 : (editFeature.ObjectId ?? 0);
        var attributes = editFeature.Attributes ?? ImmutableDictionary<string, object?>.Empty;
        return Feature.Create(objectId, editFeature.Geometry, attributes);
    }

    private bool HasGlobalIds(UnifiedEditRequest request)
    {
        if (request.Creates?.IsDefaultOrEmpty == false)
        {
            return request.Creates.Value.Any(f => !string.IsNullOrEmpty(f.GlobalId));
        }

        if (request.Updates?.IsDefaultOrEmpty == false)
        {
            return request.Updates.Value.Any(f => !string.IsNullOrEmpty(f.GlobalId));
        }

        return false;
    }

    private bool HasComplexGeometry(UnifiedEditRequest request)
    {
        // Simplified check - in reality would analyze geometry complexity
        var hasGeometry = false;

        if (request.Creates?.IsDefaultOrEmpty == false)
        {
            hasGeometry = request.Creates.Value.Any(f => f.Geometry != null);
        }

        if (request.Updates?.IsDefaultOrEmpty == false)
        {
            hasGeometry = hasGeometry || request.Updates.Value.Any(f => f.Geometry != null);
        }

        return hasGeometry;
    }

    private int GetMaxOperationsForLayer(LayerDefinition layer)
    {
        // In reality, this would check layer configuration or capabilities
        return 10000; // Default maximum operations per layer
    }

    private bool RequiresAttributes(LayerDefinition layer)
    {
        // Check if layer has non-nullable attribute fields without default values
        return layer.AttributeFields.Any(f => !f.Nullable && f.DefaultValue == null);
    }

    private bool RequiresGeometry(LayerDefinition layer)
    {
        return layer.GeometryType != GeometryType.None;
    }
}